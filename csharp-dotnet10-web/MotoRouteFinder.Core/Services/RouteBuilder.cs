using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Itinero.Profiles;
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
    private readonly StemFixer _stemFixer;
    private readonly RouteStatistics _routeStatistics;
    private readonly DiagnosticsCollector _diagnostics;
    private readonly EdgeBlocker _edgeBlocker;

    // Reusable buffers for per-segment edge tracking to avoid allocating
    // a new HashSet + List on every segment (~30x per route attempt).
    private readonly HashSet<RouteGeometryUtils.EdgeKey> _segmentEdgeBuffer = new();
    private readonly List<RouteGeometryUtils.EdgeKey> _extractEdgesBuffer = new();
    private readonly Dictionary<RouteGeometryUtils.EdgeKey, int> _overlapEdgeCounts = new();
    private readonly RouteGeometryUtils.EdgeSpatialIndex _edgeSpatialIndex = new();

    public RouteBuilder(
        MapRepository mapRepository,
        RoadClassifier roadClassifier,
        RouteAssembler routeAssembler,
        WaypointGenerator waypointGenerator,
        StemFixer stemFixer,
        RouteStatistics routeStatistics,
        DiagnosticsCollector diagnostics,
        EdgeBlocker edgeBlocker)
    {
        _mapRepository = mapRepository;
        _roadClassifier = roadClassifier;
        _routeAssembler = routeAssembler;
        _waypointGenerator = waypointGenerator;
        _stemFixer = stemFixer;
        _routeStatistics = routeStatistics;
        _diagnostics = diagnostics;
        _edgeBlocker = edgeBlocker;
    }

    public (List<Coordinate> geometry, List<Coordinate> allPoints, BuildContext context)? BuildProgressiveLoop(
        IProfileInstance profile,
        Coordinate start,
        double targetDistKm,
        int waypointCount,
        bool avoidHighways,
        CancellationToken cancellationToken,
        int attemptNumber = 0,
        Action<double>? progressCallback = null,
        HashSet<int>? avoidSectors = null,
        DirectionBias direction = DirectionBias.Any)
    {
        var context = new BuildContext(start);
        context.BuilderMethod = "progressive_loop";
        var result = BuildProgressiveLoopInternal(profile, start, targetDistKm, waypointCount, avoidHighways, cancellationToken, context, attemptNumber, progressCallback, avoidSectors, direction);
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
        int consecutiveStems = 0;
        const int maxConsecutiveStems = 5;

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
                IsStem = StemDetector.IsStemSegment(seg, strict: true),
                OriginalWaypoint = new[] { next.Lat, next.Lon },
                FinalWaypoint = new[] { next.Lat, next.Lon },
                Resolution = "committed",
            });

            if (StemDetector.IsStemSegment(seg, strict: true))
            {
                consecutiveStems++;
                if (consecutiveStems > maxConsecutiveStems)
                {
                    if (allSegments.Count >= 2)
                    {
                        var partialGeo = RouteGeometryUtils.AssembleGeometry(allSegments);
                        if (partialGeo != null)
                            return (partialGeo, allPoints, needsRegen: true, currentPos);
                    }
                    continue;
                }

                var (fixed_, _, _) = _stemFixer.TryFixStem(profile, currentPos, next, seg, allUsedEdges);
                _stemFixer.IncrementFixStemCall();
                if (fixed_ != null) _stemFixer.IncrementFixStemSuccess();
                bool usedFixed_ = false;
                if (fixed_ != null)
                {
                    var nextQuality = _roadClassifier.ClassifyRoad(next, profile, avoidHighways);
                    if (nextQuality == RoadClassifier.RoadQuality.Preferred || nextQuality == RoadClassifier.RoadQuality.Acceptable)
                    {
                        seg = fixed_;
                        consecutiveStems = 0;
                        usedFixed_ = true;
                    }
                }
                if (!usedFixed_)
                {
                    var (replacement, _) = _stemFixer.GenerateReplacementWaypoint(profile, next, currentPos, avoidHighways, RouteGeometryUtils.CalculateDistance(seg));
                    _stemFixer.IncrementReplacementCall();
                    if (replacement != null) _stemFixer.IncrementReplacementSuccess();
                    if (replacement != null)
                    {
                        remaining.Insert(0, replacement);
                        continue;
                    }

                    var midLat = (currentPos.Lat + next.Lat) / 2;
                    var midLon = (currentPos.Lon + next.Lon) / 2;
                    var intermediate = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(midLat, midLon), 3000);
                    if (intermediate == null) { continue; }
                    var intermediateQuality = _roadClassifier.ClassifyRoad(intermediate, profile, avoidHighways);
                    if ((intermediateQuality == RoadClassifier.RoadQuality.Preferred || intermediateQuality == RoadClassifier.RoadQuality.Acceptable)
                        && RouteGeometryUtils.HaversineDistance(intermediate, currentPos) > 500
                        && RouteGeometryUtils.HaversineDistance(intermediate, next) > 500)
                    {
                        remaining.Insert(0, next);
                        remaining.Insert(0, intermediate);
                        continue;
                    }

                    if (consecutiveStems > 2 && allSegments.Count >= 2)
                    {
                        var partialGeo = RouteGeometryUtils.AssembleGeometry(allSegments);
                        if (partialGeo != null)
                            return (partialGeo, allPoints, needsRegen: true, currentPos);
                    }

                    continue;
                }
            }
            else
            {
                consecutiveStems = 0;
            }

            if (avoidHighways)
            {
                (seg, _) = RerouteIfMotorwaysBlocked(seg!, profile, currentPos, next, allUsedEdges, blockedMotorwayEdges, avoidHighways);
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
        if (returnSeg != null && StemDetector.IsStemSegment(returnSeg, strict: true))
        {
            var forwardEdgeKeys = RouteGeometryUtils.BuildForwardEdgeKeySet(allSegments);
            double forwardOverlap = RouteGeometryUtils.CalculateSegmentOverlap(returnSeg, forwardEdgeKeys);
            if (forwardOverlap <= 0.4)
            {
                var (fixedReturn, _, _) = _stemFixer.TryFixStem(profile, currentPos, start, returnSeg, allUsedEdges);
                _stemFixer.IncrementFixStemCall();
                if (fixedReturn != null) _stemFixer.IncrementFixStemSuccess();
                if (fixedReturn != null)
                {
                    returnSeg = fixedReturn;
                }
                else
                {
                    var (replacement, _) = _stemFixer.GenerateReplacementWaypoint(profile, start, currentPos, avoidHighways, RouteGeometryUtils.CalculateDistance(returnSeg));
                    _stemFixer.IncrementReplacementCall();
                    if (replacement != null) _stemFixer.IncrementReplacementSuccess();
                    if (replacement != null)
                    {
                        var (replSeg, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, replacement, allUsedEdges);
                        if (replSeg != null && !StemDetector.IsStemSegment(replSeg, strict: true))
                        {
                            if (avoidHighways)
                            {
                                (replSeg, _) = RerouteIfMotorwaysBlocked(replSeg, profile, currentPos, replacement, allUsedEdges, blockedMotorwayEdges, avoidHighways);
                            }
                            allUsedEdges.UnionWith(RouteGeometryUtils.ExtractEdges(replSeg));
                            allSegments.Add(replSeg);
                            allPoints.Add(replacement);
                            currentPos = replacement;
                            var (_returnSegTemp, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, start, allUsedEdges);
                            returnSeg = _returnSegTemp;
                        }
                    }

                    if (returnSeg != null && StemDetector.IsStemSegment(returnSeg, strict: true))
                    {
                        var midLat = (currentPos.Lat + start.Lat) / 2;
                        var midLon = (currentPos.Lon + start.Lon) / 2;
                        var intermediate = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(midLat, midLon), 3000);
                        if (intermediate != null && RouteGeometryUtils.HaversineDistance(intermediate, currentPos) > 500
                            && RouteGeometryUtils.HaversineDistance(intermediate, start) > 500)
                        {
                            var intermediateQuality = _roadClassifier.ClassifyRoad(intermediate, profile, avoidHighways);
                            if (intermediateQuality == RoadClassifier.RoadQuality.Preferred || intermediateQuality == RoadClassifier.RoadQuality.Acceptable)
                            {
                                var (viaSeg, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, intermediate, allUsedEdges);
                                if (viaSeg != null && !StemDetector.IsStemSegment(viaSeg, strict: true))
                                {
                                    if (avoidHighways)
                                    {
                                        (viaSeg, _) = RerouteIfMotorwaysBlocked(viaSeg, profile, currentPos, intermediate, allUsedEdges, blockedMotorwayEdges, avoidHighways);
                                    }
                                    allUsedEdges.UnionWith(RouteGeometryUtils.ExtractEdges(viaSeg));
                                    allSegments.Add(viaSeg);
                                    allPoints.Add(intermediate);
                                    currentPos = intermediate;
                                    var (_returnSegTemp, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, start, allUsedEdges);
                                    returnSeg = _returnSegTemp;
                                }
                            }
                        }
                    }
                }
            }
        }

        if (returnSeg != null)
        {
            if (avoidHighways)
            {
                (returnSeg, _) = RerouteIfMotorwaysBlocked(returnSeg, profile, currentPos, start, allUsedEdges, blockedMotorwayEdges, avoidHighways);
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
        Action<double>? progressCallback,
        HashSet<int>? avoidSectors,
        DirectionBias direction = DirectionBias.Any)
    {
        // Adaptive waypoint count: scale with distance (GraphHopper-inspired)
        int adaptiveWaypointCount = Math.Max(waypointCount, 3 + (int)(targetDistKm / 40));

        // Convert direction bias to a bearing in radians for waypoint placement
        double preferredBearingRad = direction switch
        {
            DirectionBias.North => 0,
            DirectionBias.Northeast => Math.PI / 4,
            DirectionBias.East => Math.PI / 2,
            DirectionBias.Southeast => 3 * Math.PI / 4,
            DirectionBias.South => Math.PI,
            DirectionBias.Southwest => 5 * Math.PI / 4,
            DirectionBias.West => 3 * Math.PI / 2,
            DirectionBias.Northwest => 7 * Math.PI / 4,
            _ => -1 // Any (no preference)
        };

        _diagnostics.Add(new DebugStemEvent
        {
            AttemptNumber = attemptNumber,
            SegmentRole = "heartbeat",
            Resolution = "pipeline_active",
            OriginalWaypoint = new[] { start.Lat, start.Lon },
        });
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
        double angularOffset = preferredBearingRad >= 0
            ? preferredBearingRad + (Random.Shared.NextDouble() - 0.5) * Math.PI / 6 // ±15° jitter
            : Random.Shared.NextDouble() * 2 * Math.PI;
        double angleStep = 2 * Math.PI / Math.Max(adaptiveWaypointCount, 3);
        var blockedSectors = new HashSet<int>();
        if (avoidSectors != null)
        {
            foreach (var s in avoidSectors)
                blockedSectors.Add(s);
        }
        int committedCount = 0;
        int consecutiveFailures = 0;
        int consecutiveStemSegments = 0;
        int maxConsecutiveStemSegments = 0;
        int nullSegmentDrops = 0;
        int lastStemSegmentIndex = -1;
        const int maxFailures = 30;

        _stemFixer.ResetTimers();
        ctx.Reset(start);

        _roadClassifier.ResetCounters();
        _routeAssembler.ResetCounters();
        _edgeBlocker.ResetCounters();
        _waypointGenerator.ResetCounters();

        double lastEstReturnHaversineKm = 0;
        double lastEstReturnWithMultiplierKm = 0;
        var failedWaypointsBuffer = new List<Models.FailedWaypoint>();
        var previousStemPositions = new List<Coordinate>();
        double forwardDistanceKm = 0;
        string loopExitReason = "target_72pct";
        double runningOverlapRatio = 0; // Track cumulative overlap to detect repetition early

        while (totalDistKm < targetDistKm * 0.85 && consecutiveFailures < maxFailures)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double estReturnHaversine = RouteGeometryUtils.HaversineDistance(currentPos, start) / 1000;
            double estReturn = estReturnHaversine * 2.2;
            lastEstReturnHaversineKm = estReturnHaversine;
            lastEstReturnWithMultiplierKm = estReturn;
            if (totalDistKm + estReturn > targetDistKm * 1.4)
            {
                loopExitReason = "cap_110pct";
                break;
            }

            // Early abort on catastrophic attempts: too many failures too early
            if (consecutiveFailures >= maxFailures * 0.6 && totalDistKm < targetDistKm * 0.4)
            {
                ctx.AttemptFailedEarlyAbort = true;
                loopExitReason = "early_abort";
                break;
            }

            double targetAngle = angularOffset + committedCount * angleStep;

            // Bearing-aware angle adjustment: prefer angles near current heading
            if (allSegments.Count > 0 && allSegments[^1].Count >= 2)
            {
                double currentBearing = RouteGeometryUtils.ComputeBearing(allSegments[^1][^2], allSegments[^1][^1]);
                double angleDelta = targetAngle - currentBearing;
                if (angleDelta > Math.PI) angleDelta -= 2 * Math.PI;
                if (angleDelta < -Math.PI) angleDelta += 2 * Math.PI;

                // If the target angle is more than 90° from current heading, shift it closer
                if (Math.Abs(angleDelta) > Math.PI / 2)
                {
                    targetAngle = currentBearing + Math.Sign(angleDelta) * Math.PI / 2.5; // 72° from heading
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

            double waypointRadius = radius * (0.5 + Random.Shared.NextDouble() * 0.6);

            // Edge density-aware radius adjustment
            if (allUsedEdges.Count > 30)
            {
                double predictedLat = start.Lat + (waypointRadius / GeoConstants.KmPerDegreeLat) * Math.Sin(targetAngle);
                double predictedLon = start.Lon + (waypointRadius / (GeoConstants.KmPerDegreeLat * Math.Max(cosLat, GeoConstants.MinCosLat))) * Math.Cos(targetAngle);
                int density = _edgeSpatialIndex.CountInRadius(new Coordinate(predictedLat, predictedLon), 5000);
                if (density > 25)
                    waypointRadius *= 1.1; // slight push further from congested area
            }

            var wpSw = System.Diagnostics.Stopwatch.StartNew();
            var candidate = _waypointGenerator.GenerateWaypointAtAngle(profile, start, waypointRadius, targetAngle, cosLat, avoidHighways);
            wpSw.Stop();
            ctx.WaypointGenMs += wpSw.ElapsedMilliseconds;
            ctx.TotalWaypointAttempts++;

            // Retry-with-shrink: if waypoint generation fails, try with smaller radius
            if (candidate == null && waypointRadius > radius * 0.3)
            {
                double shrinkRadius = waypointRadius * 0.7;
                candidate = _waypointGenerator.GenerateWaypointAtAngle(profile, start, shrinkRadius, targetAngle, cosLat, avoidHighways);
                if (candidate != null)
                    waypointRadius = shrinkRadius;
            }

            if (candidate == null)
            {
                ctx.WaypointRejectionReasons["NoRoadFound"] = ctx.WaypointRejectionReasons.GetValueOrDefault("NoRoadFound") + 1;
                consecutiveFailures++;
                continue;
            }
            if (RouteGeometryUtils.HaversineDistance(currentPos, candidate) < 200)
            {
                ctx.WaypointRejectionReasons["TooClose"] = ctx.WaypointRejectionReasons.GetValueOrDefault("TooClose") + 1;
                failedWaypointsBuffer.Add(new Models.FailedWaypoint { Lat = candidate.Lat, Lon = candidate.Lon, Reason = "TooClose", DistanceFromCurrentPosM = RouteGeometryUtils.HaversineDistance(currentPos, candidate) });
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

                if (bearingDelta > 150)
                {
                    ctx.WaypointRejectionReasons["Backtracking"] = ctx.WaypointRejectionReasons.GetValueOrDefault("Backtracking") + 1;
                    failedWaypointsBuffer.Add(new Models.FailedWaypoint { Lat = candidate.Lat, Lon = candidate.Lon, Reason = "Backtracking", DistanceFromCurrentPosM = RouteGeometryUtils.HaversineDistance(currentPos, candidate) });
                    consecutiveFailures++;
                    continue;
                }
            }

            double candidateDistKm = RouteGeometryUtils.HaversineDistance(currentPos, candidate) / 1000;
            if (candidateDistKm > targetDistKm * 0.40)
            {
                ctx.WaypointRejectionReasons["TooFar"] = ctx.WaypointRejectionReasons.GetValueOrDefault("TooFar") + 1;
                failedWaypointsBuffer.Add(new Models.FailedWaypoint { Lat = candidate.Lat, Lon = candidate.Lon, Reason = "TooFar", DistanceFromCurrentPosM = RouteGeometryUtils.HaversineDistance(currentPos, candidate) });
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
                        int pushedDensity = _edgeSpatialIndex.CountInRadius(pushedCandidate, 5000);
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
                failedWaypointsBuffer.Add(new Models.FailedWaypoint { Lat = candidate.Lat, Lon = candidate.Lon, Reason = "NoConnectivity", DistanceFromCurrentPosM = RouteGeometryUtils.HaversineDistance(currentPos, candidate) });
                consecutiveFailures++;
                continue;
            }
            connSw.Stop();
            ctx.ConnectivityCheckMs += connSw.ElapsedMilliseconds;

            var segSw = System.Diagnostics.Stopwatch.StartNew();

            double haversineSegDist = RouteGeometryUtils.HaversineDistance(currentPos, candidate);
            double estimatedRoutedDist = haversineSegDist * 1.5;
            if (estimatedRoutedDist > targetDistKm * 0.40 * 1000)
            {
                ctx.WaypointRejectionReasons["SegmentTooLong"] = ctx.WaypointRejectionReasons.GetValueOrDefault("SegmentTooLong") + 1;
                failedWaypointsBuffer.Add(new Models.FailedWaypoint { Lat = candidate.Lat, Lon = candidate.Lon, Reason = "SegmentTooLong", DistanceFromCurrentPosM = haversineSegDist });
                segSw.Stop();
                ctx.TotalSegmentBuildMs += segSw.ElapsedMilliseconds;
                _waypointGenerator.BlockSector(targetAngle, blockedSectors);
                consecutiveFailures++;
                continue;
            }

            var (seg, segPushRerouted, segOverlapBefore, segOverlapAfter, pushAttemptsUsed) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, candidate, allUsedEdges, edgeAge, segmentCounter);
            ctx.TotalPushAttemptsUsed += pushAttemptsUsed;
            if (seg == null)
            {
                nullSegmentDrops++;
                failedWaypointsBuffer.Add(new Models.FailedWaypoint { Lat = candidate.Lat, Lon = candidate.Lon, Reason = "NoRouteFound", DistanceFromCurrentPosM = haversineSegDist });
                _waypointGenerator.BlockSector(targetAngle, blockedSectors);
                consecutiveFailures++;
                continue;
            }

            double segLengthM = RouteGeometryUtils.CalculateDistance(seg);

            if (segLengthM > targetDistKm * 0.40 * 1000)
            {
                ctx.WaypointRejectionReasons["SegmentTooLong"] = ctx.WaypointRejectionReasons.GetValueOrDefault("SegmentTooLong") + 1;
                segSw.Stop();
                ctx.TotalSegmentBuildMs += segSw.ElapsedMilliseconds;
                _waypointGenerator.BlockSector(targetAngle, blockedSectors);
                consecutiveFailures++;
                continue;
            }

            DebugStemEvent? stemEvt = null;
            bool fixApplied = false;
            bool fixTimedOut = false;

                bool isOverlapping = segOverlapAfter > 0.15;

            var stemSw = System.Diagnostics.Stopwatch.StartNew();
            bool isGeometryStem = StemDetector.IsStemSegment(seg, strict: true, out int stemCc, out int stemOc, out int stemEx, out int stemFh, out int stemSh, out double stemNearMiss);
            stemSw.Stop();
            ctx.StemDetectionMs += stemSw.ElapsedMilliseconds;
            if (isOverlapping || isGeometryStem)
            {
                // AnalyzeStemRootCause: only called for stems (saves 9 TryResolve calls per non-stem segment)
                var (preCheckRootCause, preCheckStartBr, preCheckEndBr, preCheckBearingDelta, preCheckConnectivity, preCheckMaxDev, preCheckStraightRatio) = StemDetector.AnalyzeStemRootCause(seg, candidate, _mapRepository.Router!, _mapRepository.RouterDb!, profile);

                consecutiveStemSegments++;
                if (consecutiveStemSegments > maxConsecutiveStemSegments)
                    maxConsecutiveStemSegments = consecutiveStemSegments;
                double segBearing = RouteGeometryUtils.ComputeBearing(currentPos, candidate);
                var midpoint = RouteGeometryUtils.ComputeMidpoint(seg);
                double distFromStart = RouteGeometryUtils.HaversineDistance(start, midpoint);
                double sectorFromStart = RouteGeometryUtils.ComputeBearing(start, midpoint);
                string? segRoadType = _roadClassifier.GetHighwayType(midpoint, profile);
                double segCurvature = RouteGeometryUtils.CalculateAverageCurvature(seg);
                int segEdgeCount = RouteGeometryUtils.CountEdges(seg);
                var candidateQuality = _roadClassifier.ClassifyRoad(candidate, profile, avoidHighways);
                stemEvt = new DebugStemEvent
                {
                    AttemptNumber = attemptNumber,
                    SegmentRole = "forward",
                    SegmentIndex = committedCount,
                    SegmentLengthM = segLengthM,
                    CumulativeDistanceM = totalDistKm * 1000,
                    SegmentBearing = Math.Round(segBearing, 1),
                    FirstHalfPoints = stemFh,
                    SecondHalfPoints = stemSh,
                    CloseCount = stemCc,
                    OpposedCount = stemOc,
                    Examined = stemEx,
                    CloseRatio = stemEx > 0 ? (double)stemCc / stemEx : 0,
                    OpposedRatio = stemEx > 0 ? (double)stemOc / stemEx : 0,
                    IsStem = isGeometryStem,
                    OverlapWithPriorSegments = segOverlapAfter,
                    OriginalWaypoint = new[] { candidate.Lat, candidate.Lon },
                    PushRerouted = segPushRerouted,
                    PushOverlapBefore = segOverlapBefore,
                    PushOverlapAfter = segOverlapAfter,
                    RoadType = segRoadType,
                    MidpointLat = midpoint.Lat,
                    MidpointLon = midpoint.Lon,
                    DistanceFromStartM = distFromStart,
                    SectorFromStart = Math.Round(sectorFromStart, 1),
                    SegmentCurvature = Math.Round(segCurvature, 4),
                    EdgeCount = segEdgeCount,
                    CandidateRoadClass = candidateQuality.ToString(),
                    StemCause = isOverlapping ? Models.StemCause.OverlapWithPrior :
                                segLengthM > targetDistKm * 0.40 * 1000 ? Models.StemCause.LongSegment :
                                Models.StemCause.Backtracking,
                    NearestNearMissM = Math.Round(stemNearMiss, 0),
                    ConsecutiveStemCount = consecutiveStemSegments,
                    // Session 9: New diagnostic fields
                    HaversineToRoutedRatio = segLengthM > 0 ? Math.Round(haversineSegDist / segLengthM, 3) : 0,
                    ConnectivityAtStart = preCheckConnectivity,
                    RoadQualityAtMidpoint = _roadClassifier.GetHighwayType(midpoint, profile),
                    SegmentEdgeDensityAtMidpoint = _edgeSpatialIndex.CountInRadius(midpoint, 5000),
                    TimeSinceLastStem = lastStemSegmentIndex >= 0 ? committedCount - lastStemSegmentIndex : -1,
                };
                // Store root cause analysis results
                stemEvt.RootCause = preCheckRootCause;
                stemEvt.StartBearing = Math.Round(preCheckStartBr, 1);
                stemEvt.EndBearing = Math.Round(preCheckEndBr, 1);
                stemEvt.BearingDelta = Math.Round(preCheckBearingDelta, 1);
                stemEvt.EndpointConnectivity = preCheckConnectivity;
                stemEvt.MaxDeviationFromLine = Math.Round(preCheckMaxDev, 0);
                stemEvt.StraightLineRatio = Math.Round(preCheckStraightRatio, 2);
                stemEvt.RootCauseAnalyzed = true;

                RouteGeometryUtils.CaptureOverlappingEdgeIds(stemEvt, seg, allUsedEdges);

                // Gap 3: Assign failed waypoints buffer
                if (failedWaypointsBuffer.Count > 0)
                    stemEvt.FailedWaypoints = new List<Models.FailedWaypoint>(failedWaypointsBuffer);

                // Fix 1: Cascade Detection - check if this is a chain-reaction stem
                bool cascadeDetected = false;
                if (consecutiveStemSegments >= 3 && previousStemPositions.Count > 0)
                {
                    var midpointForCascade = RouteGeometryUtils.ComputeMidpoint(seg);
                    foreach (var prevPos in previousStemPositions)
                    {
                        double distToPrev = RouteGeometryUtils.HaversineDistance(midpointForCascade, prevPos);
                        if (distToPrev < 5000)
                        {
                            cascadeDetected = true;
                            break;
                        }
                    }
                }

                if (cascadeDetected)
                {
                    stemEvt.Resolution = "cascade_aborted";
                    stemEvt.FixStrategy = null;
                    _diagnostics.Add(stemEvt);
                    lastStemSegmentIndex = committedCount;
                    _waypointGenerator.BlockSector(targetAngle, blockedSectors);
                    consecutiveFailures++;
                    continue;
                }

                // Track this stem position for cascade detection
                var stemMidpoint = RouteGeometryUtils.ComputeMidpoint(seg);
                previousStemPositions.Add(stemMidpoint);

                // Root-cause-aware stem fix pipeline
                var fixResult = ApplyRootCauseFixPipeline(profile, currentPos, candidate, seg, stemEvt, allUsedEdges, avoidHighways, segOverlapAfter, targetAngle, blockedSectors, ctx, allSegments, allPoints, ref totalDistKm);
                if (fixResult.HasValue)
                {
                    var (newSeg, newCandidate, newFixApplied, newFixTimedOut) = fixResult.Value;
                    seg = newSeg;
                    candidate = newCandidate;
                    fixApplied = newFixApplied;
                    fixTimedOut = newFixTimedOut;
                }

                if (fixTimedOut && !fixApplied)
                {
                    stemEvt.Resolution = "timeout_accepted";
                    stemEvt.FinalWaypoint = new[] { candidate.Lat, candidate.Lon };
                    stemEvt.FinalWaypointDistanceM = RouteGeometryUtils.HaversineDistance(
                        new Coordinate(stemEvt.OriginalWaypoint[0], stemEvt.OriginalWaypoint[1]),
                        candidate);
                    _diagnostics.Add(stemEvt);
                    lastStemSegmentIndex = committedCount;
                }

                if (fixApplied)
                {
                    stemEvt.FinalWaypoint = new[] { candidate.Lat, candidate.Lon };
                    stemEvt.FinalWaypointDistanceM = RouteGeometryUtils.HaversineDistance(
                        new Coordinate(stemEvt.OriginalWaypoint[0], stemEvt.OriginalWaypoint[1]),
                        candidate);
                    _diagnostics.Add(stemEvt);
                    lastStemSegmentIndex = committedCount;
                }
            }
            segSw.Stop();
            ctx.TotalSegmentBuildMs += segSw.ElapsedMilliseconds;
            if (stemEvt != null)
                stemEvt.SegmentBuildMs = segSw.ElapsedMilliseconds;

            if (avoidHighways)
            {
                (seg, _) = RerouteIfMotorwaysBlocked(seg!, profile, currentPos, candidate, allUsedEdges, ctx.BlockedMotorwayEdges, avoidHighways);
            }

            // Post-fix overlap re-check: reject segment if fix didn't improve overlap
            if (isOverlapping && !fixApplied && !fixTimedOut)
            {
                double finalOverlap = RouteGeometryUtils.CalculateSegmentOverlap(seg!, allUsedEdges);
                if (finalOverlap > 0.25)
                {
                    if (stemEvt != null)
                    {
                        stemEvt.Resolution = "postFixOverlap_rejected";
                        _diagnostics.Add(stemEvt);
                    }
                    _waypointGenerator.BlockSector(targetAngle, blockedSectors);
                    consecutiveFailures++;
                    continue;
                }
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
                if (runningOverlapRatio > 0.12)
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
                        int sector = (int)(bearing / 20) % 18;
                        sectorUsage[sector] = sectorUsage.GetValueOrDefault(sector) + 1;
                    }
                    foreach (var kvp in sectorUsage.OrderByDescending(x => x.Value).Take(3))
                        blockedSectors.Add(kvp.Key);
                }
            }

            if (stemEvt == null)
            {
                double segBearing = RouteGeometryUtils.ComputeBearing(currentPos, candidate);
                var midpoint = RouteGeometryUtils.ComputeMidpoint(seg);
                double distFromStart = RouteGeometryUtils.HaversineDistance(start, midpoint);
                double sectorFromStart = RouteGeometryUtils.ComputeBearing(start, midpoint);
                string? segRoadType = _roadClassifier.GetHighwayType(midpoint, profile);
                double segCurvature = RouteGeometryUtils.CalculateAverageCurvature(seg);
                int segEdgeCount = RouteGeometryUtils.CountEdges(seg);
                var candidateQuality = _roadClassifier.ClassifyRoad(candidate, profile, avoidHighways);
                var committedEvt = new DebugStemEvent
                {
                    AttemptNumber = attemptNumber,
                    SegmentRole = "forward",
                    SegmentIndex = committedCount,
                    SegmentLengthM = segLengthM,
                    CumulativeDistanceM = (totalDistKm - segLengthM / 1000) * 1000,
                    SegmentBearing = Math.Round(segBearing, 1),
                    IsStem = false, // Already determined by isGeometryStem check on line 534
                    CloseCount = 0,
                    OpposedCount = 0,
                    Examined = 0,
                    FirstHalfPoints = 0,
                    SecondHalfPoints = 0,
                    OverlapWithPriorSegments = segOverlapAfter,
                    OriginalWaypoint = new[] { candidate.Lat, candidate.Lon },
                    FinalWaypoint = new[] { candidate.Lat, candidate.Lon },
                    Resolution = "committed",
                    PushRerouted = segPushRerouted,
                    PushOverlapBefore = segOverlapBefore,
                    PushOverlapAfter = segOverlapAfter,
                    RoadType = segRoadType,
                    MidpointLat = midpoint.Lat,
                    MidpointLon = midpoint.Lon,
                    DistanceFromStartM = distFromStart,
                    SectorFromStart = Math.Round(sectorFromStart, 1),
                    SegmentCurvature = Math.Round(segCurvature, 4),
                    EdgeCount = segEdgeCount,
                    CandidateRoadClass = candidateQuality.ToString(),
                    NearestNearMissM = 0,
                    ConsecutiveStemCount = 0,
                    // Session 9: New diagnostic fields
                    HaversineToRoutedRatio = segLengthM > 0 ? Math.Round(haversineSegDist / segLengthM, 3) : 0,
                    RoadQualityAtMidpoint = segRoadType,
                    SegmentEdgeDensityAtMidpoint = _edgeSpatialIndex.CountInRadius(midpoint, 5000),
                    TimeSinceLastStem = lastStemSegmentIndex >= 0 ? committedCount - lastStemSegmentIndex : -1,
                };

                RouteGeometryUtils.CaptureOverlappingEdgeIds(committedEvt, seg, allUsedEdges);

                // Gap 6: Root cause analysis on non-stem segments with overlap > 10%
                if (segOverlapAfter > 0.10)
                {
                    var (rootCause, startBr, endBr, bearingDelta, connectivity, maxDev, straightRatio) = StemDetector.AnalyzeStemRootCause(seg, candidate, _mapRepository.Router!, _mapRepository.RouterDb!, profile);
                    committedEvt.RootCause = rootCause;
                    committedEvt.StartBearing = Math.Round(startBr, 1);
                    committedEvt.EndBearing = Math.Round(endBr, 1);
                    committedEvt.BearingDelta = Math.Round(bearingDelta, 1);
                    committedEvt.EndpointConnectivity = connectivity;
                    committedEvt.MaxDeviationFromLine = Math.Round(maxDev, 0);
                    committedEvt.StraightLineRatio = Math.Round(straightRatio, 2);
                    committedEvt.RootCauseAnalyzed = true;
                }

                // Gap 3: Assign failed waypoints buffer
                if (failedWaypointsBuffer.Count > 0)
                    committedEvt.FailedWaypoints = new List<Models.FailedWaypoint>(failedWaypointsBuffer);

                _diagnostics.Add(committedEvt);
            }
            committedCount++;
            consecutiveFailures = 0;
            failedWaypointsBuffer.Clear();
            if (stemEvt == null)
                consecutiveStemSegments = 0;
        }

        // Capture forward distance after loop exits
        forwardDistanceKm = totalDistKm;

        // Capture forward loop exit diagnostics
        ctx.EstReturnAtLoopExit = lastEstReturnWithMultiplierKm;
        ctx.ForwardHaversineAtExit = lastEstReturnHaversineKm;
        ctx.SectorsBlockedAtReturn = _waypointGenerator.BlockedSectorCounts.Count(c => c > 0);

        // Circularity diagnostics
        ctx.ForwardSegmentCount = committedCount;

        // Update context with tracking values
        ctx.MaxConsecutiveStemSegments = maxConsecutiveStemSegments;
        ctx.NullSegmentDrops = nullSegmentDrops;

        Array.Copy(_waypointGenerator.BlockedSectorCounts, ctx.BlockedSectorCounts, 18);

        if (allSegments.Count < 2) return null;

        // --- Part 1: Return Segment Pre-Planning (Enhanced Homing Waypoints) ---
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
            double perpOffset = 1500 * Math.Sin(hi * Math.PI / Math.Max(homingCount, 1)); // 1.5km offset, alternating sides
            double perpAngle = bearingToStart + Math.PI / 2;

            bool homingFound = false;

            for (int homingAngleIdx = 0; homingAngleIdx < 13; homingAngleIdx++)
            {
                double angleOffset = (homingAngleIdx - 6) * 5 * Math.PI / 180; // -30° to +30° in 5° steps
                double homingAngle = bearingToStart + angleOffset;

                // Use the new homing waypoint generator with edge avoidance
                var homingCandidate = _waypointGenerator.GenerateHomingWaypoint(
                    profile, currentPos, start, homingDistKm, bearingToStart, angleOffset, avoidHighways, allUsedEdges);

                if (homingCandidate == null) continue;

                // Additional checks
                double candDist = RouteGeometryUtils.HaversineDistance(currentPos, homingCandidate) / 1000;
                if (candDist < 3) continue;

                // Check road connectivity
                if (!_roadClassifier.HasRoadConnectivity(profile, homingCandidate, cosLat)) continue;

                // Accept this homing waypoint
                var (homingSeg, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(
                    profile, currentPos, homingCandidate, allUsedEdges);
                if (homingSeg == null) continue;

                double homingSegOverlap = RouteGeometryUtils.CalculateSegmentOverlap(homingSeg, allUsedEdges);
                if (homingSegOverlap > 0.20) continue;

                // Commit homing segment
                if (avoidHighways)
                {
                    (homingSeg, _) = RerouteIfMotorwaysBlocked(homingSeg, profile, currentPos, homingCandidate, allUsedEdges, ctx.BlockedMotorwayEdges, avoidHighways);
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
                    IsStem = false,
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

        ctx.HomingWaypointsUsed = homingWaypointsUsed;

        DebugStemEvent? returnStemEvt = null;
        var (returnSeg, _, _, _, retPushUsed) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, start, allUsedEdges, edgeAge, segmentCounter);
        double returnOverlapBeforePush = 0;
        int returnPushAttemptsUsed = retPushUsed;
        int returnRerouteCount = 0;
        if (returnSeg != null)
        {
            var forwardEdgeKeys = RouteGeometryUtils.BuildForwardEdgeKeySet(allSegments);
            double forwardOverlap = RouteGeometryUtils.CalculateSegmentOverlap(returnSeg, forwardEdgeKeys);
            returnOverlapBeforePush = forwardOverlap;
            bool retIsStem = StemDetector.IsStemSegment(returnSeg, strict: true, out int retCc, out int retOc, out int retEx, out int retFh, out int retSh, out double retNearMiss);

            if (forwardOverlap > 0.30 || retIsStem)
            {
                returnStemEvt = new DebugStemEvent
                {
                    AttemptNumber = attemptNumber,
                    SegmentRole = "return",
                    SegmentLengthM = RouteGeometryUtils.CalculateDistance(returnSeg),
                    FirstHalfPoints = retFh,
                    SecondHalfPoints = retSh,
                    CloseCount = retCc,
                    OpposedCount = retOc,
                    Examined = retEx,
                    CloseRatio = retEx > 0 ? (double)retCc / retEx : 0,
                    OpposedRatio = retEx > 0 ? (double)retOc / retEx : 0,
                    IsStem = retIsStem,
                    OverlapWithPriorSegments = forwardOverlap,
                    OriginalWaypoint = new[] { start.Lat, start.Lon },
                    EstReturnHaversineKm = Math.Round(lastEstReturnHaversineKm, 2),
                    EstReturnWithMultiplierKm = Math.Round(lastEstReturnWithMultiplierKm, 2),
                    ActualReturnRoutedKm = Math.Round(RouteGeometryUtils.CalculateDistance(returnSeg) / 1000, 2),
                    NearestNearMissM = Math.Round(retNearMiss, 0),
                };

                bool returnFixApplied = false;

                // --- Return Step 1: GenerateReplacementWaypoint (light touch) ---
                var (replacement, grRetDetails) = _stemFixer.GenerateReplacementWaypoint(profile, start, currentPos, avoidHighways, RouteGeometryUtils.CalculateDistance(returnSeg!));
                _stemFixer.IncrementReplacementCall();
                if (replacement != null) _stemFixer.IncrementReplacementSuccess();
                if (replacement != null)
                {
                    returnStemEvt.GenerateReplacement = new DebugFixStep { Attempted = true };
                    var (replSeg, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, replacement, allUsedEdges);
                    if (replSeg != null && !StemDetector.IsStemSegment(replSeg, strict: true))
                    {
                        allUsedEdges.UnionWith(RouteGeometryUtils.ExtractEdges(replSeg));
                        allSegments.Add(replSeg);
                        allPoints.Add(replacement);
                        currentPos = replacement;
                        var (newReturnSeg, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, start, allUsedEdges, edgeAge, segmentCounter);
                        returnSeg = newReturnSeg;
                        // Fix: Add null check to prevent NullReferenceException
                        if (returnSeg != null)
                        {
                            var genRecheckOverlap = RouteGeometryUtils.CalculateSegmentOverlap(returnSeg, forwardEdgeKeys);
                            if (genRecheckOverlap <= 0.10)
                            {
                                returnStemEvt.GenerateReplacement.Succeeded = true;
                                returnStemEvt.FixStrategy = "replacement";
                                returnStemEvt.Resolution = "replaced";
                                returnFixApplied = true;
                                returnRerouteCount++;
                            }
                            else
                            {
                                returnStemEvt.GenerateReplacement.Succeeded = false;
                                returnStemEvt.GenerateReplacement.FailureReason = $"Re-routed return still overlaps {genRecheckOverlap:P0} with forward path";
                            }
                        }
                        else
                        {
                            returnStemEvt.GenerateReplacement.Succeeded = false;
                            returnStemEvt.GenerateReplacement.FailureReason = "No route for return segment after replacement";
                        }
                    }
                    else
                    {
                        returnStemEvt.GenerateReplacement.Succeeded = false;
                        returnStemEvt.GenerateReplacement.FailureReason = replSeg == null ? "No route to replacement" : "Replacement route is also a stem";
                    }
                }
                else
                {
                    returnStemEvt.GenerateReplacement = new DebugFixStep { Attempted = true, Succeeded = false, FailureReason = "No replacement waypoint found", Details = grRetDetails };
                }

                // --- Return Step 2: Intermediate midpoint (light touch) ---
                if (!returnFixApplied)
                {
                    double currentOverlap = returnSeg != null ? RouteGeometryUtils.CalculateSegmentOverlap(returnSeg, forwardEdgeKeys) : 0;
                    if (returnSeg != null && currentOverlap > 0.10)
                    {
                        var midLat = (currentPos.Lat + start.Lat) / 2;
                        var midLon = (currentPos.Lon + start.Lon) / 2;
                        var intermediate = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(midLat, midLon), 3000);
                        if (intermediate != null && RouteGeometryUtils.HaversineDistance(intermediate, currentPos) > 500
                            && RouteGeometryUtils.HaversineDistance(intermediate, start) > 500)
                        {
                            var intermediateQuality = _roadClassifier.ClassifyRoad(intermediate, profile, avoidHighways);
                            if (intermediateQuality == RoadClassifier.RoadQuality.Preferred || intermediateQuality == RoadClassifier.RoadQuality.Acceptable)
                            {
                                var (viaSeg, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, intermediate, allUsedEdges);
                                if (viaSeg != null && !StemDetector.IsStemSegment(viaSeg, strict: true))
                                {
                                    allUsedEdges.UnionWith(RouteGeometryUtils.ExtractEdges(viaSeg));
                                    allSegments.Add(viaSeg);
                                    allPoints.Add(intermediate);
                                    currentPos = intermediate;
                                    var (_returnSegTemp, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, start, allUsedEdges, edgeAge, segmentCounter);
                                    returnSeg = _returnSegTemp;
                                    returnFixApplied = true;
                                    returnRerouteCount++;
                                    returnStemEvt.Intermediate = new DebugFixStep { Attempted = true, Succeeded = true };
                                    returnStemEvt.FixStrategy = "intermediate";
                                    returnStemEvt.Resolution = "intermediate";
                                }
                                else
                                {
                                    returnStemEvt.Intermediate = new DebugFixStep { Attempted = true, Succeeded = false, FailureReason = "Intermediate route is also a stem" };
                                }
                            }
                            else
                            {
                                returnStemEvt.Intermediate = new DebugFixStep { Attempted = true, Succeeded = false, FailureReason = $"Intermediate on {intermediateQuality} road" };
                            }
                        }
                        else
                        {
                            returnStemEvt.Intermediate = new DebugFixStep { Attempted = true, Succeeded = false, FailureReason = intermediate == null ? "No road near midpoint" : "Midpoint too close" };
                        }
                    }
                }

                // --- Return Step 3: TryFixStem (aggressive, blocks edges) ---
                // Early exit: skip expensive fix stages if overlap is already acceptable
                double currentReturnOverlap = returnSeg != null ? RouteGeometryUtils.CalculateSegmentOverlap(returnSeg, forwardEdgeKeys) : 0;
                if (!returnFixApplied && returnSeg != null && currentReturnOverlap > 0.20)
                {
                    var (fixedReturn, tsRetDetails, retFixReasonCode) = _stemFixer.TryFixStem(profile, currentPos, start, returnSeg, allUsedEdges);
                    _stemFixer.IncrementFixStemCall();
                    if (fixedReturn != null) _stemFixer.IncrementFixStemSuccess();
                    if (fixedReturn != null)
                    {
                        returnSeg = fixedReturn;
                        double recheckOverlap = RouteGeometryUtils.CalculateSegmentOverlap(returnSeg, forwardEdgeKeys);
                        if (recheckOverlap <= 0.25)
                        {
                            returnFixApplied = true;
                            returnRerouteCount++;
                            returnStemEvt.TryFixStem = new DebugFixStep { Attempted = true, Succeeded = true };
                            returnStemEvt.FixStrategy = "tryFixStem";
                            returnStemEvt.Resolution = "fixedAndKept";
                        }
                        else
                        {
                            returnStemEvt.TryFixStem = new DebugFixStep { Attempted = true, Succeeded = false, FailureReason = $"Fixed route still overlaps {recheckOverlap:P0} with forward path", Details = new Dictionary<string, double> { ["recheckOverlap"] = recheckOverlap } };
                        }
                    }
                    else
                    {
                        returnStemEvt.TryFixStem = new DebugFixStep { Attempted = true, Succeeded = false, FailureReason = "No alternative return route found", Details = tsRetDetails };
                    }
                }

                // --- Return Step 4: TryMultiHopSplit (last resort) ---
                // Early exit: skip MultiHop if overlap is already acceptable
                if (!returnFixApplied)
                {
                    double returnOverlapForCheck = returnSeg != null ? RouteGeometryUtils.CalculateSegmentOverlap(returnSeg, forwardEdgeKeys) : 0;
                    if (returnOverlapForCheck <= 0.20)
                    {
                        // Overlap is acceptable, skip expensive MultiHop
                        returnFixApplied = true;
                        returnStemEvt.Resolution = "returnAcceptable";
                    }
                }

                if (!returnFixApplied)
                {
                    var mhSw = System.Diagnostics.Stopwatch.StartNew();
                    var (hopMid, hop1Seg, hop2Seg, details) = returnSeg != null ? _stemFixer.TryMultiHopSplit(profile, currentPos, start, returnSeg, allUsedEdges, avoidHighways) : (null, null, null, new Dictionary<string, double>());
                    mhSw.Stop();
                    _stemFixer.AddMultiHopTime(mhSw.ElapsedMilliseconds);
                    _stemFixer.IncrementMultiHopCall();
                    if (hopMid != null) _stemFixer.IncrementMultiHopSuccess();
                    if (hopMid != null)
                    {
                        double hop2TotalOverlap = RouteGeometryUtils.CalculateSegmentOverlap(hop2Seg!, forwardEdgeKeys);
                        if (hop2TotalOverlap > 0.15)
                        {
                            returnStemEvt.Intermediate = new DebugFixStep { Attempted = true, Succeeded = false, FailureReason = $"Multi-hop hop2 still overlaps {hop2TotalOverlap:P0} with forward path", Details = details };
                        }
                        else
                        {
                            allUsedEdges.UnionWith(RouteGeometryUtils.ExtractEdges(hop1Seg!));
                            allSegments.Add(hop1Seg!);
                            allPoints.Add(hopMid);
                            currentPos = hopMid;
                            totalDistKm += RouteGeometryUtils.CalculateDistance(hop1Seg!) / 1000;
                            returnSeg = hop2Seg;
                            returnFixApplied = true;
                            returnRerouteCount++;
                            returnStemEvt.Intermediate = new DebugFixStep { Attempted = true, Succeeded = true, Details = details };
                            returnStemEvt.FixStrategy = "multiHop";
                            returnStemEvt.Resolution = "multiHop";
                            returnStemEvt.HopCount = 2;
                        }
                    }
                    else
                    {
                        returnStemEvt.Intermediate = new DebugFixStep { Attempted = true, Succeeded = false, FailureReason = "Multi-hop split failed", Details = details };
                    }
                }

                if (!returnFixApplied)
                {
                    returnStemEvt.Resolution = "fixFailed_rejected";
                    returnSeg = null;
                }

                returnStemEvt.FinalWaypoint = new[] { start.Lat, start.Lon };
                _diagnostics.Add(returnStemEvt);
            }
            else
            {
                _diagnostics.Add(new DebugStemEvent
                {
                    AttemptNumber = attemptNumber,
                    SegmentRole = "return",
                    SegmentLengthM = RouteGeometryUtils.CalculateDistance(returnSeg),
                    IsStem = false,
                    OverlapWithPriorSegments = forwardOverlap,
                    CloseCount = retCc,
                    OpposedCount = retOc,
                    Examined = retEx,
                    FirstHalfPoints = retFh,
                    SecondHalfPoints = retSh,
                    CloseRatio = retEx > 0 ? (double)retCc / retEx : 0,
                    OpposedRatio = retEx > 0 ? (double)retOc / retEx : 0,
                    OriginalWaypoint = new[] { start.Lat, start.Lon },
                    FinalWaypoint = new[] { start.Lat, start.Lon },
                    Resolution = "committed",
                    NearestNearMissM = Math.Round(retNearMiss, 0),
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
                (returnSeg, _) = RerouteIfMotorwaysBlocked(returnSeg, profile, currentPos, start, allUsedEdges, ctx.BlockedMotorwayEdges, avoidHighways);
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
    /// Root-cause-aware stem fix pipeline.
    /// Uses StemRootCause to select the best fix strategy order.
    /// GraphHopper-inspired: witness path verification for stem alternatives.
    /// </summary>
    private (List<Coordinate>? seg, Coordinate candidate, bool fixApplied, bool fixTimedOut)? ApplyRootCauseFixPipeline(
        IProfileInstance profile,
        Coordinate currentPos,
        Coordinate candidate,
        List<Coordinate>? seg,
        DebugStemEvent stemEvt,
        HashSet<RouteGeometryUtils.EdgeKey> allUsedEdges,
        bool avoidHighways,
        double segOverlapAfter,
        double targetAngle,
        HashSet<int> blockedSectors,
        BuildContext ctx,
        List<List<Coordinate>> allSegments,
        List<Coordinate> allPoints,
        ref double totalDistKm)
    {
        bool fixApplied = false;
        bool fixTimedOut = false;
        var fixPipelineSw = System.Diagnostics.Stopwatch.StartNew();

        // Select fix strategy order based on root cause
        var fixStrategies = GetFixStrategiesForRootCause(stemEvt.RootCause);

        foreach (var strategy in fixStrategies)
        {
            if (fixApplied || fixTimedOut) break;

            // Check timeout
            if (fixPipelineSw.ElapsedMilliseconds > 8000)
            {
                fixTimedOut = true;
                break;
            }

            switch (strategy)
            {
                case FixStrategy.Replacement:
                    fixApplied = TryReplacementFix(profile, currentPos, ref candidate, ref seg, stemEvt, allUsedEdges, avoidHighways, segOverlapAfter, fixPipelineSw);
                    break;

                case FixStrategy.Intermediate:
                    fixApplied = TryIntermediateFix(profile, currentPos, ref candidate, ref seg, stemEvt, allUsedEdges, avoidHighways, segOverlapAfter, fixPipelineSw, ctx);
                    break;

                case FixStrategy.WitnessPath:
                    fixApplied = TryWitnessPathFix(profile, currentPos, ref candidate, ref seg, stemEvt, allUsedEdges, avoidHighways, segOverlapAfter, fixPipelineSw);
                    break;

                case FixStrategy.EdgeBlocking:
                    if (segOverlapAfter > 0.30)
                    {
                        fixApplied = TryEdgeBlockingFix(profile, currentPos, ref candidate, ref seg, stemEvt, allUsedEdges, avoidHighways, segOverlapAfter, fixPipelineSw);
                    }
                    break;

                case FixStrategy.MultiHop:
                    fixApplied = TryMultiHopFix(profile, currentPos, ref candidate, ref seg, stemEvt, allUsedEdges, avoidHighways, fixPipelineSw, ctx, allSegments, allPoints, ref totalDistKm);
                    break;
            }
        }

        fixPipelineSw.Stop();
        stemEvt.FixPipelineMs = fixPipelineSw.ElapsedMilliseconds;

        if (!fixApplied && !fixTimedOut)
        {
            stemEvt.Resolution = "dropped";
            _diagnostics.Add(stemEvt);
            _waypointGenerator.BlockSector(targetAngle, blockedSectors);
        }

        return (seg, candidate, fixApplied, fixTimedOut);
    }

    private enum FixStrategy
    {
        Replacement,
        Intermediate,
        WitnessPath,
        EdgeBlocking,
        MultiHop
    }

    private List<FixStrategy> GetFixStrategiesForRootCause(StemRootCause rootCause)
    {
        return rootCause switch
        {
            StemRootCause.DeadEndRoad => new List<FixStrategy> { FixStrategy.Intermediate, FixStrategy.Replacement, FixStrategy.MultiHop },
            StemRootCause.OneWayStreet => new List<FixStrategy> { FixStrategy.Replacement, FixStrategy.Intermediate, FixStrategy.MultiHop },
            StemRootCause.OvershootBacktrack => new List<FixStrategy> { FixStrategy.Replacement, FixStrategy.WitnessPath, FixStrategy.Intermediate },
            StemRootCause.TerrainDetour => new List<FixStrategy> { FixStrategy.Intermediate, FixStrategy.WitnessPath, FixStrategy.Replacement },
            StemRootCause.PrivateRoad => new List<FixStrategy> { FixStrategy.Replacement, FixStrategy.Intermediate, FixStrategy.MultiHop },
            StemRootCause.NoDirectRoad => new List<FixStrategy> { FixStrategy.Intermediate, FixStrategy.MultiHop, FixStrategy.Replacement },
            _ => new List<FixStrategy> { FixStrategy.Replacement, FixStrategy.Intermediate, FixStrategy.EdgeBlocking, FixStrategy.MultiHop }
        };
    }

    private bool TryReplacementFix(
        IProfileInstance profile,
        Coordinate currentPos,
        ref Coordinate candidate,
        ref List<Coordinate>? seg,
        DebugStemEvent stemEvt,
        HashSet<RouteGeometryUtils.EdgeKey> allUsedEdges,
        bool avoidHighways,
        double segOverlapAfter,
        System.Diagnostics.Stopwatch fixPipelineSw)
    {
        var replSw = System.Diagnostics.Stopwatch.StartNew();
        var (replacement, grFwdDetails) = _stemFixer.GenerateReplacementWaypoint(profile, candidate, currentPos, avoidHighways, RouteGeometryUtils.CalculateDistance(seg!));
        _stemFixer.IncrementReplacementCall();
        if (replacement != null) _stemFixer.IncrementReplacementSuccess();
        replSw.Stop();
        _stemFixer.AddReplacementTime(replSw.ElapsedMilliseconds);

        if (replacement != null)
        {
            var (replSeg, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, replacement, allUsedEdges);
            if (replSeg != null && !StemDetector.IsStemSegment(replSeg, strict: true))
            {
                double replOverlap = RouteGeometryUtils.CalculateSegmentOverlap(replSeg, allUsedEdges);
                if (replOverlap < segOverlapAfter)
                {
                    seg = replSeg;
                    candidate = replacement;
                    stemEvt.GenerateReplacement = new DebugFixStep { Attempted = true, Succeeded = true, OverlapBefore = segOverlapAfter, OverlapAfter = replOverlap, FixAttemptMs = replSw.ElapsedMilliseconds };
                    stemEvt.FixStrategy = "replacement";
                    stemEvt.Resolution = "replaced";
                    return true;
                }
                else
                {
                    stemEvt.GenerateReplacement = new DebugFixStep { Attempted = true, Succeeded = false, FailureReason = $"Replacement overlap {replOverlap:F3} not better than original {segOverlapAfter:F3}", OverlapBefore = segOverlapAfter, OverlapAfter = replOverlap, FixAttemptMs = replSw.ElapsedMilliseconds };
                }
            }
            else
            {
                stemEvt.GenerateReplacement = new DebugFixStep { Attempted = true, Succeeded = false, FailureReason = replSeg == null ? "No route to replacement" : "Replacement route is also a stem", OverlapBefore = segOverlapAfter, FixAttemptMs = replSw.ElapsedMilliseconds };
            }
        }
        else
        {
            stemEvt.GenerateReplacement = new DebugFixStep { Attempted = true, Succeeded = false, FailureReason = "No replacement waypoint found", Details = grFwdDetails };
        }

        return false;
    }

    private bool TryIntermediateFix(
        IProfileInstance profile,
        Coordinate currentPos,
        ref Coordinate candidate,
        ref List<Coordinate>? seg,
        DebugStemEvent stemEvt,
        HashSet<RouteGeometryUtils.EdgeKey> allUsedEdges,
        bool avoidHighways,
        double segOverlapAfter,
        System.Diagnostics.Stopwatch fixPipelineSw,
        BuildContext ctx)
    {
        var intermediateSw = System.Diagnostics.Stopwatch.StartNew();
        double[][] midCandidates = {
            new[] { 0.5, 0.0 },
            new[] { 0.3, 0.0 },
            new[] { 0.7, 0.0 },
            new[] { 0.5, 500.0 },
            new[] { 0.5, -500.0 },
        };

        foreach (var mc in midCandidates)
        {
            double ratio = mc[0];
            double perpOff = mc[1];
            double midLat = currentPos.Lat + (candidate.Lat - currentPos.Lat) * ratio;
            double midLon = currentPos.Lon + (candidate.Lon - currentPos.Lon) * ratio;

            if (perpOff != 0)
            {
                double bearing = Math.Atan2(candidate.Lat - currentPos.Lat, candidate.Lon - currentPos.Lon);
                double perpAngle = bearing + Math.PI / 2;
                midLat += (perpOff / GeoConstants.MetersPerDegreeLat) * Math.Sin(perpAngle);
                midLon += (perpOff / (GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(currentPos.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(perpAngle);
            }

            var intermediate = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(midLat, midLon), 3000);
            if (intermediate != null && RouteGeometryUtils.HaversineDistance(intermediate, currentPos) > 500
                && RouteGeometryUtils.HaversineDistance(intermediate, candidate) > 500)
            {
                var intermediateQuality = _roadClassifier.ClassifyRoad(intermediate, profile, avoidHighways);
                if (intermediateQuality == RoadClassifier.RoadQuality.Preferred || intermediateQuality == RoadClassifier.RoadQuality.Acceptable)
                {
                    var (viaSeg, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, intermediate, allUsedEdges);
                    if (viaSeg != null && !StemDetector.IsStemSegment(viaSeg, strict: true))
                    {
                        seg = viaSeg;
                        candidate = intermediate;
                        intermediateSw.Stop();
                        ctx.IntermediateTotalMs += intermediateSw.ElapsedMilliseconds;
                        stemEvt.Intermediate = new DebugFixStep { Attempted = true, Succeeded = true, FixAttemptMs = intermediateSw.ElapsedMilliseconds, Details = new Dictionary<string, double> { ["midpointRatio"] = ratio, ["perpOffsetM"] = perpOff } };
                        stemEvt.FixStrategy = "intermediate";
                        stemEvt.Resolution = "intermediate";
                        return true;
                    }
                }
            }
        }

        intermediateSw.Stop();
        ctx.IntermediateTotalMs += intermediateSw.ElapsedMilliseconds;
        stemEvt.Intermediate = new DebugFixStep { Attempted = true, Succeeded = false, FailureReason = "No valid midpoint found", FixAttemptMs = intermediateSw.ElapsedMilliseconds };
        return false;
    }

    private bool TryWitnessPathFix(
        IProfileInstance profile,
        Coordinate currentPos,
        ref Coordinate candidate,
        ref List<Coordinate>? seg,
        DebugStemEvent stemEvt,
        HashSet<RouteGeometryUtils.EdgeKey> allUsedEdges,
        bool avoidHighways,
        double segOverlapAfter,
        System.Diagnostics.Stopwatch fixPipelineSw)
    {
        // GraphHopper-inspired witness path: find alternative between divergence and rejoin points
        if (seg == null || seg.Count < 4) return false;

        // Find divergence point (start of stem) and rejoin point (end of stem)
        var divergencePoint = seg[0];
        var rejoinPoint = seg[^1];

        // Try to find a witness path that doesn't use the stem edges
        var stemEdges = new HashSet<RouteGeometryUtils.EdgeKey>(RouteGeometryUtils.ExtractEdges(seg));

        // Try multiple intermediate points to find a witness path
        for (int attempt = 0; attempt < 3; attempt++)
        {
            double bearing = RouteGeometryUtils.ComputeBearing(divergencePoint, rejoinPoint);
            double offset = (attempt + 1) * 1000; // 1km, 2km, 3km offset
            double perpAngle = bearing + Math.PI / 2;

            double midLat = (divergencePoint.Lat + rejoinPoint.Lat) / 2 + (offset / GeoConstants.MetersPerDegreeLat) * Math.Sin(perpAngle);
            double midLon = (divergencePoint.Lon + rejoinPoint.Lon) / 2 + (offset / (GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(divergencePoint.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(perpAngle);

            var witnessPoint = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(midLat, midLon), 3000);
            if (witnessPoint == null) continue;

            var witnessQuality = _roadClassifier.ClassifyRoad(witnessPoint, profile, avoidHighways);
            if (witnessQuality == RoadClassifier.RoadQuality.Blocked) continue;

            // Route via witness point, avoiding stem edges
            var (witnessSeg1, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, witnessPoint, allUsedEdges);
            if (witnessSeg1 == null) continue;

            var (witnessSeg2, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, witnessPoint, candidate, allUsedEdges);
            if (witnessSeg2 == null) continue;

            var combined = new List<Coordinate>(witnessSeg1);
            combined.AddRange(witnessSeg2.GetRange(1, witnessSeg2.Count - 1));

            double combinedOverlap = RouteGeometryUtils.CalculateSegmentOverlap(combined, allUsedEdges);
            if (combinedOverlap < segOverlapAfter && !StemDetector.IsStemSegment(combined, strict: true))
            {
                seg = combined;
                stemEvt.FixStrategy = "witnessPath";
                stemEvt.Resolution = "witnessPath";
                return true;
            }
        }

        return false;
    }

    private bool TryEdgeBlockingFix(
        IProfileInstance profile,
        Coordinate currentPos,
        ref Coordinate candidate,
        ref List<Coordinate>? seg,
        DebugStemEvent stemEvt,
        HashSet<RouteGeometryUtils.EdgeKey> allUsedEdges,
        bool avoidHighways,
        double segOverlapAfter,
        System.Diagnostics.Stopwatch fixPipelineSw)
    {
        if (seg == null) return false;

        var fixSw = System.Diagnostics.Stopwatch.StartNew();
        var (fixedSeg, tsDetails, fixReasonCode) = _stemFixer.TryFixStem(profile, currentPos, candidate, seg, allUsedEdges);
        _stemFixer.IncrementFixStemCall();
        if (fixedSeg != null) _stemFixer.IncrementFixStemSuccess();
        fixSw.Stop();
        _stemFixer.AddFixStemTime(fixSw.ElapsedMilliseconds);

        if (fixedSeg != null)
        {
            var fixedRoadQuality = _roadClassifier.ClassifyRoad(candidate, profile, avoidHighways);
            if (fixedRoadQuality == RoadClassifier.RoadQuality.Preferred || fixedRoadQuality == RoadClassifier.RoadQuality.Acceptable)
            {
                seg = fixedSeg;
                double fixedOverlap = tsDetails.TryGetValue("altOverlap", out double altOv) ? altOv : 0;
                stemEvt.TryFixStem = new DebugFixStep { Attempted = true, Succeeded = true, ReasonCode = FailureReasonCode.None, OverlapBefore = segOverlapAfter, OverlapAfter = fixedOverlap, FixAttemptMs = fixSw.ElapsedMilliseconds };
                stemEvt.FixStrategy = "tryFixStem";
                stemEvt.Resolution = "fixedAndKept";
                return true;
            }
            else
            {
                stemEvt.TryFixStem = new DebugFixStep { Attempted = true, Succeeded = false, FailureReason = $"Waypoint on {fixedRoadQuality} road", ReasonCode = FailureReasonCode.WaypointOnBadRoad, OverlapBefore = segOverlapAfter, FixAttemptMs = fixSw.ElapsedMilliseconds };
            }
        }
        else
        {
            stemEvt.TryFixStem = new DebugFixStep { Attempted = true, Succeeded = false, FailureReason = "No alternative route found", ReasonCode = fixReasonCode, Details = tsDetails, OverlapBefore = segOverlapAfter };
        }

        return false;
    }

    private bool TryMultiHopFix(
        IProfileInstance profile,
        Coordinate currentPos,
        ref Coordinate candidate,
        ref List<Coordinate>? seg,
        DebugStemEvent stemEvt,
        HashSet<RouteGeometryUtils.EdgeKey> allUsedEdges,
        bool avoidHighways,
        System.Diagnostics.Stopwatch fixPipelineSw,
        BuildContext ctx,
        List<List<Coordinate>> allSegments,
        List<Coordinate> allPoints,
        ref double totalDistKm)
    {
        if (seg == null) return false;

        var mhSw = System.Diagnostics.Stopwatch.StartNew();
        var (hopMid, hop1Seg, hop2Seg, details) = _stemFixer.TryMultiHopSplit(profile, currentPos, candidate, seg, allUsedEdges, avoidHighways);
        mhSw.Stop();
        _stemFixer.AddMultiHopTime(mhSw.ElapsedMilliseconds);
        _stemFixer.IncrementMultiHopCall();

        if (hopMid != null)
        {
            _stemFixer.IncrementMultiHopSuccess();
            allUsedEdges.UnionWith(RouteGeometryUtils.ExtractEdges(hop1Seg!));
            allSegments.Add(hop1Seg!);
            allPoints.Add(hopMid);
            currentPos = hopMid;
            totalDistKm += RouteGeometryUtils.CalculateDistance(hop1Seg!) / 1000;
            seg = hop2Seg;
            candidate = hop2Seg![hop2Seg.Count - 1];
            stemEvt.Intermediate = new DebugFixStep { Attempted = true, Succeeded = true, Details = details };
            stemEvt.FixStrategy = "multiHop";
            stemEvt.Resolution = "multiHop";
            stemEvt.HopCount = 2;
            return true;
        }

        return false;
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
        DirectionBias direction = DirectionBias.Any)
    {
        var context = new BuildContext(start);
        context.BuilderMethod = "alternative_path";
        var alternativePathFinder = new AlternativePathFinder(_mapRepository, _roadClassifier, _edgeBlocker);

        var result = alternativePathFinder.FindRoundTrip(profile, start, targetDistKm, avoidHighways, turnaroundRatio, direction);
        if (result == null)
        {
            return null;
        }

        var (geometry, overlapRatio, plateauCount, shareWeight, turnaroundPoint) = result.Value;

        // Motorways are now blocked at load time — no post-hoc checking needed

        // Add diagnostic information
        _diagnostics.Add(new DebugStemEvent
        {
            AttemptNumber = attemptNumber,
            SegmentRole = "forward",
            SegmentLengthM = RouteGeometryUtils.CalculateDistance(geometry),
            IsStem = false,
            OverlapWithPriorSegments = overlapRatio,
            OriginalWaypoint = new[] { start.Lat, start.Lon },
            FinalWaypoint = new[] { start.Lat, start.Lon },
            Resolution = "alternative_path",
            StraightLineRatio = plateauCount,
            MaxDeviationFromLine = shareWeight,
        });

        context.FinalEdgeSetSize = geometry.Count - 1;

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
        context.ForwardWaypointAngles = segmentAngles;
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
    private (List<Coordinate> segment, bool wasRerouted) RerouteIfMotorwaysBlocked(
        List<Coordinate> segment,
        IProfileInstance profile,
        Coordinate from,
        Coordinate to,
        HashSet<RouteGeometryUtils.EdgeKey> allUsedEdges,
        HashSet<uint> blockedEdges,
        bool avoidHighways)
    {
        if (!avoidHighways)
            return (segment, false);

        var blockedMw = _edgeBlocker.BlockMotorwaysInSegment(segment, profile, blockedEdges);
        if (blockedMw.Count == 0)
            return (segment, false);

        var (rerouted, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(
            profile, from, to, allUsedEdges);
        if (rerouted == null || RouteGeometryUtils.CalculateDistance(rerouted) == 0)
            return (segment, false);

        _edgeBlocker.BlockMotorwaysInSegment(rerouted, profile, blockedEdges);
        return (rerouted, true);
    }
}
