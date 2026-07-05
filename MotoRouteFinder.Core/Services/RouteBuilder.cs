using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Itinero.Profiles;
using Microsoft.Extensions.Options;
using MotoRouteFinder.Helpers;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Services;

/// <summary>
/// Builds route geometry by progressively adding segments.
/// </summary>
public class RouteBuilder
{
    private readonly MapRepository _mapRepository;
    private readonly RoadClassifier _roadClassifier;
    private readonly RouteAssembler _routeAssembler;
    private readonly WaypointGenerator _waypointGenerator;
    private readonly RouteStatistics _routeStatistics;
    private readonly DiagnosticsCollector _diagnostics;
    private readonly EdgeBlocker _edgeBlocker;
    private readonly RouteGenerationOptions _options;

    // Reusable buffers for per-segment edge tracking to avoid allocating
    // a new HashSet + List on every segment (~30x per route attempt).
    private readonly HashSet<RouteGeometryUtils.EdgeKey> _segmentEdgeBuffer = new();
    private readonly Dictionary<RouteGeometryUtils.EdgeKey, int> _overlapEdgeCounts = new();
    private readonly RouteGeometryUtils.EdgeSpatialIndex _edgeSpatialIndex = new();

    private const double HeadingAdjustmentAngle = 1.2566; // PI/2.5 = 72°
    private const double WaypointRadiusMinFraction = 0.5;
    private const double WaypointRadiusRangeFraction = 0.6;
    private const int SectorSizeDegrees = 20;

    private record ForwardLoopConfig(
        IProfileInstance Profile,
        Coordinate Start,
        double TargetDistKm,
        int AdaptiveWaypointCount,
        bool AvoidHighways,
        int AttemptNumber,
        double Radius,
        double CosLat,
        double AngularOffset,
        double AngleStep,
        int MaxFailures,
        HashSet<int>? AvoidSectors,
        CancellationToken CancellationToken
    );

    private class ForwardLoopState
    {
        public Coordinate CurrentPos = default!;
        public int SegmentCounter;
        public double TotalDistKm;
        public int CommittedCount;
        public double LastEstReturnHaversineKm;
        public double LastEstReturnWithMultiplierKm;
    }

    public RouteBuilder(
        MapRepository mapRepository,
        RoadClassifier roadClassifier,
        RouteAssembler routeAssembler,
        WaypointGenerator waypointGenerator,
        RouteStatistics routeStatistics,
        DiagnosticsCollector diagnostics,
        EdgeBlocker edgeBlocker,
        IOptions<RouteGenerationOptions>? options = null)
    {
        _mapRepository = mapRepository;
        _roadClassifier = roadClassifier;
        _routeAssembler = routeAssembler;
        _waypointGenerator = waypointGenerator;
        _routeStatistics = routeStatistics;
        _diagnostics = diagnostics;
        _edgeBlocker = edgeBlocker;
        _options = options?.Value ?? new RouteGenerationOptions();
    }

    public (List<Coordinate> geometry, List<Coordinate> allPoints, BuildContext context)? BuildProgressiveLoop(
        IProfileInstance profile,
        Coordinate start,
        double targetDistKm,
        int waypointCount,
        bool avoidHighways,
        CancellationToken cancellationToken,
        int attemptNumber = 0,
        HashSet<int>? avoidSectors = null,
        DirectionBias direction = DirectionBias.Any)
    {
        var context = new BuildContext(start);
        context.BuilderMethod = "progressive_loop";
        var result = BuildProgressiveLoopInternal(profile, start, targetDistKm, waypointCount, avoidHighways, cancellationToken, context, attemptNumber, avoidSectors, direction);
        return result != null ? (result.Value.geometry, result.Value.allPoints, context) : null;
    }

    public (List<Coordinate> geometry, List<Coordinate> allPoints, bool needsRegen, Coordinate lastPos)? IterativeRouteLoop(
        IProfileInstance profile,
        Coordinate start,
        List<Coordinate> waypoints,
        bool avoidHighways,
        CancellationToken cancellationToken)
    {
        var currentPos = _roadClassifier.TryResolveToRoadCounted(profile, start, 2000) ?? start;
        var allUsedEdges = new HashSet<RouteGeometryUtils.EdgeKey>();
        var blockedMotorwayEdges = new HashSet<uint>();

        var allSegments = new List<List<Coordinate>>();
        var allPoints = new List<Coordinate> { start };

        var remaining = new List<Coordinate>(waypoints);

        while (remaining.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var next = remaining[0];
            remaining.RemoveAt(0);

            var (seg, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, next, allUsedEdges);
            if (seg == null) continue;

            _diagnostics.Add(new DebugStemEvent
            {
                AttemptNumber = 0,
                SegmentRole = "forward",
                SegmentLengthM = RouteGeometryUtils.CalculateDistance(seg),
                OriginalWaypoint = new[] { next.Lat, next.Lon },
                FinalWaypoint = new[] { next.Lat, next.Lon },
                Resolution = "committed",
            });

            if (avoidHighways)
            {
                seg = RerouteIfMotorwaysBlocked(seg!, profile, currentPos, next, allUsedEdges, blockedMotorwayEdges, avoidHighways);
            }

            _segmentEdgeBuffer.Clear();
            RouteGeometryUtils.ExtractEdgesInto(seg!, _segmentEdgeBuffer);
            allUsedEdges.UnionWith(_segmentEdgeBuffer);

            allSegments.Add(seg);
            allPoints.Add(next);
            currentPos = next;
        }

        if (allSegments.Count == 0) return null;

        var (returnSeg, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, start, allUsedEdges);

        if (returnSeg != null)
        {
            if (avoidHighways)
            {
                returnSeg = RerouteIfMotorwaysBlocked(returnSeg, profile, currentPos, start, allUsedEdges, blockedMotorwayEdges, avoidHighways);
            }
            allSegments.Add(returnSeg);
        }

        allPoints.Add(start);
        var geometry = RouteGeometryUtils.AssembleGeometry(allSegments);
        if (geometry == null || geometry.Count < 2) return null;

        _routeStatistics.ReturnToStartIndex = RouteGeometryUtils.FindLoopMidpoint(geometry, start);

        return (geometry, allPoints, needsRegen: false, start);
    }

