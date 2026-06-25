using System;
using System.Collections.Generic;
using System.Linq;
using Itinero;
using Itinero.Profiles;
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

    // GraphHopper-inspired parameters
    private const double MaxShareFactor = 0.15;         // Max allowed share with forward path (distance-weighted)

    public AlternativePathFinder(MapRepository mapRepository, RoadClassifier roadClassifier, EdgeBlocker edgeBlocker)
    {
        _mapRepository = mapRepository;
        _roadClassifier = roadClassifier;
        _edgeBlocker = edgeBlocker;
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
    public (List<Coordinate> geometry, double overlapRatio, int plateauCount, double shareWeight, Coordinate turnaroundPoint)? FindRoundTrip(
        IProfileInstance profile,
        Coordinate start,
        double targetDistanceKm,
        bool avoidHighways,
        double turnaroundRatio = 0.5,
        DirectionBias direction = DirectionBias.Any)
    {
        // Convert direction to bearing
        double userBearing = DirectionToBearing(direction);

        // Generate 2 candidates with different seeds, pick the most circular
        var candidate1 = FindSingleRoundTrip(profile, start, targetDistanceKm, avoidHighways, turnaroundRatio, candidateSeed: 0, userBearing);

        if (candidate1 == null)
        {
            var candidate2Only = FindSingleRoundTrip(profile, start, targetDistanceKm, avoidHighways, turnaroundRatio, candidateSeed: 1, userBearing);
            return candidate2Only;
        }

        double spread1 = RouteGeometryUtils.CalculateBearingSpread(candidate1.Value.geometry);

        // Early exit: if candidate 1 is already excellent, skip candidate 2
        if (candidate1.Value.overlapRatio <= 0.10 && spread1 >= 4.0)
            return candidate1;

        var candidate2 = FindSingleRoundTrip(profile, start, targetDistanceKm, avoidHighways, turnaroundRatio, candidateSeed: 1, userBearing);

        if (candidate2 == null) return candidate1;

        // Pick the one with higher bearing spread (more circular)
        double spread2 = RouteGeometryUtils.CalculateBearingSpread(candidate2.Value.geometry);

        return spread1 >= spread2 ? candidate1 : candidate2;
    }

    /// <summary>
    /// Converts a DirectionBias enum to bearing degrees.
    /// </summary>
    private static double DirectionToBearing(DirectionBias direction)
    {
        return direction switch
        {
            DirectionBias.North => 0,
            DirectionBias.Northeast => 45,
            DirectionBias.East => 90,
            DirectionBias.Southeast => 135,
            DirectionBias.South => 180,
            DirectionBias.Southwest => 225,
            DirectionBias.West => 270,
            DirectionBias.Northwest => 315,
            _ => -1 // Any (no preference)
        };
    }

    /// <summary>
    /// Single round trip candidate with seeded randomness for diversity.
    /// Blocks edges along the direct path to force the router to find a curved alternative.
    /// </summary>
    private (List<Coordinate> geometry, double overlapRatio, int plateauCount, double shareWeight, Coordinate turnaroundPoint)? FindSingleRoundTrip(
        IProfileInstance profile,
        Coordinate start,
        double targetDistanceKm,
        bool avoidHighways,
        double turnaroundRatio,
        int candidateSeed,
        double userBearing)
    {
        double targetDistanceM = targetDistanceKm * 1000;
        // Compensate for road detour factor — routed paths are always longer than haversine
        // Empirical average detour factor: roads are ~1.32x longer than straight-line distance
        double estimatedDetourFactor = 1.35;
        double halfDistanceM = targetDistanceM * turnaroundRatio / estimatedDetourFactor;

        // Phase 1: Find turnaround point
        // If user set a direction, use it. Otherwise, use seed-based diversity (0° vs 180°)
        double preferredBearing = userBearing >= 0 ? userBearing : candidateSeed * 180;
        var turnaroundPoint = FindTurnaroundPoint(profile, start, halfDistanceM, preferredBearing, candidateSeed);
        if (turnaroundPoint == null)
            return null;

        // Phase 1b: Block edges along direct path to force curvature
        double corridorWidthM = candidateSeed == 0 ? 800 : 500; // different widths for diversity
        var blockedEdgeIds = FindEdgesAlongLine(profile, start, turnaroundPoint, corridorWidthM);
        using var penaltyScope = blockedEdgeIds.Count > 0
            ? _edgeBlocker.PenaltyEdges(blockedEdgeIds, 10.0)
            : null;

        // Phase 2: Forward path (router forced to curve around blocked zone)
        var forwardPath = RouteSingleSegment(profile, start, turnaroundPoint);
        if (forwardPath == null || forwardPath.Count < 2)
        {
            // Fallback: try without penalty
            penaltyScope?.Dispose();
            forwardPath = RouteSingleSegment(profile, start, turnaroundPoint);
            if (forwardPath == null || forwardPath.Count < 2)
                return null;
        }

        // Extract forward edge set
        var forwardEdgeSet = new HashSet<RouteGeometryUtils.EdgeKey>();
        for (int i = 0; i < forwardPath.Count - 1; i++)
        {
            var edgeKey = new RouteGeometryUtils.EdgeKey(forwardPath[i], forwardPath[i + 1]);
            forwardEdgeSet.Add(edgeKey);
        }

        // Phase 3: Alternative return path
        var alternativeReturn = FindAlternativePath(profile, turnaroundPoint, start, forwardEdgeSet, forwardPath);
        if (alternativeReturn == null)
        {
            alternativeReturn = new List<Coordinate>(forwardPath);
            alternativeReturn.Reverse();
        }

        // Phase 3b: Soft distance preference (adaptive — uses forward path's actual detour ratio)
        double forwardHaversine = RouteGeometryUtils.HaversineDistance(start, turnaroundPoint);
        double forwardActual = RouteGeometryUtils.CalculateDistance(forwardPath);
        double actualDetourRatio = forwardHaversine > 0 ? forwardActual / forwardHaversine : 1.35;
        alternativeReturn = ApplyDistancePreference(profile, turnaroundPoint, start, forwardEdgeSet, forwardPath, alternativeReturn, actualDetourRatio);

        // Phase 4: Merge and calculate metrics
        var mergedGeometry = new List<Coordinate>(forwardPath);
        mergedGeometry.AddRange(alternativeReturn.GetRange(1, alternativeReturn.Count - 1));

        double overlapRatio = RouteGeometryUtils.CalculateSegmentOverlap(mergedGeometry, forwardEdgeSet);
        int plateauCount = CountPlateaus(mergedGeometry, forwardEdgeSet);

        return (mergedGeometry, overlapRatio, plateauCount, overlapRatio, turnaroundPoint);
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
        if (currentDistance <= expectedReturnMax && currentOverlap <= 0.15)
            return currentReturn;

        // Try to find a shorter return path with penalty-based routing
        var shorterReturn = FindPathWithPenalty(profile, from, to, forwardEdges, forwardPath, 5.0);
        if (shorterReturn != null)
        {
            double shorterDistance = RouteGeometryUtils.CalculateDistance(shorterReturn);
            double shorterOverlap = RouteGeometryUtils.CalculateSegmentOverlap(shorterReturn, forwardEdges);

            // Use shorter path if it's significantly shorter and overlap is acceptable
            if (shorterDistance < currentDistance * 0.85 && shorterOverlap <= 0.15)
                return shorterReturn;
        }

        return currentReturn;
    }

    /// <summary>
    /// Generates intermediate waypoints at angular offsets from start→turnaround bearing.
    /// Creates 3 waypoints at 45°, 90°, 135° offset with increasing distance (40%, 65%, 85% of halfDistance).
    /// This forces the forward path to curve through multiple compass directions.
    /// </summary>
    private List<Coordinate> GenerateForwardWaypoints(
        IProfileInstance profile,
        Coordinate start,
        Coordinate turnaround,
        double halfDistanceM,
        double startBearingDeg,
        int candidateSeed)
    {
        var waypoints = new List<Coordinate>();

        // 3 waypoints at 45°, 90°, 135° offset from start→turnaround bearing
        double[] offsets = { 45, 90, 135 };
        double[] distanceFractions = { 0.40, 0.65, 0.85 };

        // Alternate side based on seed (left or right of the direct line)
        double sideSign = candidateSeed % 2 == 0 ? 1 : -1;

        for (int i = 0; i < offsets.Length; i++)
        {
            double bearingDeg = startBearingDeg + offsets[i] * sideSign;
            double bearingRad = bearingDeg * Math.PI / 180;
            double distance = halfDistanceM * distanceFractions[i];

            double lat = start.Lat + (distance / GeoConstants.MetersPerDegreeLat) * Math.Sin(bearingRad);
            double lon = start.Lon + (distance / (GeoConstants.MetersPerDegreeLat *
                Math.Max(Math.Cos(start.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(bearingRad);

            var resolved = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(lat, lon));
            if (resolved != null)
            {
                double actualDist = RouteGeometryUtils.HaversineDistance(start, resolved);
                if (actualDist >= halfDistanceM * 0.2 && actualDist <= halfDistanceM * 0.95)
                    waypoints.Add(resolved);
            }
        }

        return waypoints;
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
            if (bestSpread >= 5.0)
                return currentPath;

            // Extract edge IDs from current forward path
            var edgeIds = new List<uint>();
            var seenEdgeIds = new HashSet<uint>();
            for (int i = 0; i < currentPath.Count - 1; i++)
            {
                double midLat = (currentPath[i].Lat + currentPath[i + 1].Lat) / 2;
                double midLon = (currentPath[i].Lon + currentPath[i + 1].Lon) / 2;

                var result = _mapRepository.Router.TryResolve(profile, (float)midLat, (float)midLon, 50);
                if (!result.IsError && !seenEdgeIds.Contains(result.Value.EdgeId))
                {
                    edgeIds.Add(result.Value.EdgeId);
                    seenEdgeIds.Add(result.Value.EdgeId);
                }
            }

            if (edgeIds.Count == 0)
                break;

            // Apply graduated penalty: 5x, 10x, 15x
            double penaltyFactor = 5.0 + attempt * 5.0;
            using var penaltyScope = _edgeBlocker.PenaltyEdges(edgeIds, penaltyFactor);

            // Block motorways if needed
            using var motorwayScope = avoidHighways
                ? _edgeBlocker.BlockMotorways(profile, start, 200)
                : null;

            // Re-route with penalties
            var curvierPath = RouteSingleSegment(profile, start, turnaround);
            if (curvierPath == null || curvierPath.Count < 2)
                break;

            double newSpread = RouteGeometryUtils.CalculateBearingSpread(curvierPath);
            if (newSpread > bestSpread)
            {
                bestSpread = newSpread;
                currentPath = curvierPath;
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
    private Coordinate? FindTurnaroundPoint(IProfileInstance profile, Coordinate start, double targetDistanceM, double preferredBearingDeg = -1, int candidateSeed = 0)
    {
        // Add random initial bearing offset for route diversity
        double randomOffset = Random.Shared.NextDouble() * 30; // 0-30° offset
        // Primary candidate (seed 0) uses 12 bearings, others use 6 for speed
        int bearingCount = candidateSeed == 0 ? 12 : 6;

        var candidates = new List<(Coordinate coord, double delta, double bearingDeg)>();

        for (int i = 0; i < bearingCount; i++)
        {
            try
            {
                double deg = (i * 360.0 / bearingCount) + randomOffset;
                double bearing = deg * Math.PI / 180;

                // Add ±10% random distance variation
                double distanceVariation = 1.0 + (Random.Shared.NextDouble() - 0.5) * 0.2; // 0.9 to 1.1
                double variedDistance = targetDistanceM * distanceVariation;

                double lat = start.Lat + (variedDistance / GeoConstants.MetersPerDegreeLat) * Math.Sin(bearing);
                double lon = start.Lon + (variedDistance / (GeoConstants.MetersPerDegreeLat *
                    Math.Max(Math.Cos(start.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(bearing);

                var candidate = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(lat, lon));
                if (candidate == null) continue;

                // Verify distance is within acceptable range
                double actualDistance = RouteGeometryUtils.HaversineDistance(start, candidate);
                if (actualDistance < targetDistanceM * 0.6 || actualDistance > targetDistanceM * 1.4) continue;

                double delta = Math.Abs(actualDistance - targetDistanceM);
                // Use RESOLVED bearing (from start to actual road point), not the raw bearing.
                // TryResolveToRoad can shift the point significantly to the nearest road,
                // so the original bearing may not represent the actual direction.
                double actualBearing = RouteGeometryUtils.ComputeBearing(start, candidate);
                candidates.Add((candidate, delta, actualBearing));
            }
            catch
            {
                continue;
            }
        }

        if (candidates.Count == 0)
            return null;

        // If we have a preferred bearing, score candidates by both distance and bearing preference
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
                double combinedScore = normalizedDelta + bearingScore * 1000;

                return (c.coord, score: combinedScore);
            }).OrderBy(x => x.score).ToList();

            return scored[0].coord;
        }

        // Fallback: sort by delta and pick randomly from top 3 for diversity
        candidates.Sort((a, b) => a.delta.CompareTo(b.delta));
        int topN = Math.Min(3, candidates.Count);
        int selectedIndex = Random.Shared.Next(topN);
        return candidates[selectedIndex].coord;
    }

    /// <summary>
    /// Finds an alternative path with edge penalties (GraphHopper-inspired).
    /// Implements graduated fallback mechanism: high penalty -> medium penalty -> no penalty.
    /// Also applies soft distance preference to reduce overshoot.
    /// </summary>
    private List<Coordinate>? FindAlternativePath(
        IProfileInstance profile,
        Coordinate from,
        Coordinate to,
        HashSet<RouteGeometryUtils.EdgeKey> forwardEdges,
        List<Coordinate> forwardPath)
    {
        double haversineReturn = RouteGeometryUtils.HaversineDistance(from, to);

        // Try to find a path with penalties on forward edges
        var normalPath = RouteSingleSegment(profile, from, to);
        if (normalPath == null)
            return null;

        // Check how much this path overlaps with forward edges
        double overlap = RouteGeometryUtils.CalculateSegmentOverlap(normalPath, forwardEdges);

        // If overlap is acceptable, return this path
        if (overlap <= MaxShareFactor)
            return normalPath;

        // Graduated fallback mechanism
        // Level 1: Try with high penalty (8x) - try to find a completely different path
        var highPenaltyResult = FindPathWithPenalty(profile, from, to, forwardEdges, forwardPath, 8.0);
        if (highPenaltyResult != null && RouteGeometryUtils.CalculateSegmentOverlap(highPenaltyResult, forwardEdges) <= MaxShareFactor)
            return highPenaltyResult;

        // Level 2: Try with medium penalty (3x) - accept some overlap
        var mediumPenaltyResult = FindPathWithPenalty(profile, from, to, forwardEdges, forwardPath, 3.0);
        if (mediumPenaltyResult != null && RouteGeometryUtils.CalculateSegmentOverlap(mediumPenaltyResult, forwardEdges) <= MaxShareFactor * 2)
            return mediumPenaltyResult;

        // Level 3: Try without penalty - accept the best available path
        // Apply soft distance preference: prefer paths closer to haversine
        var bestPath = normalPath;
        double bestOverlap = overlap;
        double bestScore = ScoreReturnPath(normalPath, overlap, haversineReturn);

        // Try different push points to find a better alternative
        for (int attempt = 0; attempt < 4; attempt++)
        {
            double pushDist = RouteGeometryUtils.CalculateDistance(normalPath) * 0.2;
            double angle = attempt * Math.PI / 2; // Try 0°, 90°, 180°, 270°

            double pushLat = (from.Lat + to.Lat) / 2 + (pushDist / GeoConstants.MetersPerDegreeLat) * Math.Sin(angle);
            double pushLon = (from.Lon + to.Lon) / 2 + (pushDist / (GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(from.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(angle);

            var pushPoint = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(pushLat, pushLon));
            if (pushPoint == null) continue;

            var viaRoute1 = RouteSingleSegment(profile, from, pushPoint);
            if (viaRoute1 == null) continue;

            var viaRoute2 = RouteSingleSegment(profile, pushPoint, to);
            if (viaRoute2 == null) continue;

            var combined = new List<Coordinate>(viaRoute1);
            combined.AddRange(viaRoute2.GetRange(1, viaRoute2.Count - 1));

            double combinedOverlap = RouteGeometryUtils.CalculateSegmentOverlap(combined, forwardEdges);
            double combinedScore = ScoreReturnPath(combined, combinedOverlap, haversineReturn);
            if (combinedScore < bestScore)
            {
                bestScore = combinedScore;
                bestOverlap = combinedOverlap;
                bestPath = combined;
            }

            if (bestOverlap <= MaxShareFactor * 2)
                break;
        }

        return bestPath;
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
        double distancePenalty = distanceRatio > 1.3 ? (distanceRatio - 1.3) * 50 : 0;

        return overlapPenalty + distancePenalty;
    }

    /// <summary>
    /// Finds a path with custom penalty factor for forward edges.
    /// GraphHopper-inspired: uses real edge weight modification via EdgeBlocker.
    /// </summary>
    private List<Coordinate>? FindPathWithPenalty(
        IProfileInstance profile,
        Coordinate from,
        Coordinate to,
        HashSet<RouteGeometryUtils.EdgeKey> forwardEdges,
        List<Coordinate> forwardPath,
        double penaltyFactor)
    {
        // Resolve Itinero edge IDs directly from forwardPath coordinates
        var profileInstance = _mapRepository.RouterDb?.GetSupportedProfile("motorcycle");
        if (profileInstance == null)
            return RouteSingleSegment(profile, from, to);

        var edgeIds = new List<uint>();
        var seenEdgeIds = new HashSet<uint>();

        for (int i = 0; i < forwardPath.Count - 1; i++)
        {
            double midLat = (forwardPath[i].Lat + forwardPath[i + 1].Lat) / 2;
            double midLon = (forwardPath[i].Lon + forwardPath[i + 1].Lon) / 2;

            var result = _mapRepository.Router.TryResolve(profileInstance, (float)midLat, (float)midLon, 50);
            if (!result.IsError && !seenEdgeIds.Contains(result.Value.EdgeId))
            {
                edgeIds.Add(result.Value.EdgeId);
                seenEdgeIds.Add(result.Value.EdgeId);
            }
        }

        if (edgeIds.Count == 0)
            return RouteSingleSegment(profile, from, to);

        // Apply penalty to forward edges
        using var penaltyScope = _edgeBlocker.PenaltyEdges(edgeIds, penaltyFactor);

        // Route with penalties applied
        var path = RouteSingleSegment(profile, from, to);

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
        const int minPlateauLength = 3; // Minimum edges for a valid plateau

        for (int i = 0; i < path.Count - 1; i++)
        {
            var edgeKey = new RouteGeometryUtils.EdgeKey(path[i], path[i + 1]);
            bool isOverlapping = forwardEdges.Contains(edgeKey) || forwardEdges.Contains(edgeKey.Reversed());

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
                if (inPlateau && plateauLength >= minPlateauLength)
                {
                    plateaus++;
                }
                inPlateau = false;
                plateauLength = 0;
            }
        }

        // Check if we ended in a valid plateau
        if (inPlateau && plateauLength >= minPlateauLength)
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
        int sampleCount = Math.Max(2, (int)(distance / 500)); // sample every 500m

        for (int i = 0; i <= sampleCount; i++)
        {
            double fraction = (double)i / sampleCount;
            double lat = from.Lat + (to.Lat - from.Lat) * fraction;
            double lon = from.Lon + (to.Lon - from.Lon) * fraction;

            try
            {
                var result = _mapRepository.Router!.TryResolve(profile, (float)lat, (float)lon, (float)corridorWidthM);
                if (!result.IsError && !seenEdges.Contains(result.Value.EdgeId))
                {
                    edgeIds.Add(result.Value.EdgeId);
                    seenEdges.Add(result.Value.EdgeId);
                }
            }
            catch
            {
                // Skip unresolvable points
            }
        }

        return edgeIds;
    }

    /// <summary>
    /// Routes a single segment between two points.
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
        catch
        {
            return null;
        }
    }
}
