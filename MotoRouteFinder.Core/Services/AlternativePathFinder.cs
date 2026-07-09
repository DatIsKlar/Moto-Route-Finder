using System;
using System.Collections.Generic;
using System.Linq;
using Itinero;
using Itinero.Profiles;
using Microsoft.Extensions.Options;
using MotoRouteFinder.Helpers;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Services;

/// <summary>
/// Implements GraphHopper-inspired forward-then-alternative path finding approach.
/// Instead of building a complete loop and splitting, this finds the outgoing path first,
/// then finds an alternative return path with edge penalties.
/// </summary>
public class AlternativePathFinder
{
    private readonly MapRepository _mapRepository;
    private readonly RoadClassifier _roadClassifier;
    private readonly EdgeBlocker _edgeBlocker;
    private readonly RouteGenerationOptions _options;

    // GraphHopper-inspired parameters
    private const double EarlyExitOverlapThreshold = 0.10;
    private const double EarlyExitSpreadMinimum = 250.0;
    private const double DistanceValidationMin = 0.2;
    private const double DistanceValidationMax = 0.95;
    private const double ShorterReturnImprovementThreshold = 0.85;
    private const double DistancePenaltyThreshold = 1.3;
    private const int MinPlateauLength = 3;
    private const double GridSampleIntervalM = 500;
    private const double BearingScoreWeight = 1000;

    // §11a: Routing call timing instrumentation
    private enum RoutingCallCategory { Forward, Normal, VeryHigh, High, PushFallback }
    private ReturnPathDiagnostics? _currentReturnDiag;
    private BuildContext? _currentContext;

    // Step 6: Per-generation density cache (~500m cells, cleared per map lifecycle)
    private Dictionary<(int, int), int>? _densityCache;

    public AlternativePathFinder(MapRepository mapRepository, RoadClassifier roadClassifier, EdgeBlocker edgeBlocker, IOptions<RouteGenerationOptions>? options = null)
    {
        _mapRepository = mapRepository;
        _roadClassifier = roadClassifier;
        _edgeBlocker = edgeBlocker;
        _options = options?.Value ?? new RouteGenerationOptions();
    }

    public void ClearDensityCache()
    {
        _densityCache?.Clear();
    }

    /// <summary>
    /// Finds a round trip using a forward-then-alternative approach (inspired by GraphHopper).
    /// Generates 2 candidates with different seeds and picks the most circular one.
    /// </summary>
    /// <param name="profile">Routing profile</param>
    /// <param name="start">Starting point</param>
    /// <param name="targetDistanceKm">Target total distance in km</param>
    /// <param name="avoidHighways">Whether to avoid highways</param>
    /// <param name="turnaroundRatio">Fraction of target distance for turnaround (default 0.5 = 50%)</param>
    /// <returns>Round trip geometry and quality metrics</returns>
    public (List<Coordinate> geometry, double overlapRatio, int plateauCount, double shareWeight, Coordinate turnaroundPoint, ReturnPathDiagnostics returnPathDiagnostics)? FindRoundTrip(
        IProfileInstance profile,
        Coordinate start,
        double targetDistanceKm,
        bool avoidHighways,
        double turnaroundRatio = 0.5,
        DirectionBias direction = DirectionBias.Any,
        BuildContext? context = null,
        double? avoidBearing = null)
    {
        // Convert direction to bearing
        double userBearing = GeoConstants.DirectionToBearing(direction);

        // Generate 3 candidates with different seeds and turnaround ratios, pick the best
        double[] turnaroundRatios = { turnaroundRatio, 0.45, 0.55 };
        var candidates = new List<(List<Coordinate> geometry, double overlapRatio, int plateauCount, double shareWeight, Coordinate turnaroundPoint, ReturnPathDiagnostics returnPathDiagnostics)>();

        double? measuredDetour = null;
        for (int seed = 0; seed < 3; seed++)
        {
            var c = FindSingleRoundTrip(profile, start, targetDistanceKm, avoidHighways, turnaroundRatios[seed], seed, userBearing, measuredDetour, context, avoidBearing);
            if (c != null)
            {
                // If this candidate overshot, record the measured ratio for next candidates
                // Use the exact value already computed inside FindSingleRoundTrip
                if (c.Value.returnPathDiagnostics.ForwardPathDetourRatio > 0)
                    measuredDetour = c.Value.returnPathDiagnostics.ForwardPathDetourRatio;

                // Early exit: excellent overlap AND high circularity
                double spread = RouteGeometryUtils.CalculateBearingSpread(c.Value.geometry);
                if (c.Value.overlapRatio <= EarlyExitOverlapThreshold && spread >= EarlyExitSpreadMinimum)
                    return c.Value;

                candidates.Add(c.Value);
            }
        }

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        // Score: prefer high spread and low overlap
        return candidates.OrderByDescending(c =>
        {
            double spread = RouteGeometryUtils.CalculateBearingSpread(c.geometry);
            // Combine spread (higher=better) with overlap penalty (lower=better)
            return spread * (1.0 - Math.Min(c.overlapRatio, 0.5));
        }).First();
    }