    private (List<Coordinate> geometry, List<Coordinate> allPoints)? BuildProgressiveLoopInternal(
        IProfileInstance profile,
        Coordinate start,
        double targetDistKm,
        int waypointCount,
        bool avoidHighways,
        CancellationToken cancellationToken,
        BuildContext ctx,
        int attemptNumber,
        HashSet<int>? avoidSectors,
        DirectionBias direction = DirectionBias.Any)
    {
        // Adaptive waypoint count: scale with distance (GraphHopper-inspired)
        int adaptiveWaypointCount = Math.Max(waypointCount, 3 + (int)(targetDistKm / 40));

        // M1: Convert compass bearings to math angles for Sin/Cos placement (0=East, CCW)
        double? preferredBearingRad = direction switch
        {
            DirectionBias.North => RouteGeometryUtils.BearingToMathAngle(0),
            DirectionBias.Northeast => RouteGeometryUtils.BearingToMathAngle(45),
            DirectionBias.East => RouteGeometryUtils.BearingToMathAngle(90),
            DirectionBias.Southeast => RouteGeometryUtils.BearingToMathAngle(135),
            DirectionBias.South => RouteGeometryUtils.BearingToMathAngle(180),
            DirectionBias.Southwest => RouteGeometryUtils.BearingToMathAngle(225),
            DirectionBias.West => RouteGeometryUtils.BearingToMathAngle(270),
            DirectionBias.Northwest => RouteGeometryUtils.BearingToMathAngle(315),
            _ => null // Any (no preference)
        };

        _diagnostics.Add(new DebugStemEvent
        {
            AttemptNumber = attemptNumber,
            SegmentRole = "heartbeat",
            Resolution = "pipeline_active",
            OriginalWaypoint = new[] { start.Lat, start.Lon },
        });

        // Initialize shared state
        var currentPos = _roadClassifier.TryResolveToRoadCounted(profile, start, 2000) ?? start;
        var allUsedEdges = new HashSet<RouteGeometryUtils.EdgeKey>();
        _edgeSpatialIndex.Clear();

        var allSegments = new List<List<Coordinate>>();
        var allPoints = new List<Coordinate> { start };
        var edgeAge = new Dictionary<RouteGeometryUtils.EdgeKey, int>();
        int segmentCounter = 0;

        double totalDistKm = 0;
        double radius = targetDistKm / (2 * Math.PI) * 1.4;
        double cosLat = Math.Cos(start.Lat * Math.PI / 180);
        // Bias angular offset toward requested direction when specified.
        // With direction: start from preferred bearing with small random jitter.
        // Without direction: fully random for diversity.
        double angularOffset = preferredBearingRad.HasValue
            ? preferredBearingRad.Value + (Random.Shared.NextDouble() - 0.5) * Math.PI / 6 // ±15° jitter
            : Random.Shared.NextDouble() * 2 * Math.PI;
        double angleStep = 2 * Math.PI / Math.Max(adaptiveWaypointCount, 3);
        int committedCount = 0;
        const int maxFailures = 30;

        _roadClassifier.ResetStats();  // preserve _resolveCache across attempts
        _routeAssembler.ResetCounters();
        _edgeBlocker.ResetCounters();
        _waypointGenerator.ResetCounters();

        double lastEstReturnHaversineKm = 0;
        double lastEstReturnWithMultiplierKm = 0;

        var fwdCfg = new ForwardLoopConfig(profile, start, targetDistKm, adaptiveWaypointCount, avoidHighways,
            attemptNumber, radius, cosLat, angularOffset, angleStep, maxFailures, avoidSectors, cancellationToken);
        var fwdState = new ForwardLoopState
        {
            CurrentPos = currentPos,
            SegmentCounter = segmentCounter,
            TotalDistKm = totalDistKm,
            CommittedCount = committedCount,
            LastEstReturnHaversineKm = lastEstReturnHaversineKm,
            LastEstReturnWithMultiplierKm = lastEstReturnWithMultiplierKm
        };

        // Phase 1: Forward loop
        string loopExitReason = RunForwardLoop(fwdCfg, fwdState, allUsedEdges, allSegments, allPoints, edgeAge, ctx);

        // Sync back to locals
        currentPos = fwdState.CurrentPos;
        segmentCounter = fwdState.SegmentCounter;
        totalDistKm = fwdState.TotalDistKm;
        committedCount = fwdState.CommittedCount;
        lastEstReturnHaversineKm = fwdState.LastEstReturnHaversineKm;
        lastEstReturnWithMultiplierKm = fwdState.LastEstReturnWithMultiplierKm;

        // Post-loop diagnostics
        double forwardDistanceKm = totalDistKm;
        ctx.EstReturnAtLoopExit = lastEstReturnWithMultiplierKm;
        ctx.ForwardHaversineAtExit = lastEstReturnHaversineKm;
        ctx.SectorsBlockedAtReturn = _waypointGenerator.BlockedSectorCounts.Count(c => c > 0);
        ctx.ForwardSegmentCount = committedCount;

        Array.Copy(_waypointGenerator.BlockedSectorCounts, ctx.BlockedSectorCounts, 18);

        if (allSegments.Count < 2) return null;

        // Phase 2: Homing waypoints
        int homingWaypointsUsed = GenerateHomingWaypoints(
            fwdCfg, fwdState, allUsedEdges, allSegments, allPoints, ctx);
        ctx.HomingWaypointsUsed = homingWaypointsUsed;

        // Phase 3: Return segment
        BuildReturnSegment(
            fwdCfg, fwdState, forwardDistanceKm, edgeAge,
            allUsedEdges, allSegments, allPoints, ref loopExitReason, ctx);

        // Phase 4: Assemble final route
        return AssembleFinalRoute(start, allSegments, allPoints, allUsedEdges, ctx);
    }