    /// <summary>
    /// Single round trip candidate with seeded randomness for diversity.
    /// Blocks edges along the direct path to force the router to find a curved alternative.
    /// </summary>
    private (List<Coordinate> geometry, double overlapRatio, int plateauCount, double shareWeight, Coordinate turnaroundPoint, ReturnPathDiagnostics returnPathDiagnostics)? FindSingleRoundTrip(
        IProfileInstance profile,
        Coordinate start,
        double targetDistanceKm,
        bool avoidHighways,
        double turnaroundRatio,
        int candidateSeed,
        double userBearing,
        double? measuredDetourRatio = null,
        BuildContext? context = null,
        double? avoidBearing = null)
    {
        _currentContext = context; // §11a: enable forward-path timing
        _densityCache ??= new Dictionary<(int, int), int>();
        // §11e: capture EdgeBlocker start values for delta computation
        long penaltyEdgesMsStart = _edgeBlocker.PenaltyEdgesMs;
        int penaltyEdgesCountStart = _edgeBlocker.PenaltyEdgesEdgeCount;
        long restoreEdgesMsStart = _edgeBlocker.RestoreEdgesMs;
        int restoreEdgesCountStart = _edgeBlocker.RestoreEdgesEdgeCount;

        double targetDistanceM = targetDistanceKm * 1000;
        // Compensate for road detour factor — routed paths are always longer than haversine
        // With edge blocking for curvature, forward paths routinely detour 2.0-2.3x
        // Use measured ratio from previous candidate if available, otherwise use configured default
        double estimatedDetourFactor = measuredDetourRatio ?? _options.EstimatedDetourFactor;
        double halfDistanceM = targetDistanceM * turnaroundRatio / estimatedDetourFactor;

        // Phase 1: Find turnaround point
        // If user set a direction, use it. Otherwise, use seed-based diversity (0° vs 180°)
        double preferredBearing = userBearing >= 0 ? userBearing : candidateSeed * 180;
        var turnaroundPoint = FindTurnaroundPoint(profile, start, halfDistanceM, preferredBearing, candidateSeed, avoidBearing);
        if (turnaroundPoint == null)
            return null;

        // Phase 1b: Block edges along direct path to force curvature
        double corridorWidthM = candidateSeed == 0 ? _options.CorridorWidthPrimary : _options.CorridorWidthSecondary; // different widths for diversity
        var blockedEdgeIds = FindEdgesAlongLine(profile, start, turnaroundPoint, corridorWidthM);
        using var penaltyScope = blockedEdgeIds.Count > 0
            ? _edgeBlocker.PenaltyEdges(blockedEdgeIds, _options.EdgePenaltyFactor)
            : null;

        // Phase 2: Forward path (router forced to curve around blocked zone)
        var forwardPath = TimedRoute(profile, start, turnaroundPoint, RoutingCallCategory.Forward);
        if (forwardPath == null || forwardPath.Count < 2)
        {
            // Fallback: try without penalty
            penaltyScope?.Dispose();
            forwardPath = TimedRoute(profile, start, turnaroundPoint, RoutingCallCategory.Forward);
            if (forwardPath == null || forwardPath.Count < 2)
                return null;
        }

        // Phase 2b: Try to improve circularity by penalizing straight-line edges
        forwardPath = FindCurvierForward(profile, start, turnaroundPoint, forwardPath, avoidHighways);

        // Phase 2c: If spread still low, try routing through a perpendicular waypoint
        double currentSpread = RouteGeometryUtils.CalculateBearingSpread(forwardPath);
        if (currentSpread < _options.MinimumSpreadThreshold)
        {
            double startBearing = RouteGeometryUtils.ComputeBearing(start, turnaroundPoint);
            var perpWp = GenerateForwardWaypoint(profile, start, turnaroundPoint, halfDistanceM, startBearing, candidateSeed);
            if (perpWp != null)
            {
                // Route through perpendicular waypoint: start → perpWp → turnaround
                var seg1 = TimedRoute(profile, start, perpWp, RoutingCallCategory.Forward);
                var seg2 = TimedRoute(profile, perpWp, turnaroundPoint, RoutingCallCategory.Forward);
                if (seg1 != null && seg1.Count >= 2 && seg2 != null && seg2.Count >= 2)
                {
                    var candidatePath = RouteGeometryUtils.ConcatenatePaths(seg1, seg2);
                    double candidateSpread = RouteGeometryUtils.CalculateBearingSpread(candidatePath);
                    if (candidateSpread > currentSpread)
                        forwardPath = candidatePath;
                }
            }
        }

        // Extract forward edge set
        var forwardEdgeSet = new HashSet<RouteGeometryUtils.EdgeKey>();
        for (int i = 0; i < forwardPath.Count - 1; i++)
        {
            var edgeKey = RouteGeometryUtils.MakeEdgeKey(forwardPath[i], forwardPath[i + 1]);
            forwardEdgeSet.Add(edgeKey);
        }

        // Phase 3: Alternative return path
        var (alternativeReturn, returnDiag) = FindAlternativePath(profile, turnaroundPoint, start, forwardEdgeSet, forwardPath);
        if (alternativeReturn == null)
        {
            alternativeReturn = new List<Coordinate>(forwardPath);
            alternativeReturn.Reverse();
            returnDiag.PenaltyLevelUsed = "reversed_forward";
        }

        // §13b: Record failed turnaround bearing for cross-attempt avoidance
        // If the escalation ladder exhausted all tiers and still couldn't avoid overlap,
        // record the bearing so the next attempt can bias away from it.
        if (context != null
            && returnDiag.PenaltyLevelUsed == "push_fallback"
            && returnDiag.PushFallbackBestOverlap > _options.MaxShareFactor)
        {
            context.FailedTurnaroundBearing = RouteGeometryUtils.ComputeBearing(start, turnaroundPoint);
        }

        // Phase 3b: Soft distance preference (adaptive — uses forward path's actual detour ratio)
        double forwardHaversine = RouteGeometryUtils.HaversineDistance(start, turnaroundPoint);
        double forwardActual = RouteGeometryUtils.CalculateDistance(forwardPath);
        double actualDetourRatio = forwardHaversine > 0 ? forwardActual / forwardHaversine : _options.EstimatedDetourFactor;
        alternativeReturn = ApplyDistancePreference(profile, turnaroundPoint, start, forwardEdgeSet, forwardPath, alternativeReturn, actualDetourRatio);

        // Phase 4: Merge and calculate metrics
        var mergedGeometry = RouteGeometryUtils.ConcatenatePaths(forwardPath, alternativeReturn);

        double overlapRatio = RouteGeometryUtils.CalculateSegmentOverlap(mergedGeometry, forwardEdgeSet);
        int plateauCount = CountPlateaus(mergedGeometry, forwardEdgeSet);

        // Compute root cause and forward path context for diagnostics
        double turnaroundAngle = RouteGeometryUtils.CalculateTurnaroundAngle(forwardPath, alternativeReturn);
        double forwardDetourRatio = forwardHaversine > 0 ? forwardActual / forwardHaversine : 1.0;
        int edgeDensity = CountEdgesNearPoint(turnaroundPoint, 2000);
        returnDiag.ForwardPathTurnaroundAngle = turnaroundAngle;
        returnDiag.ForwardPathDetourRatio = forwardDetourRatio;
        returnDiag.ForwardPathEdgeDensity = edgeDensity;

        // Find turnaround index in merged geometry to compute return overlap
        int turnaroundIdx = 0;
        double minDistToTurn = double.MaxValue;
        for (int i = 0; i < mergedGeometry.Count; i++)
        {
            double d = RouteGeometryUtils.HaversineDistance(mergedGeometry[i], turnaroundPoint);
            if (d < minDistToTurn) { minDistToTurn = d; turnaroundIdx = i; }
        }
        double returnOverlapPct = RouteGeometryUtils.CalculateSegmentOverlap(
            mergedGeometry.GetRange(turnaroundIdx, mergedGeometry.Count - turnaroundIdx),
            forwardEdgeSet);
        double totalDistM = RouteGeometryUtils.CalculateDistance(mergedGeometry);
        double edgeOverlapM = overlapRatio * totalDistM;

        // Distance cap: if total distance significantly exceeds target, try shorter return
        double distanceCap = targetDistanceM * _options.DistanceCapMultiplier;
        if (totalDistM > distanceCap)
        {
            // Try direct return (no penalty) first
            var directReturn = TimedRoute(profile, turnaroundPoint, start, RoutingCallCategory.Forward);
            if (directReturn != null)
            {
                double directDist = RouteGeometryUtils.CalculateDistance(directReturn);
                double directOverlap = RouteGeometryUtils.CalculateSegmentOverlap(directReturn, forwardEdgeSet);
                if (directDist + forwardActual <= distanceCap && directOverlap <= _options.MaxShareFactor)
                {
                    mergedGeometry = RouteGeometryUtils.ConcatenatePaths(forwardPath, directReturn);
                    alternativeReturn = directReturn;
                    totalDistM = RouteGeometryUtils.CalculateDistance(mergedGeometry);
                    overlapRatio = directOverlap;
                    edgeOverlapM = overlapRatio * totalDistM;
                    returnDiag.PenaltyLevelUsed = "distance_cap_direct";
                }
            }

            // If still over cap, try low-penalty return
            if (totalDistM > distanceCap)
            {
                var capEdgeIds = ResolveForwardEdgeIds(forwardPath);
                var lowPenaltyReturn = FindPathWithPenalty(profile, turnaroundPoint, start, forwardPath, _options.LowPenaltyFallbackFactor, capEdgeIds);
                if (lowPenaltyReturn != null)
                {
                    double lpDist = RouteGeometryUtils.CalculateDistance(lowPenaltyReturn);
                    double lpOverlap = RouteGeometryUtils.CalculateSegmentOverlap(lowPenaltyReturn, forwardEdgeSet);
                    if (lpDist + forwardActual <= distanceCap && lpOverlap <= _options.MaxShareFactor)
                    {
                        mergedGeometry = RouteGeometryUtils.ConcatenatePaths(forwardPath, lowPenaltyReturn);
                        alternativeReturn = lowPenaltyReturn;
                        totalDistM = RouteGeometryUtils.CalculateDistance(mergedGeometry);
                        overlapRatio = lpOverlap;
                        edgeOverlapM = overlapRatio * totalDistM;
                        returnDiag.PenaltyLevelUsed = "distance_cap_low_penalty";
                    }
                }
            }
        }
        returnDiag.RepetitionRootCause = ClassifyRepetitionRootCause(
            returnOverlapPct, turnaroundAngle, edgeOverlapM, totalDistM, edgeDensity);

        // §11e: compute EdgeBlocker timing delta for this seed
        if (_currentContext != null)
        {
            _currentContext.PenaltyEdgesMs += _edgeBlocker.PenaltyEdgesMs - penaltyEdgesMsStart;
            _currentContext.PenaltyEdgesEdgeCount += _edgeBlocker.PenaltyEdgesEdgeCount - penaltyEdgesCountStart;
            _currentContext.RestoreEdgesMs += _edgeBlocker.RestoreEdgesMs - restoreEdgesMsStart;
            _currentContext.RestoreEdgesEdgeCount += _edgeBlocker.RestoreEdgesEdgeCount - restoreEdgesCountStart;
        }

        return (mergedGeometry, overlapRatio, plateauCount, overlapRatio, turnaroundPoint, returnDiag);
    }