    private string RunForwardLoop(
        ForwardLoopConfig cfg,
        ForwardLoopState state,
        HashSet<RouteGeometryUtils.EdgeKey> allUsedEdges,
        List<List<Coordinate>> allSegments,
        List<Coordinate> allPoints,
        Dictionary<RouteGeometryUtils.EdgeKey, int> edgeAge,
        BuildContext ctx)
    {
        // Local aliases for readability — body references these names unchanged
        var profile = cfg.Profile;
        var start = cfg.Start;
        var targetDistKm = cfg.TargetDistKm;
        var adaptiveWaypointCount = cfg.AdaptiveWaypointCount;
        var avoidHighways = cfg.AvoidHighways;
        var attemptNumber = cfg.AttemptNumber;
        var radius = cfg.Radius;
        var cosLat = cfg.CosLat;
        var angularOffset = cfg.AngularOffset;
        var angleStep = cfg.AngleStep;
        var maxFailures = cfg.MaxFailures;
        var avoidSectors = cfg.AvoidSectors;
        var cancellationToken = cfg.CancellationToken;
        ref var currentPos = ref state.CurrentPos;
        ref var segmentCounter = ref state.SegmentCounter;
        ref var totalDistKm = ref state.TotalDistKm;
        ref var committedCount = ref state.CommittedCount;
        ref var lastEstReturnHaversineKm = ref state.LastEstReturnHaversineKm;
        ref var lastEstReturnWithMultiplierKm = ref state.LastEstReturnWithMultiplierKm;

        var blockedSectors = new HashSet<int>();
        if (avoidSectors != null)
        {
            foreach (var s in avoidSectors)
                blockedSectors.Add(s);
        }
        int consecutiveFailures = 0;
        string loopExitReason = "target_72pct";
        double runningOverlapRatio = 0;

        while (totalDistKm < targetDistKm * _options.ForwardLoopTargetRatio && consecutiveFailures < maxFailures)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double estReturnHaversine = RouteGeometryUtils.HaversineDistance(currentPos, start) / 1000;
            double estReturn = estReturnHaversine * 2.2;
            lastEstReturnHaversineKm = estReturnHaversine;
            lastEstReturnWithMultiplierKm = estReturn;
            if (totalDistKm + estReturn > targetDistKm * _options.ReturnDistanceCapMultiplier)
            {
                loopExitReason = "cap_110pct";
                break;
            }

            // Early abort on catastrophic attempts: too many failures too early
            if (consecutiveFailures >= maxFailures * _options.EarlyAbortFailureRatio && totalDistKm < targetDistKm * _options.EarlyAbortDistanceRatio)
            {
                ctx.AttemptFailedEarlyAbort = true;
                loopExitReason = "early_abort";
                break;
            }

            double targetAngle = angularOffset + committedCount * angleStep;

            // Bearing-aware angle adjustment: prefer angles near current heading
            if (allSegments.Count > 0 && allSegments[^1].Count >= 2)
            {
                double currentBearing = RouteGeometryUtils.ComputeBearing(allSegments[^1][^2], allSegments[^1][^1]) * Math.PI / 180;
                double angleDelta = targetAngle - currentBearing;
                if (angleDelta > Math.PI) angleDelta -= 2 * Math.PI;
                if (angleDelta < -Math.PI) angleDelta += 2 * Math.PI;

                // If the target angle is more than 90° from current heading, shift it closer
                if (Math.Abs(angleDelta) > Math.PI / 2)
                {
                    targetAngle = currentBearing + Math.Sign(angleDelta) * HeadingAdjustmentAngle; // 72° from heading
                }
            }

            int sectorAttempt = 0;
            int maxSectorShift = adaptiveWaypointCount + 4;
            while (sectorAttempt < maxSectorShift && WaypointGenerator.IsSectorBlocked(targetAngle, blockedSectors))
            {
                committedCount++;
                targetAngle = angularOffset + committedCount * angleStep;
                sectorAttempt++;
            }

            if (WaypointGenerator.IsSectorBlocked(targetAngle, blockedSectors))
                targetAngle = angularOffset + committedCount * angleStep + Random.Shared.NextDouble() * angleStep;

            double waypointRadius = radius * (WaypointRadiusMinFraction + Random.Shared.NextDouble() * WaypointRadiusRangeFraction);

            // Adaptive centre blending: shift toward currentPos when stalled to escape TooClose trap
            double blendFactor = Math.Min(1.0, consecutiveFailures / 10.0) * 0.4;
            var genCentre = blendFactor > 0.01
                ? new Coordinate(
                    start.Lat + (currentPos.Lat - start.Lat) * blendFactor,
                    start.Lon + (currentPos.Lon - start.Lon) * blendFactor)
                : start;
            double genCosLat = Math.Cos(genCentre.Lat * Math.PI / 180);

            // Edge density-aware radius adjustment
            if (allUsedEdges.Count > 30)
            {
                double predictedLat = genCentre.Lat + (waypointRadius / GeoConstants.KmPerDegreeLat) * Math.Sin(targetAngle);
                double predictedLon = genCentre.Lon + (waypointRadius / (GeoConstants.KmPerDegreeLat * Math.Max(genCosLat, GeoConstants.MinCosLat))) * Math.Cos(targetAngle);
                int density = _edgeSpatialIndex.CountInRadius(new Coordinate(predictedLat, predictedLon), _options.EdgeDensityCheckRadiusM);
                if (density > _options.EdgeDensityThreshold)
                    waypointRadius *= 1.1; // slight push further from congested area
            }

            var wpSw = System.Diagnostics.Stopwatch.StartNew();
            var candidate = _waypointGenerator.GenerateWaypointAtAngle(profile, genCentre, waypointRadius, targetAngle, genCosLat, avoidHighways);
            wpSw.Stop();
            ctx.WaypointGenMs += wpSw.ElapsedMilliseconds;
            ctx.TotalWaypointAttempts++;

            // Retry-with-shrink: if waypoint generation fails, try with smaller radius
            if (candidate == null && waypointRadius > radius * 0.3)
            {
                double shrinkRadius = waypointRadius * 0.7;
                candidate = _waypointGenerator.GenerateWaypointAtAngle(profile, genCentre, shrinkRadius, targetAngle, genCosLat, avoidHighways);
                if (candidate != null)
                    waypointRadius = shrinkRadius;
            }

            if (candidate == null)
            {
                ctx.WaypointRejectionReasons["NoRoadFound"] = ctx.WaypointRejectionReasons.GetValueOrDefault("NoRoadFound") + 1;
                consecutiveFailures++;
                continue;
            }
            if (RouteGeometryUtils.HaversineDistance(currentPos, candidate) < _options.MinWaypointDistanceM)
            {
                ctx.WaypointRejectionReasons["TooClose"] = ctx.WaypointRejectionReasons.GetValueOrDefault("TooClose") + 1;
                consecutiveFailures++;
                continue;
            }

            // Reject backtracking waypoints (bearing delta > 120°)
            if (allSegments.Count > 0 && allSegments[^1].Count >= 2)
            {
                double currentBearing = RouteGeometryUtils.ComputeBearing(allSegments[^1][^2], allSegments[^1][^1]);
                double candidateBearing = RouteGeometryUtils.ComputeBearing(currentPos, candidate);
                double bearingDelta = Math.Abs(candidateBearing - currentBearing);
                if (bearingDelta > 180) bearingDelta = 360 - bearingDelta;

                if (bearingDelta > _options.BacktrackingBearingDelta)
                {
                    ctx.WaypointRejectionReasons["Backtracking"] = ctx.WaypointRejectionReasons.GetValueOrDefault("Backtracking") + 1;
                    consecutiveFailures++;
                    continue;
                }
            }

            double candidateDistKm = RouteGeometryUtils.HaversineDistance(currentPos, candidate) / 1000;
            if (candidateDistKm > targetDistKm * _options.MaxWaypointDistanceRatio)
            {
                ctx.WaypointRejectionReasons["TooFar"] = ctx.WaypointRejectionReasons.GetValueOrDefault("TooFar") + 1;
                consecutiveFailures++;
                continue;
            }

            // Edge density check: soft guidance instead of hard reject
            if (allUsedEdges.Count > 50)
            {
                int edgeDensity = _edgeSpatialIndex.CountInRadius(candidate, 5000);
                if (edgeDensity > 35)
                {
                    // Try pushed version with larger radius
                    double pushedRadius = waypointRadius * 1.5;
                    var pushedCandidate = _waypointGenerator.GenerateWaypointAtAngle(
                        profile, start, pushedRadius, targetAngle, cosLat, avoidHighways);
                    if (pushedCandidate != null)
                    {
                        int pushedDensity = _edgeSpatialIndex.CountInRadius(pushedCandidate, _options.EdgeDensityCheckRadiusM);
                        if (pushedDensity < edgeDensity)
                            candidate = pushedCandidate; // use the less dense version
                    }
                    // If pushed version is also dense or null, keep original (don't reject)
                }
            }

            var connSw = System.Diagnostics.Stopwatch.StartNew();
            if (!_roadClassifier.HasRoadConnectivity(profile, candidate, cosLat))
            {
                connSw.Stop();
                ctx.ConnectivityCheckMs += connSw.ElapsedMilliseconds;
                ctx.WaypointRejectionReasons["NoConnectivity"] = ctx.WaypointRejectionReasons.GetValueOrDefault("NoConnectivity") + 1;
                consecutiveFailures++;
                continue;
            }
            connSw.Stop();
            ctx.ConnectivityCheckMs += connSw.ElapsedMilliseconds;

            var segSw = System.Diagnostics.Stopwatch.StartNew();

            double haversineSegDist = RouteGeometryUtils.HaversineDistance(currentPos, candidate);
            double estimatedRoutedDist = haversineSegDist * 1.5;
            if (estimatedRoutedDist > targetDistKm * _options.MaxWaypointDistanceRatio * 1000)
            {
                ctx.WaypointRejectionReasons["SegmentTooLong"] = ctx.WaypointRejectionReasons.GetValueOrDefault("SegmentTooLong") + 1;
                segSw.Stop();
                ctx.TotalSegmentBuildMs += segSw.ElapsedMilliseconds;
                _waypointGenerator.BlockSector(targetAngle, blockedSectors);
                consecutiveFailures++;
                continue;
            }

            var (seg, segPushRerouted, segOverlapBefore, segOverlapAfter, pushAttemptsUsed) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, candidate, allUsedEdges, edgeAge, segmentCounter);
            ctx.TotalPushAttemptsUsed += pushAttemptsUsed;
            if (segPushRerouted) ctx.TotalPushReroutesSucceeded++;
            if (seg == null)
            {
                _waypointGenerator.BlockSector(targetAngle, blockedSectors);
                consecutiveFailures++;
                continue;
            }

            double segLengthM = RouteGeometryUtils.CalculateDistance(seg);

            if (segLengthM > targetDistKm * _options.MaxWaypointDistanceRatio * 1000)
            {
                ctx.WaypointRejectionReasons["SegmentTooLong"] = ctx.WaypointRejectionReasons.GetValueOrDefault("SegmentTooLong") + 1;
                segSw.Stop();
                ctx.TotalSegmentBuildMs += segSw.ElapsedMilliseconds;
                _waypointGenerator.BlockSector(targetAngle, blockedSectors);
                consecutiveFailures++;
                continue;
            }

            // Check for overlapping segment — skip if too much overlap
            bool isOverlapping = segOverlapAfter > 0.15;
            if (isOverlapping)
            {
                double finalOverlap = RouteGeometryUtils.CalculateSegmentOverlap(seg!, allUsedEdges);
                if (finalOverlap > 0.25)
                {
                    segSw.Stop();
                    ctx.TotalSegmentBuildMs += segSw.ElapsedMilliseconds;
                    _waypointGenerator.BlockSector(targetAngle, blockedSectors);
                    consecutiveFailures++;
                    continue;
                }
            }

            segSw.Stop();
            ctx.TotalSegmentBuildMs += segSw.ElapsedMilliseconds;

            if (avoidHighways)
            {
                seg = RerouteIfMotorwaysBlocked(seg!, profile, currentPos, candidate, allUsedEdges, ctx.BlockedMotorwayEdges, avoidHighways);
            }

            _segmentEdgeBuffer.Clear();
            RouteGeometryUtils.ExtractEdgesInto(seg!, _segmentEdgeBuffer);
            allUsedEdges.UnionWith(_segmentEdgeBuffer);
            foreach (var edge in _segmentEdgeBuffer)
                _edgeSpatialIndex.Add(edge);

            // Birth-segment tracking: record segment number for new edges (no O(N) increment pass)
            segmentCounter++;
            foreach (var edge in _segmentEdgeBuffer)
                edgeAge[edge] = segmentCounter;

            allSegments.Add(seg!);
            allPoints.Add(candidate);
            currentPos = candidate;
            totalDistKm += segLengthM / 1000;