    /// <summary>
    /// Applies soft distance preference to the return path.
    /// Prefers shorter return paths while maintaining low overlap.
    /// </summary>
    private List<Coordinate> ApplyDistancePreference(
        IProfileInstance profile,
        Coordinate from,
        Coordinate to,
        HashSet<RouteGeometryUtils.EdgeKey> forwardEdges,
        List<Coordinate> forwardPath,
        List<Coordinate> currentReturn,
        double actualDetourRatio = 1.35)
    {
        double haversineReturn = RouteGeometryUtils.HaversineDistance(from, to);
        double currentDistance = RouteGeometryUtils.CalculateDistance(currentReturn);
        double currentOverlap = RouteGeometryUtils.CalculateSegmentOverlap(currentReturn, forwardEdges);

        // Use actual detour ratio from forward path instead of fixed 1.1
        double expectedReturnMax = haversineReturn * Math.Max(actualDetourRatio, 1.1);
        if (currentDistance <= expectedReturnMax && currentOverlap <= _options.OverlapAcceptanceThreshold)
            return currentReturn;

        // Try to find a shorter return path with penalty-based routing
        var shorterReturn = FindPathWithPenalty(profile, from, to, forwardPath, _options.ShorterReturnPenaltyFactor);
        if (shorterReturn != null)
        {
            double shorterDistance = RouteGeometryUtils.CalculateDistance(shorterReturn);
            double shorterOverlap = RouteGeometryUtils.CalculateSegmentOverlap(shorterReturn, forwardEdges);

            // Use shorter path if it's significantly shorter and overlap is acceptable
            if (shorterDistance < currentDistance * ShorterReturnImprovementThreshold && shorterOverlap <= _options.OverlapAcceptanceThreshold)
                return shorterReturn;
        }

        return currentReturn;
    }