            ctx.SegmentLengths.Add(segLengthM);
            foreach (var pt in seg!)
            {
                if (pt.Lat < ctx.MinLat) ctx.MinLat = pt.Lat;
                if (pt.Lat > ctx.MaxLat) ctx.MaxLat = pt.Lat;
                if (pt.Lon < ctx.MinLon) ctx.MinLon = pt.Lon;
                if (pt.Lon > ctx.MaxLon) ctx.MaxLon = pt.Lon;
            }

            // Early repetition detection: if we're past 40% of target distance and
            // cumulative overlap is high, the route is likely to produce a repetitive loop.
            // Abort early to avoid wasting time on a doomed attempt.
            if (totalDistKm > targetDistKm * 0.4 && committedCount >= 4)
            {
                runningOverlapRatio = RouteGeometryUtils.CalculateTotalOverlapRatio(allSegments, _overlapEdgeCounts);
                if (runningOverlapRatio > _options.EarlyRepetitionOverlapThreshold)
                {
                    loopExitReason = "early_repetition_abort";
                    ctx.AttemptFailedEarlyAbort = true;
                    break;
                }
            }

            // Spatial spread enforcement: if after 3+ segments the route hasn't
            // ventured more than 15% of target distance from start, it's likely
            // trapped in a small area. Block more sectors to force wider exploration.
            if (committedCount >= 3 && totalDistKm < targetDistKm * 0.25)
            {
                double maxDistFromStart = 0;
                foreach (var pt in allPoints)
                {
                    double d = RouteGeometryUtils.HaversineDistance(start, pt) / 1000;
                    if (d > maxDistFromStart) maxDistFromStart = d;
                }
                if (maxDistFromStart < targetDistKm * 0.15)
                {
                    // Route is trapped — block the most-used sectors to force exploration outward
                    var sectorUsage = new Dictionary<int, int>();
                    foreach (var seg2 in allSegments)
                    {
                        if (seg2.Count < 2) continue;
                        var mid = RouteGeometryUtils.ComputeMidpoint(seg2);
                        double bearing = RouteGeometryUtils.ComputeBearing(start, mid);
                        int sector = (int)(bearing / SectorSizeDegrees) % 18;
                        sectorUsage[sector] = sectorUsage.GetValueOrDefault(sector) + 1;
                    }
                    foreach (var kvp in sectorUsage.OrderByDescending(x => x.Value).Take(3))
                        blockedSectors.Add(kvp.Key);
                }
            }

            // Commit segment — emit diagnostic event
            double segBearing2 = RouteGeometryUtils.ComputeBearing(currentPos, candidate);
            var midpoint2 = RouteGeometryUtils.ComputeMidpoint(seg);
            double distFromStart2 = RouteGeometryUtils.HaversineDistance(start, midpoint2);
            double sectorFromStart2 = RouteGeometryUtils.ComputeBearing(start, midpoint2);
            string? segRoadType2 = _roadClassifier.GetHighwayType(midpoint2, profile);
            double segCurvature2 = RouteGeometryUtils.CalculateAverageCurvature(seg);
            int segEdgeCount2 = RouteGeometryUtils.CountEdges(seg);
            var candidateQuality2 = _roadClassifier.ClassifyRoad(candidate, profile, avoidHighways);
            var committedEvt = new DebugStemEvent
            {
                AttemptNumber = attemptNumber,
                SegmentRole = "forward",
                SegmentIndex = committedCount,
                SegmentLengthM = segLengthM,
                CumulativeDistanceM = (totalDistKm - segLengthM / 1000) * 1000,
                SegmentBearing = Math.Round(segBearing2, 1),
                CloseCount = 0,
                OpposedCount = 0,
                Examined = 0,
                FirstHalfPoints = 0,
                SecondHalfPoints = 0,
                OverlapWithPriorSegments = segOverlapAfter,
                OriginalWaypoint = new[] { candidate.Lat, candidate.Lon },
                FinalWaypoint = new[] { candidate.Lat, candidate.Lon },
                Resolution = "committed",
                PushOverlapBefore = segOverlapBefore,
                PushOverlapAfter = segOverlapAfter,
                RoadType = segRoadType2,
                MidpointLat = midpoint2.Lat,
                MidpointLon = midpoint2.Lon,
                DistanceFromStartM = distFromStart2,
                SectorFromStart = Math.Round(sectorFromStart2, 1),
                SegmentCurvature = Math.Round(segCurvature2, 4),
                EdgeCount = segEdgeCount2,
                CandidateRoadClass = candidateQuality2.ToString(),
                NearestNearMissM = 0,
                HaversineToRoutedRatio = segLengthM > 0 ? Math.Round(haversineSegDist / segLengthM, 3) : 0,
                RoadQualityAtMidpoint = segRoadType2,
                SegmentEdgeDensityAtMidpoint = _edgeSpatialIndex.CountInRadius(midpoint2, _options.EdgeDensityCheckRadiusM),
            };

            RouteGeometryUtils.CaptureOverlappingEdgeIds(committedEvt, seg, allUsedEdges);
            _diagnostics.Add(committedEvt);