    /// <summary>
    /// Generates a single intermediate waypoint at a perpendicular offset from the start→turnaround bearing.
    /// Forces the forward path through a different compass direction for better circularity.
    /// </summary>
    private Coordinate? GenerateForwardWaypoint(
        IProfileInstance profile,
        Coordinate start,
        Coordinate turnaround,
        double halfDistanceM,
        double startBearingDeg,
        int candidateSeed)
    {
        // Perpendicular offset: 90° to the left or right of the direct line
        double sideSign = candidateSeed % 2 == 0 ? 1 : -1;
        // M1: Convert compass bearing to math angle for Sin/Cos-based ProjectPoint
        double bearingRad = RouteGeometryUtils.BearingToMathAngle(startBearingDeg + 90 * sideSign);
        double distance = halfDistanceM * _options.PerpendicularOffsetFraction;

        var point = RouteGeometryUtils.ProjectPoint(start, bearingRad, distance);

        var resolved = _roadClassifier.TryResolveToRoadCounted(profile, point);
        if (resolved != null)
        {
            double actualDist = RouteGeometryUtils.HaversineDistance(start, resolved);
            if (actualDist >= halfDistanceM * DistanceValidationMin && actualDist <= halfDistanceM * DistanceValidationMax)
                return resolved;
        }

        return null;
    }

    /// <summary>
    /// Attempts to find a curvier forward path by penalizing straight-line edges.
    /// Uses graduated penalty levels (5x, 10x, 15x) to force the router onto curvier alternatives.
    /// </summary>
    private List<Coordinate> FindCurvierForward(
        IProfileInstance profile,
        Coordinate start,
        Coordinate turnaround,
        List<Coordinate> initialForwardPath,
        bool avoidHighways,
        int maxAttempts = 3)
    {
        var currentPath = initialForwardPath;
        double bestSpread = RouteGeometryUtils.CalculateBearingSpread(currentPath);

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (bestSpread >= _options.MinimumSpreadThreshold)
                return currentPath;

            // Extract edge IDs from current forward path
            var edgeIds = ResolveForwardEdgeIds(currentPath);

            if (edgeIds.Count == 0)
                break;

            // Apply graduated penalty: 5x, 10x, 15x
            double penaltyFactor = _options.GraduatedPenaltyBase + attempt * _options.GraduatedPenaltyStep;
            using var penaltyScope = _edgeBlocker.PenaltyEdges(edgeIds, penaltyFactor);

            // Block motorways if needed
            using var motorwayScope = avoidHighways
                ? _edgeBlocker.BlockMotorways(profile, start, 200)
                : null;

            // Re-route with penalties
            var curvierPath = TimedRoute(profile, start, turnaround, RoutingCallCategory.Forward);
            if (curvierPath == null || curvierPath.Count < 2)
                break;

            double newSpread = RouteGeometryUtils.CalculateBearingSpread(curvierPath);
            if (newSpread > bestSpread)
            {
                bestSpread = newSpread;
                currentPath = curvierPath;
            }
        }

        // Fallback: if penalties didn't improve spread, try perpendicular waypoints
        if (bestSpread < _options.MinimumSpreadThreshold)
        {
            double startBearing = RouteGeometryUtils.ComputeBearing(start, turnaround);
            double halfDist = RouteGeometryUtils.HaversineDistance(start, turnaround);

            for (int side = -1; side <= 1; side += 2)
            {
                double bearingDeg = startBearing + 90 * side;
                double bearingRad = RouteGeometryUtils.BearingToMathAngle(bearingDeg);
                double distance = halfDist * _options.PerpendicularOffsetFraction;

                var point = RouteGeometryUtils.ProjectPoint(start, bearingRad, distance);
                var resolved = _roadClassifier.TryResolveToRoadCounted(profile, point);
                if (resolved == null) continue;

                double actualDist = RouteGeometryUtils.HaversineDistance(start, resolved);
                if (actualDist < halfDist * DistanceValidationMin || actualDist > halfDist * DistanceValidationMax) continue;

                var seg1 = TimedRoute(profile, start, resolved, RoutingCallCategory.Forward);
                var seg2 = TimedRoute(profile, resolved, turnaround, RoutingCallCategory.Forward);
                if (seg1 == null || seg1.Count < 2 || seg2 == null || seg2.Count < 2) continue;

                var candidatePath = RouteGeometryUtils.ConcatenatePaths(seg1, seg2);
                double candidateSpread = RouteGeometryUtils.CalculateBearingSpread(candidatePath);
                if (candidateSpread > bestSpread)
                {
                    bestSpread = candidateSpread;
                    currentPath = candidatePath;
                }
            }
        }

        return currentPath;
    }

    /// <summary>
    /// Finds a turnaround point using distance circle expansion (GraphHopper approach).
    /// Tries 12 evenly-spaced bearings with random offset and ±10% distance variation.
    /// Returns one of the top 3 closest candidates randomly for route diversity.
    /// Optionally weights bearings toward a preferred direction for curvature.
    /// </summary>
    private Coordinate? FindTurnaroundPoint(IProfileInstance profile, Coordinate start, double targetDistanceM, double preferredBearingDeg = -1, int candidateSeed = 0, double? avoidBearing = null)
    {
        // Add random initial bearing offset for route diversity
        double randomOffset = Random.Shared.NextDouble() * _options.TurnaroundRandomOffsetDegrees;
        // Primary candidate (seed 0) uses 12 bearings, others use 6 for speed
        int bearingCount = candidateSeed == 0 ? 12 : 6;

        var candidates = new List<(Coordinate coord, double delta, double bearingDeg, int edgeDensity)>();

        for (int i = 0; i < bearingCount; i++)
        {
            try
            {
                double deg = (i * 360.0 / bearingCount) + randomOffset;
                double bearing = RouteGeometryUtils.BearingToMathAngle(deg);

                // Add ±10% random distance variation
                double distanceVariation = 1.0 + (Random.Shared.NextDouble() - 0.5) * _options.TurnaroundDistanceVariation; // 0.9 to 1.1
                double variedDistance = targetDistanceM * distanceVariation;

                var point = RouteGeometryUtils.ProjectPoint(start, bearing, variedDistance);

                var candidate = _roadClassifier.TryResolveToRoadCounted(profile, point);
                if (candidate == null) continue;

                // Verify distance is within acceptable range
                double actualDistance = RouteGeometryUtils.HaversineDistance(start, candidate);
                if (actualDistance < targetDistanceM * 0.6 || actualDistance > targetDistanceM * 1.4) continue;

                double delta = Math.Abs(actualDistance - targetDistanceM);
                // Use RESOLVED bearing (from start to actual road point), not the raw bearing.
                // TryResolveToRoad can shift the point significantly to the nearest road,
                // so the original bearing may not represent the actual direction.
                double actualBearing = RouteGeometryUtils.ComputeBearing(start, candidate);

                // §13a: Check road density at candidate point (same radius used for ForwardPathEdgeDensity)
                var densitySw = System.Diagnostics.Stopwatch.StartNew();
                int edgeDensity = CountEdgesNearPoint(candidate, 2000);
                densitySw.Stop();
                if (_currentContext != null)
                {
                    _currentContext.TurnaroundDensityCheckMs += densitySw.ElapsedMilliseconds;
                    _currentContext.TurnaroundDensityCheckCalls++;
                }

                candidates.Add((candidate, delta, actualBearing, edgeDensity));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AlternativePathFinder] Turnaround candidate failed: {ex.Message}");
                continue;
            }
        }

        if (candidates.Count == 0)
            return null;

        // If we have a preferred bearing, score candidates by distance, bearing preference, density, and avoidance
        if (preferredBearingDeg >= 0)
        {
            // Score: lower is better. Combine distance delta with bearing preference.
            // Bearing preference: prefer bearings 60-120° offset from preferred (perpendicular)
            var scored = candidates.Select(c =>
            {
                double bearingDiff = Math.Abs(c.bearingDeg - preferredBearingDeg);
                if (bearingDiff > 180) bearingDiff = 360 - bearingDiff;

                // Prefer bearings in the requested direction (0° offset = best).
                // Penalize opposite direction (180° offset = worst).
                // Blend: 0°→0.0 (best), 180°→1.0 (worst)
                double bearingScore = bearingDiff / 180.0;

                // Combine: distance delta normalized + bearing score * 1000
                double normalizedDelta = c.delta / targetDistanceM;
                double combinedScore = normalizedDelta + bearingScore * BearingScoreWeight;

                // §13c: Normalized risk penalties (0-1 scale, weighted against BearingScoreWeight=1000)
                double densityRisk = c.edgeDensity < 5 ? (5 - c.edgeDensity) / 5.0 : 0;
                combinedScore += densityRisk * _options.TurnaroundDensityPenaltyWeight;

                // §13b: Penalty for bearing close to a known-failed bearing
                if (avoidBearing.HasValue)
                {
                    double avoidDiff = Math.Abs(c.bearingDeg - avoidBearing.Value);
                    if (avoidDiff > 180) avoidDiff = 360 - avoidDiff;
                    double avoidRisk = avoidDiff < 45 ? (45 - avoidDiff) / 45.0 : 0;
                    combinedScore += avoidRisk * _options.TurnaroundAvoidBearingPenaltyWeight;
                }

                return (c.coord, score: combinedScore);
            }).OrderBy(x => x.score).ToList();

            return scored[0].coord;
        }

        // Fallback: sort by delta + density penalty and pick randomly from top 3 for diversity
        candidates.Sort((a, b) =>
        {
            double scoreA = a.delta + (a.edgeDensity < 5 ? (5 - a.edgeDensity) * 0.5 : 0);
            double scoreB = b.delta + (b.edgeDensity < 5 ? (5 - b.edgeDensity) * 0.5 : 0);
            // §13b: Also apply avoidBearing penalty in fallback path
            if (avoidBearing.HasValue)
            {
                double avoidDiffA = Math.Abs(a.bearingDeg - avoidBearing.Value);
                if (avoidDiffA > 180) avoidDiffA = 360 - avoidDiffA;
                if (avoidDiffA < 45) scoreA += (45 - avoidDiffA) * 0.3;
                double avoidDiffB = Math.Abs(b.bearingDeg - avoidBearing.Value);
                if (avoidDiffB > 180) avoidDiffB = 360 - avoidDiffB;
                if (avoidDiffB < 45) scoreB += (45 - avoidDiffB) * 0.3;
            }
            return scoreA.CompareTo(scoreB);
        });
        int topN = Math.Min(3, candidates.Count);
        int selectedIndex = Random.Shared.Next(topN);
        return candidates[selectedIndex].coord;
    }

    /// <summary>
    /// Finds an alternative path with edge penalties (GraphHopper-inspired).
    /// Implements graduated fallback mechanism: none -> very_high (20x) -> high (8x) -> push_fallback.
    /// Also applies soft distance preference to reduce overshoot.
    /// </summary>
    private (List<Coordinate>? path, ReturnPathDiagnostics diagnostics) FindAlternativePath(
        IProfileInstance profile,
        Coordinate from,
        Coordinate to,
        HashSet<RouteGeometryUtils.EdgeKey> forwardEdges,
        List<Coordinate> forwardPath)
    {
        var diag = new ReturnPathDiagnostics();
        _currentReturnDiag = diag; // §11a: enable timing instrumentation
        double haversineReturn = RouteGeometryUtils.HaversineDistance(from, to);

        // Try to find a path with penalties on forward edges
        var normalPath = TimedRoute(profile, from, to, RoutingCallCategory.Normal);
        if (normalPath == null)
            return (null, diag);

        // Check how much this path overlaps with forward edges
        double overlap = RouteGeometryUtils.CalculateSegmentOverlap(normalPath, forwardEdges);
        diag.NormalOverlap = overlap;

        // If overlap is acceptable, return this path
        if (overlap <= _options.MaxShareFactor)
        {
            diag.PenaltyLevelUsed = "none";
            return (normalPath, diag);
        }

        // Count edges that would be penalized
        var edgeIds = ResolveForwardEdgeIds(forwardPath);
        int penalizedEdgeCount = edgeIds.Count;

        // Level 1: Very high penalty (20x) — strongest avoidance first
        diag.VeryHighPenaltyApplied = true;
        diag.VeryHighPenaltyEdgeCount = penalizedEdgeCount;
        var veryHighResult = FindPathWithPenalty(profile, from, to, forwardPath, _options.VeryHighPenaltyFactor, edgeIds, RoutingCallCategory.VeryHigh);
        if (veryHighResult != null)
        {
            double vhOverlap = RouteGeometryUtils.CalculateSegmentOverlap(veryHighResult, forwardEdges);
            diag.VeryHighPenaltyOverlap = vhOverlap;
            diag.VeryHighPenaltyAccepted = vhOverlap <= _options.MaxShareFactor;
            if (diag.VeryHighPenaltyAccepted)
            {
                diag.PenaltyLevelUsed = "very_high";
                return (veryHighResult, diag);
            }
        }

        // Level 2: High penalty (8x) — moderate fallback
        diag.HighPenaltyApplied = true;
        diag.HighPenaltyEdgeCount = penalizedEdgeCount;
        var highPenaltyResult = FindPathWithPenalty(profile, from, to, forwardPath, _options.HighReturnPenaltyFactor, edgeIds, RoutingCallCategory.High);
        if (highPenaltyResult != null)
        {
            double hOverlap = RouteGeometryUtils.CalculateSegmentOverlap(highPenaltyResult, forwardEdges);
            diag.HighPenaltyOverlap = hOverlap;
            diag.HighPenaltyAccepted = hOverlap <= _options.MaxShareFactor;
            if (diag.HighPenaltyAccepted)
            {
                diag.PenaltyLevelUsed = "high";
                return (highPenaltyResult, diag);
            }
        }

        // Level 3: Push-point fallback — route via geometric bypasses
        diag.PushFallbackApplied = true;
        var bestPath = normalPath;
        double bestOverlap = overlap;
        double bestScore = ScoreReturnPath(normalPath, overlap, haversineReturn);

        // Push-point fallback: route via geometric bypasses
        var overlapCenter = RouteGeometryUtils.FindOverlapCenter(normalPath, forwardEdges)
            ?? new Coordinate((from.Lat + to.Lat) / 2, (from.Lon + to.Lon) / 2);
        double overlapRadius = RouteGeometryUtils.CalculateOverlapZoneRadius(normalPath, forwardEdges, overlapCenter);
        double basePushDist = Math.Max(_options.PushFallbackMinDistanceM, overlapRadius * _options.PushFallbackDistanceMultiplier);

        // Sector-deficient push: route toward the least-covered compass direction
        double deficientBearing = RouteGeometryUtils.GetMostDeficientReturnBearing(from, forwardPath);
        double deficientAngle = RouteGeometryUtils.BearingToMathAngle(deficientBearing);
        var deficientPushCoord = RouteGeometryUtils.ProjectPoint(overlapCenter, deficientAngle, basePushDist);
        var deficientPushPoint = _roadClassifier.TryResolveToRoadCounted(profile, deficientPushCoord);
        if (deficientPushPoint != null)
        {
            var viaDef1 = FindPathWithPenalty(profile, from, deficientPushPoint, forwardPath, _options.PushFallbackPenaltyFactor, edgeIds, RoutingCallCategory.PushFallback)
                ?? TimedRoute(profile, from, deficientPushPoint, RoutingCallCategory.PushFallback);
            var viaDef2 = FindPathWithPenalty(profile, deficientPushPoint, to, forwardPath, _options.PushFallbackPenaltyFactor, edgeIds, RoutingCallCategory.PushFallback)
                ?? TimedRoute(profile, deficientPushPoint, to, RoutingCallCategory.PushFallback);
            if (viaDef1 != null && viaDef2 != null)
            {
                var deficientCombined = RouteGeometryUtils.ConcatenatePaths(viaDef1, viaDef2);
                double deficientOverlap = RouteGeometryUtils.CalculateSegmentOverlap(deficientCombined, forwardEdges);
                if (deficientOverlap <= _options.MaxShareFactor)
                {
                    diag.PenaltyLevelUsed = "push_fallback";
                    diag.PushFallbackBestOverlap = deficientOverlap;
                    return (deficientCombined, diag);
                }
                if (deficientOverlap < bestOverlap)
                {
                    bestOverlap = deficientOverlap;
                    bestPath = deficientCombined;
                }
            }
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            double angle = attempt * Math.PI / 2; // Try 0°, 90°, 180°, 270°

            var pushCoord = RouteGeometryUtils.ProjectPoint(overlapCenter, angle, basePushDist);

            var pushPoint = _roadClassifier.TryResolveToRoadCounted(profile, pushCoord);
            if (pushPoint == null) continue;

            var viaRoute1 = FindPathWithPenalty(profile, from, pushPoint, forwardPath, _options.PushFallbackPenaltyFactor, edgeIds, RoutingCallCategory.PushFallback)
                ?? TimedRoute(profile, from, pushPoint, RoutingCallCategory.PushFallback);
            if (viaRoute1 == null) continue;

            var viaRoute2 = FindPathWithPenalty(profile, pushPoint, to, forwardPath, _options.PushFallbackPenaltyFactor, edgeIds, RoutingCallCategory.PushFallback)
                ?? TimedRoute(profile, pushPoint, to, RoutingCallCategory.PushFallback);
            if (viaRoute2 == null) continue;

            var combined = RouteGeometryUtils.ConcatenatePaths(viaRoute1, viaRoute2);

            double combinedOverlap = RouteGeometryUtils.CalculateSegmentOverlap(combined, forwardEdges);
            double combinedScore = ScoreReturnPath(combined, combinedOverlap, haversineReturn);
            if (combinedScore < bestScore)
            {
                bestScore = combinedScore;
                bestOverlap = combinedOverlap;
                bestPath = combined;
            }

            if (bestOverlap <= _options.MaxShareFactor * 2)
                break;
        }

        diag.PushFallbackBestOverlap = bestOverlap;
        diag.PenaltyLevelUsed = "push_fallback";
        return (bestPath, diag);
    }

    /// <summary>
    /// Scores a return path based on overlap and distance (lower = better).
    /// Combines overlap penalty with distance preference.
    /// </summary>
    private double ScoreReturnPath(List<Coordinate> path, double overlap, double haversineDistance)
    {
        double actualDistance = RouteGeometryUtils.CalculateDistance(path);
        double distanceRatio = haversineDistance > 0 ? actualDistance / haversineDistance : 1.0;

        // Overlap penalty: higher overlap = worse score
        double overlapPenalty = overlap * 100;

        // Distance penalty: prefer paths closer to haversine (1.0x)
        // Paths longer than 1.3x get increasing penalty
        double distancePenalty = distanceRatio > DistancePenaltyThreshold ? (distanceRatio - DistancePenaltyThreshold) * 50 : 0;

        return overlapPenalty + distancePenalty;
    }

    /// <summary>
    /// Resolves edge IDs from forward path coordinates for penalty application.
    /// </summary>
    private List<uint> ResolveForwardEdgeIds(List<Coordinate> forwardPath)
    {
        var profileInstance = _mapRepository.RouterDb?.GetSupportedProfile("motorcycle");
        if (profileInstance == null) return new List<uint>();

        var edgeIds = new List<uint>();
        var seenEdgeIds = new HashSet<uint>();
        var sw = _currentContext != null ? System.Diagnostics.Stopwatch.StartNew() : null;
        int callCount = 0;

        // §11f: distance-based subsampling — skip redundant resolves for nearby points.
        // Road edges are typically 50-200m long, so 100m intervals capture every unique edge.
        const double MinResolveIntervalM = 100;
        double cumulativeDistM = MinResolveIntervalM; // force first point to resolve

        for (int i = 0; i < forwardPath.Count - 1; i++)
        {
            double segDist = RouteGeometryUtils.HaversineDistance(forwardPath[i], forwardPath[i + 1]);
            cumulativeDistM += segDist;

            if (cumulativeDistM < MinResolveIntervalM)
                continue;

            // Reset after resolving (or skipping) — resolve at this midpoint
            cumulativeDistM = 0;

            double midLat = (forwardPath[i].Lat + forwardPath[i + 1].Lat) / 2;
            double midLon = (forwardPath[i].Lon + forwardPath[i + 1].Lon) / 2;
            var result = _mapRepository.Router.TryResolve(profileInstance, (float)midLat, (float)midLon, 50);
            callCount++;
            if (!result.IsError && !seenEdgeIds.Contains(result.Value.EdgeId))
            {
                edgeIds.Add(result.Value.EdgeId);
                seenEdgeIds.Add(result.Value.EdgeId);
            }
        }

        if (sw != null)
        {
            sw.Stop();
            _currentContext!.ResolveForwardEdgeIdsMs += sw.ElapsedMilliseconds;
            _currentContext!.ResolveForwardEdgeIdsCalls += callCount;
        }

        return edgeIds;
    }

    /// <summary>
    /// Classifies the root cause of repetition in a route.
    /// </summary>
    private static string ClassifyRepetitionRootCause(
        double returnSegmentOverlapPct,
        double turnaroundAngle,
        double edgeOverlapM,
        double totalDistanceM,
        int forwardEdgeDensity)
    {
        if (returnSegmentOverlapPct > 0.8 && turnaroundAngle > 170)
            return "out_and_back";
        if (edgeOverlapM > totalDistanceM * 0.1 && returnSegmentOverlapPct < 0.3)
            return "intra_path_overlap";
        if (returnSegmentOverlapPct > 0.3)
            return "parallel_detour";
        if (forwardEdgeDensity < 10)
            return "low_road_density";
        return "mixed";
    }

    /// <summary>
    /// Counts unique edges within a radius of a point (approximate road density).
    /// </summary>
    private int CountEdgesNearPoint(Coordinate point, double radiusM)
    {
        // Step 6: Check density cache (~500m cells)
        int cellLat = (int)(point.Lat * 2e3);
        int cellLon = (int)(point.Lon * 2e3);
        var cellKey = (cellLat, cellLon);
        if (_densityCache != null && _densityCache.TryGetValue(cellKey, out var cached))
            return cached;

        var profileInstance = _mapRepository.RouterDb?.GetSupportedProfile("motorcycle");
        if (profileInstance == null) return 0;

        int count = 0;
        var seenEdges = new HashSet<uint>();
        var sw = _currentContext != null ? System.Diagnostics.Stopwatch.StartNew() : null;

        // Sample 8 directions
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            var samplePoint = RouteGeometryUtils.ProjectPoint(point, angle, radiusM);
            var result = _mapRepository.Router.TryResolve(profileInstance, (float)samplePoint.Lat, (float)samplePoint.Lon, 100);
            if (!result.IsError && !seenEdges.Contains(result.Value.EdgeId))
            {
                seenEdges.Add(result.Value.EdgeId);
                count++;
            }
        }

        if (sw != null)
        {
            sw.Stop();
            _currentContext!.CountEdgesNearPointMs += sw.ElapsedMilliseconds;
            _currentContext!.CountEdgesNearPointCalls += 8;
        }

        // Step 6: Store in density cache (skip during block windows)
        if (_densityCache != null && _edgeBlocker?.HasActiveBlocks != true)
            _densityCache[cellKey] = count;

        return count;
    }

    /// <summary>
    /// Finds a path with custom penalty factor for forward edges.
    /// GraphHopper-inspired: uses real edge weight modification via EdgeBlocker.
    /// </summary>
    private List<Coordinate>? FindPathWithPenalty(
        IProfileInstance profile,
        Coordinate from,
        Coordinate to,
        List<Coordinate> forwardPath,
        double penaltyFactor,
        List<uint>? preResolvedEdgeIds = null,
        RoutingCallCategory category = RoutingCallCategory.Normal)
    {
        // Use pre-resolved edge IDs if available, otherwise resolve fresh
        List<uint> edgeIds;
        if (preResolvedEdgeIds != null && preResolvedEdgeIds.Count > 0)
        {
            edgeIds = preResolvedEdgeIds;
        }
        else
        {
            edgeIds = ResolveForwardEdgeIds(forwardPath);
        }

        if (edgeIds.Count == 0)
            return TimedRoute(profile, from, to, category);

        // Apply penalty to forward edges
        using var penaltyScope = _edgeBlocker.PenaltyEdges(edgeIds, penaltyFactor);

        // Route with penalties applied
        var path = TimedRoute(profile, from, to, category);

        return path;
    }

    /// <summary>
    /// Counts plateaus (consecutive non-overlapping sections) in the path.
    /// GraphHopper uses this to ensure alternatives are meaningfully different.
    /// </summary>
    private int CountPlateaus(List<Coordinate> path, HashSet<RouteGeometryUtils.EdgeKey> forwardEdges)
    {
        if (path.Count < 2)
            return 0;

        int plateaus = 0;
        bool inPlateau = false;
        int plateauLength = 0;
        for (int i = 0; i < path.Count - 1; i++)
        {
            var edgeKey = RouteGeometryUtils.MakeEdgeKey(path[i], path[i + 1]);
            bool isOverlapping = forwardEdges.Contains(edgeKey);

            if (!isOverlapping)
            {
                if (!inPlateau)
                {
                    inPlateau = true;
                    plateauLength = 0;
                }
                plateauLength++;
            }
            else
            {
                if (inPlateau && plateauLength >= MinPlateauLength)
                {
                    plateaus++;
                }
                inPlateau = false;
                plateauLength = 0;
            }
        }

        // Check if we ended in a valid plateau
        if (inPlateau && plateauLength >= MinPlateauLength)
        {
            plateaus++;
        }

        return plateaus;
    }

    /// <summary>
    /// Finds all edge IDs within X meters of a direct line between two points.
    /// Used to block the direct path and force curved routing.
    /// </summary>
    private List<uint> FindEdgesAlongLine(
        IProfileInstance profile,
        Coordinate from,
        Coordinate to,
        double corridorWidthM)
    {
        var edgeIds = new List<uint>();
        var seenEdges = new HashSet<uint>();

        double distance = RouteGeometryUtils.HaversineDistance(from, to);
        int sampleCount = Math.Max(2, (int)(distance / GridSampleIntervalM));

        var sw = _currentContext != null ? System.Diagnostics.Stopwatch.StartNew() : null;
        int callCount = 0;

        for (int i = 0; i <= sampleCount; i++)
        {
            double fraction = (double)i / sampleCount;
            double lat = from.Lat + (to.Lat - from.Lat) * fraction;
            double lon = from.Lon + (to.Lon - from.Lon) * fraction;

            try
            {
                var result = _mapRepository.Router!.TryResolve(profile, (float)lat, (float)lon, (float)corridorWidthM);
                callCount++;
                if (!result.IsError && !seenEdges.Contains(result.Value.EdgeId))
                {
                    edgeIds.Add(result.Value.EdgeId);
                    seenEdges.Add(result.Value.EdgeId);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AlternativePathFinder] Edge resolution failed: {ex.Message}");
            }
        }

        if (sw != null)
        {
            sw.Stop();
            _currentContext!.FindEdgesAlongLineMs += sw.ElapsedMilliseconds;
            _currentContext!.FindEdgesAlongLineCalls += callCount;
        }

        return edgeIds;
    }

    /// <summary>
    /// Routes a single segment between two points (uncached — §14).
    /// Deliberately bypasses RouteAssembler's coordinate-rounding cache because
    /// FindAlternativePath's penalty escalation ladder routes the same (from,to)
    /// pair under different edge-penalty states; a shared cache would return
    /// stale, wrong-penalty-state results.
    /// </summary>
    private List<Coordinate>? RouteSingleSegment(IProfileInstance profile, Coordinate from, Coordinate to)
    {
        var fromCoord = new Itinero.LocalGeo.Coordinate((float)from.Lat, (float)from.Lon);
        var toCoord = new Itinero.LocalGeo.Coordinate((float)to.Lat, (float)to.Lon);

        try
        {
            var segment = _mapRepository.Router!.Calculate(profile, fromCoord, toCoord);
            return RouteGeometryUtils.ExtractCoordinates(segment);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RouteSingleSegment failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Routes a single segment with timing instrumentation (§11a).
    /// Tracks routing time by call category into the active diagnostics context.
    /// </summary>
    private List<Coordinate>? TimedRoute(IProfileInstance profile, Coordinate from, Coordinate to, RoutingCallCategory category)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = RouteSingleSegment(profile, from, to);
        sw.Stop();

        long ms = sw.ElapsedMilliseconds;

        // Track return-path timing by tier
        if (_currentReturnDiag != null)
        {
            _currentReturnDiag.TotalRoutingCalls++;
            switch (category)
            {
                case RoutingCallCategory.Normal:
                    _currentReturnDiag.NormalRoutingMs += ms;
                    break;
                case RoutingCallCategory.VeryHigh:
                    _currentReturnDiag.VeryHighPenaltyRoutingMs += ms;
                    break;
                case RoutingCallCategory.High:
                    _currentReturnDiag.HighPenaltyRoutingMs += ms;
                    break;
                case RoutingCallCategory.PushFallback:
                    _currentReturnDiag.PushFallbackRoutingMs += ms;
                    break;
            }
        }

        // Track forward-path timing separately
        if (category == RoutingCallCategory.Forward && _currentContext != null)
        {
            _currentContext.ForwardPathRoutingMs += ms;
            _currentContext.ForwardPathRoutingCalls++;
        }

        return result;
    }
}