            committedCount++;
            consecutiveFailures = 0;
        }

        return loopExitReason;
    }

    private int GenerateHomingWaypoints(
        ForwardLoopConfig cfg,
        ForwardLoopState state,
        HashSet<RouteGeometryUtils.EdgeKey> allUsedEdges,
        List<List<Coordinate>> allSegments,
        List<Coordinate> allPoints,
        BuildContext ctx)
    {
        var profile = cfg.Profile;
        var start = cfg.Start;
        var targetDistKm = cfg.TargetDistKm;
        var avoidHighways = cfg.AvoidHighways;
        var attemptNumber = cfg.AttemptNumber;
        var committedCount = state.CommittedCount;
        var cosLat = cfg.CosLat;
        ref var currentPos = ref state.CurrentPos;
        ref var totalDistKm = ref state.TotalDistKm;

        int homingWaypointsUsed = 0;
        double remainingReturnKm = RouteGeometryUtils.HaversineDistance(currentPos, start) / 1000;

        // Multi-hop return planning: generate 1-3 homing waypoints based on distance
        // Gate: skip homing if already at 55%+ of target (reduces overshoot while maintaining guidance)
        int homingCount = totalDistKm < targetDistKm * 0.65
            ? (remainingReturnKm > 40 ? 3 : remainingReturnKm > 20 ? 2 : remainingReturnKm > 10 ? 1 : 0)
            : 0;

        for (int hi = 0; hi < homingCount; hi++)
        {
            double bearingToStart = RouteGeometryUtils.ComputeBearing(currentPos, start);
            // Distance for this hop: earlier hops go further, later hops get closer to start
            double hopRatio = hi == 0 ? 0.3 : hi == 1 ? 0.6 : 0.8;
            double homingDistKm = remainingReturnKm * hopRatio;

            // Add perpendicular offset to avoid overlapping with forward path
            double perpOffset = _options.HomingPerpendicularOffsetM * Math.Sin(hi * Math.PI / Math.Max(homingCount, 1)); // 1.5km offset, alternating sides
            // M1: perpendicular to bearing-to-start, in math angle radians
            double perpAngle = RouteGeometryUtils.BearingToMathAngle(bearingToStart) + Math.PI / 2;

            bool homingFound = false;

            for (int homingAngleIdx = 0; homingAngleIdx < _options.MaxHomingAngleAttempts; homingAngleIdx++)
            {
                double angleOffset = (homingAngleIdx - 6) * _options.HomingAngleStepDegrees * Math.PI / 180; // -30° to +30° in 5° steps

                // Use the new homing waypoint generator with edge avoidance
                var homingCandidate = _waypointGenerator.GenerateHomingWaypoint(
                    profile, currentPos, start, homingDistKm, bearingToStart, angleOffset, avoidHighways,
                    perpOffset, perpAngle);

                if (homingCandidate == null) continue;

                // Additional checks
                double candDist = RouteGeometryUtils.HaversineDistance(currentPos, homingCandidate) / 1000;
                if (candDist < _options.MinHomingSegmentDistanceKm) continue;

                // Check road connectivity
                if (!_roadClassifier.HasRoadConnectivity(profile, homingCandidate, cosLat)) continue;

                // Accept this homing waypoint
                var (homingSeg, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(
                    profile, currentPos, homingCandidate, allUsedEdges);
                if (homingSeg == null) continue;

                double homingSegOverlap = RouteGeometryUtils.CalculateSegmentOverlap(homingSeg, allUsedEdges);
                if (homingSegOverlap > _options.HomingOverlapThreshold) continue;

                // Commit homing segment
                if (avoidHighways)
                {
                    homingSeg = RerouteIfMotorwaysBlocked(homingSeg, profile, currentPos, homingCandidate, allUsedEdges, ctx.BlockedMotorwayEdges, avoidHighways);
                }
                allSegments.Add(homingSeg);
                allPoints.Add(homingCandidate);
                currentPos = homingCandidate;
                totalDistKm += RouteGeometryUtils.CalculateDistance(homingSeg) / 1000;
                homingWaypointsUsed++;
                homingFound = true;

                _diagnostics.Add(new DebugStemEvent
                {
                    AttemptNumber = attemptNumber,
                    SegmentRole = "homing",
                    SegmentIndex = committedCount + hi,
                    SegmentLengthM = RouteGeometryUtils.CalculateDistance(homingSeg),
                    OverlapWithPriorSegments = homingSegOverlap,
                    OriginalWaypoint = new[] { homingCandidate.Lat, homingCandidate.Lon },
                    FinalWaypoint = new[] { homingCandidate.Lat, homingCandidate.Lon },
                    Resolution = "homing",
                    DistanceFromStartM = RouteGeometryUtils.HaversineDistance(start, homingCandidate),
                    NearestNearMissM = 0,
                });

                break;
            }

            if (!homingFound) break; // can't find homing waypoints, stop trying

            // Recalculate remaining return distance
            remainingReturnKm = RouteGeometryUtils.HaversineDistance(currentPos, start) / 1000;
        }

        return homingWaypointsUsed;
    }

    private void BuildReturnSegment(
        ForwardLoopConfig cfg,
        ForwardLoopState state,
        double forwardDistanceKm,
        Dictionary<RouteGeometryUtils.EdgeKey, int> edgeAge,
        HashSet<RouteGeometryUtils.EdgeKey> allUsedEdges,
        List<List<Coordinate>> allSegments,
        List<Coordinate> allPoints,
        ref string loopExitReason,
        BuildContext ctx)
    {
        var profile = cfg.Profile;
        var start = cfg.Start;
        var targetDistKm = cfg.TargetDistKm;
        var avoidHighways = cfg.AvoidHighways;
        var attemptNumber = cfg.AttemptNumber;
        var lastEstReturnHaversineKm = state.LastEstReturnHaversineKm;
        var lastEstReturnWithMultiplierKm = state.LastEstReturnWithMultiplierKm;
        var committedCount = state.CommittedCount;
        var segmentCounter = state.SegmentCounter;
        ref var currentPos = ref state.CurrentPos;
        ref var totalDistKm = ref state.TotalDistKm;
        var (returnSeg, retPushRerouted, _, _, retPushUsed) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, start, allUsedEdges, edgeAge, segmentCounter);
        double returnOverlapBeforePush = 0;
        int returnPushAttemptsUsed = retPushUsed;
        if (retPushRerouted) ctx.TotalPushReroutesSucceeded++;
        int returnRerouteCount = 0;
        if (returnSeg != null)
        {
            var forwardEdgeKeys = RouteGeometryUtils.BuildForwardEdgeKeySet(allSegments);
            double forwardOverlap = RouteGeometryUtils.CalculateSegmentOverlap(returnSeg, forwardEdgeKeys);
            returnOverlapBeforePush = forwardOverlap;

            if (forwardOverlap > 0.30)
            {
                // High overlap on return — emit diagnostic and continue with the segment as-is
                _diagnostics.Add(new DebugStemEvent
                {
                    AttemptNumber = attemptNumber,
                    SegmentRole = "return",
                    SegmentLengthM = RouteGeometryUtils.CalculateDistance(returnSeg),
                    OverlapWithPriorSegments = forwardOverlap,
                    OriginalWaypoint = new[] { start.Lat, start.Lon },
                    FinalWaypoint = new[] { start.Lat, start.Lon },
                    Resolution = "committed",
                    EstReturnHaversineKm = Math.Round(lastEstReturnHaversineKm, 2),
                    EstReturnWithMultiplierKm = Math.Round(lastEstReturnWithMultiplierKm, 2),
                    ActualReturnRoutedKm = Math.Round(RouteGeometryUtils.CalculateDistance(returnSeg) / 1000, 2),
                });
            }
            else
            {
                _diagnostics.Add(new DebugStemEvent
                {
                    AttemptNumber = attemptNumber,
                    SegmentRole = "return",
                    SegmentLengthM = RouteGeometryUtils.CalculateDistance(returnSeg),
                    OverlapWithPriorSegments = forwardOverlap,
                    OriginalWaypoint = new[] { start.Lat, start.Lon },
                    FinalWaypoint = new[] { start.Lat, start.Lon },
                    Resolution = "committed",
                    NearestNearMissM = 0,
                    EstReturnHaversineKm = Math.Round(lastEstReturnHaversineKm, 2),
                    EstReturnWithMultiplierKm = Math.Round(lastEstReturnWithMultiplierKm, 2),
                    ActualReturnRoutedKm = Math.Round(RouteGeometryUtils.CalculateDistance(returnSeg) / 1000, 2),
                });
            }
        }

        if (returnSeg != null)
        {
            if (avoidHighways)
            {
                returnSeg = RerouteIfMotorwaysBlocked(returnSeg, profile, currentPos, start, allUsedEdges, ctx.BlockedMotorwayEdges, avoidHighways);
            }
            allSegments.Add(returnSeg);

            // Compute return segment quality
            double returnDistanceKm = RouteGeometryUtils.CalculateDistance(returnSeg) / 1000;
            double returnHaversineKm = RouteGeometryUtils.HaversineDistance(currentPos, start) / 1000;
            double returnRatio = returnHaversineKm > 0 ? returnDistanceKm / returnHaversineKm : 0;
            var (returnCurvature, returnOverlapPct) = RouteGeometryUtils.AnalyzeReturnSegment(returnSeg, allSegments);
            string returnRoadType = _roadClassifier.GetHighwayType(RouteGeometryUtils.ComputeMidpoint(returnSeg), profile) ?? "unknown";

            ctx.ForwardDistanceKm = forwardDistanceKm;
            ctx.ReturnDistanceKm = returnDistanceKm;
            ctx.ReturnRatio = Math.Round(returnRatio, 2);
            ctx.ForwardLoopExitReason = loopExitReason;
            ctx.ReturnSegmentCurvature = returnCurvature;
            ctx.ReturnSegmentRoadType = returnRoadType;
            ctx.ReturnSegmentOverlapPct = returnOverlapPct;

            // Return path quality gate: if the return path is more than 2.5x the
            // straight-line distance, it's backtracking. This is a strong signal that
            // the forward path ended in a bad position (dead end, one-way trap, etc).
            // Mark this as a failed attempt so the next attempt can try different waypoints.
            if (returnRatio > 2.5 && returnDistanceKm > targetDistKm * 0.3)
            {
                loopExitReason = "return_ratio_too_high";
                ctx.AttemptFailedEarlyAbort = true;
            }

            // Return path diagnostics
            ctx.ReturnPushAttempts = returnPushAttemptsUsed;
            ctx.ReturnOverlapBeforePush = returnOverlapBeforePush;
            ctx.ReturnOverlapAfterPush = returnOverlapPct;
            ctx.ActualReturnVsEstimate = lastEstReturnWithMultiplierKm > 0 ? returnDistanceKm / lastEstReturnWithMultiplierKm : 0;
            ctx.ReturnSegmentRerouteCount = returnRerouteCount;

            // Return path sector coverage
            var returnBearings = new HashSet<int>();
            for (int i = 0; i < returnSeg.Count - 1; i++)
            {
                double brg = RouteGeometryUtils.ComputeBearing(returnSeg[i], returnSeg[i + 1]);
                int sector = (int)(brg / 30) % 12;
                returnBearings.Add(sector);
            }
            ctx.ReturnPathSectorCoverage = returnBearings.Count;
        }
    }

    private (List<Coordinate> geometry, List<Coordinate> allPoints)? AssembleFinalRoute(
        Coordinate start,
        List<List<Coordinate>> allSegments,
        List<Coordinate> allPoints,
        HashSet<RouteGeometryUtils.EdgeKey> allUsedEdges,
        BuildContext ctx)
    {
        allPoints.Add(start);
        ctx.FinalEdgeSetSize = allUsedEdges.Count;
        var geometry = RouteGeometryUtils.AssembleGeometry(allSegments);
        if (geometry == null || geometry.Count < 2) return null;

        // Early abort: return null so caller retries with different parameters.
        // This happens when repetition is detected mid-route or return path is too long.
        if (ctx.AttemptFailedEarlyAbort && geometry.Count < 10)
            return null;

        _routeStatistics.ReturnToStartIndex = RouteGeometryUtils.FindLoopMidpoint(geometry, start);

        return (geometry, allPoints);
    }

    /// <summary>
    /// Builds a round trip using the GraphHopper-inspired forward-then-alternative approach.
    /// This is an alternative to the build-then-split approach used in BuildProgressiveLoop.
    /// </summary>
    public (List<Coordinate> geometry, List<Coordinate> allPoints, BuildContext context)? BuildAlternativeLoop(
        IProfileInstance profile,
        Coordinate start,
        double targetDistKm,
        bool avoidHighways,
        CancellationToken cancellationToken,
        int attemptNumber = 0,
        double turnaroundRatio = 0.5,
        DirectionBias direction = DirectionBias.Any,
        double? avoidBearing = null)
    {
        var context = new BuildContext(start);
        context.BuilderMethod = "alternative_path";
        var alternativePathFinder = new AlternativePathFinder(_mapRepository, _roadClassifier, _edgeBlocker, Options.Create(_options));

        var result = alternativePathFinder.FindRoundTrip(profile, start, targetDistKm, avoidHighways, turnaroundRatio, direction, context, avoidBearing);
        if (result == null)
        {
            return null;
        }

        var (geometry, overlapRatio, plateauCount, shareWeight, turnaroundPoint, returnPathDiagnostics) = result.Value;

        // Motorways are now blocked at load time — no post-hoc checking needed

        // Add diagnostic information
        _diagnostics.Add(new DebugStemEvent
        {
            AttemptNumber = attemptNumber,
            SegmentRole = "forward",
            SegmentLengthM = RouteGeometryUtils.CalculateDistance(geometry),
            OverlapWithPriorSegments = overlapRatio,
            OriginalWaypoint = new[] { start.Lat, start.Lon },
            FinalWaypoint = new[] { start.Lat, start.Lon },
            Resolution = "alternative_path",
            StraightLineRatio = plateauCount,
        });

        context.FinalEdgeSetSize = geometry.Count - 1;
        context.ReturnPathDiagnostics = returnPathDiagnostics;

        // Compute forward/return split using the actual turnaround point
        // Find the index in geometry closest to the turnaround point
        int loopMidIndex = 0;
        double minDist = double.MaxValue;
        for (int i = 0; i < geometry.Count; i++)
        {
            double dist = RouteGeometryUtils.HaversineDistance(geometry[i], turnaroundPoint);
            if (dist < minDist) { minDist = dist; loopMidIndex = i; }
        }
        _routeStatistics.ReturnToStartIndex = loopMidIndex;
        double forwardDistKm = RouteGeometryUtils.CalculateDistance(geometry.GetRange(0, loopMidIndex + 1)) / 1000;
        double returnDistKm = RouteGeometryUtils.CalculateDistance(geometry.GetRange(loopMidIndex, geometry.Count - loopMidIndex)) / 1000;
        double totalDistKm = forwardDistKm + returnDistKm;
        double returnHaversineKm = RouteGeometryUtils.HaversineDistance(geometry[loopMidIndex], start) / 1000;
        double returnRatio = returnHaversineKm > 0 ? returnDistKm / returnHaversineKm : 0;
        var (returnCurvature, returnOverlapPct) = RouteGeometryUtils.AnalyzeReturnSegment(
            geometry.GetRange(loopMidIndex, geometry.Count - loopMidIndex),
            new List<List<Coordinate>> { geometry.GetRange(0, loopMidIndex + 1) });

        context.ForwardDistanceKm = forwardDistKm;
        context.ReturnDistanceKm = returnDistKm;
        context.ReturnRatio = Math.Round(returnRatio, 2);
        context.ForwardLoopExitReason = "alternative_path";
        context.ReturnSegmentCurvature = returnCurvature;
        context.ReturnSegmentRoadType = _roadClassifier.GetHighwayType(RouteGeometryUtils.ComputeMidpoint(geometry.GetRange(loopMidIndex, geometry.Count - loopMidIndex)), profile) ?? "unknown";
        context.ReturnSegmentOverlapPct = returnOverlapPct;

        // Return path diagnostics (alternative path - no push/reroute pipeline)
        context.ReturnPushAttempts = 0;
        context.ReturnOverlapBeforePush = returnOverlapPct;
        context.ReturnOverlapAfterPush = returnOverlapPct;
        context.EstReturnAtLoopExit = 0;
        context.ActualReturnVsEstimate = 0;
        context.ForwardHaversineAtExit = returnHaversineKm;
        context.SectorsBlockedAtReturn = 0;
        context.ReturnSegmentRerouteCount = 0;

        // Circularity diagnostics (alternative path)
        var forwardGeo = geometry.GetRange(0, loopMidIndex + 1);

        // Count actual routing segments by detecting large bearing changes
        // Each angular waypoint creates a direction change
        var segmentBearings = new List<double>();
        var segmentAngles = new List<double>();
        int segmentCount = 1;

        if (forwardGeo.Count >= 2)
        {
            double prevBearing = RouteGeometryUtils.ComputeBearing(forwardGeo[0], forwardGeo[1]);
            segmentBearings.Add(prevBearing);

            for (int i = 1; i < forwardGeo.Count - 1; i++)
            {
                double currBearing = RouteGeometryUtils.ComputeBearing(forwardGeo[i], forwardGeo[i + 1]);
                double delta = Math.Abs(currBearing - prevBearing);
                if (delta > 180) delta = 360 - delta;

                if (delta > 15) // significant direction change = new segment
                {
                    segmentCount++;
                    segmentBearings.Add(currBearing);
                    segmentAngles.Add(currBearing);
                }
                prevBearing = currBearing;
            }
        }

        context.ForwardSegmentCount = segmentCount;
        context.ForwardSegmentBearings = segmentBearings;
        context.ForwardWaypointCount = Math.Max(0, segmentCount - 1); // waypoints = segments - 1

        // Compute waypoint distribution uniformity from bearings (chi-squared over 18 sectors)
        if (segmentBearings.Count > 1)
        {
            int[] sectorCounts = new int[18];
            foreach (double bearing in segmentBearings)
            {
                int sector = (int)(bearing / 20) % 18;
                if (sector < 0) sector += 18;
                sectorCounts[sector]++;
            }
            double expected = segmentBearings.Count / 18.0;
            double chiSquared = 0;
            for (int i = 0; i < 18; i++)
            {
                double diff = sectorCounts[i] - expected;
                chiSquared += (diff * diff) / expected;
            }
            double maxChiSquared = expected * 18;
            context.WaypointDistributionUniformity = maxChiSquared > 0 ? 1.0 - (chiSquared / maxChiSquared) : 0;
        }
        context.ReturnPathSectorCoverage = RouteGeometryUtils.CalculateBearingSpread(
            geometry.GetRange(loopMidIndex, geometry.Count - loopMidIndex)) > 0 ? 6 : 0;

        // Build allPoints with turnaround point for accurate waypoint tracking
        var allPoints = new List<Coordinate> { start };
        allPoints.Add(turnaroundPoint);
        allPoints.Add(start);
        return (geometry, allPoints, context);
    }

    /// <summary>
    /// Checks if a segment contains motorway edges and re-routes if needed.
    /// Returns the (possibly re-routed) segment and whether it was re-routed.
    /// </summary>
    private List<Coordinate> RerouteIfMotorwaysBlocked(
        List<Coordinate> segment,
        IProfileInstance profile,
        Coordinate from,
        Coordinate to,
        HashSet<RouteGeometryUtils.EdgeKey> allUsedEdges,
        HashSet<uint> blockedEdges,
        bool avoidHighways)
    {
        if (!avoidHighways)
            return segment;

        var blockedMw = _edgeBlocker.BlockMotorwaysInSegment(segment, profile, blockedEdges);
        if (blockedMw.Count == 0)
            return segment;

        var (rerouted, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(
            profile, from, to, allUsedEdges);
        if (rerouted == null || RouteGeometryUtils.CalculateDistance(rerouted) == 0)
            return segment;

        _edgeBlocker.BlockMotorwaysInSegment(rerouted, profile, blockedEdges);
        return rerouted;
    }
}
