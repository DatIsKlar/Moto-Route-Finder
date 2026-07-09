using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Itinero;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Helpers;

public static class RouteGeometryUtils
{
    private const double MaxParallelOverlapDistanceM = 25.0;
    private const double MaxParallelOverlapBearingDelta = 30.0;
    private const double ParallelOverlapGridDegrees = 0.0005;
    private const double RecentEdgeWeight = 3.0;
    private const double OldEdgeWeight = 1.0;
    private const double BearingSpreadSampleIntervalM = 500;
    private const double ExcludeDistanceForBearingSpreadM = 1000;
    private const double WindingNumberBearingThreshold = 30;
    private const double SharpTurnAngleThreshold = 90;

    /// <summary>
    /// Shared grid-based parallel overlap detection for divided highways.
    /// For each return-path edge, checks if any forward-path edge is within maxDistM
    /// with similar bearing (within maxBearingDelta). Returns total overlap distance
    /// in meters and optionally the list of return-path segment indices that overlap.
    /// </summary>
    public static (double overlapM, List<int> overlappingIndices) CalculateParallelOverlap(
        List<Coordinate> route, int returnStartIndex,
        double maxDistM = MaxParallelOverlapDistanceM,
        double maxBearingDelta = MaxParallelOverlapBearingDelta)
    {
        double overlapM = 0;
        var overlappingIndices = new List<int>();

        if (returnStartIndex <= 0 || returnStartIndex >= route.Count)
            return (0, overlappingIndices);

        const double gridDeg = ParallelOverlapGridDegrees;

        // Build grid index of forward-path edges
        var forwardGrid = new Dictionary<(int, int), List<(int index, double bearing, double dist)>>();
        for (int i = 0; i < returnStartIndex - 1; i++)
        {
            double midLat = (route[i].Lat + route[i + 1].Lat) / 2;
            double midLon = (route[i].Lon + route[i + 1].Lon) / 2;
            double bearing = ComputeBearing(route[i], route[i + 1]);
            double dist = HaversineDistance(route[i], route[i + 1]);
            int cellLat = (int)Math.Floor(midLat / gridDeg);
            int cellLon = (int)Math.Floor(midLon / gridDeg);
            var cellKey = (cellLat, cellLon);
            if (!forwardGrid.TryGetValue(cellKey, out var list))
            {
                list = new List<(int, double, double)>();
                forwardGrid[cellKey] = list;
            }
            list.Add((i, bearing, dist));
        }

        // Check each return-path edge against nearby forward-path edges
        for (int i = returnStartIndex; i < route.Count - 1; i++)
        {
            double retMidLat = (route[i].Lat + route[i + 1].Lat) / 2;
            double retMidLon = (route[i].Lon + route[i + 1].Lon) / 2;
            double retBearing = ComputeBearing(route[i], route[i + 1]);
            double retDist = HaversineDistance(route[i], route[i + 1]);
            int retCellLat = (int)Math.Floor(retMidLat / gridDeg);
            int retCellLon = (int)Math.Floor(retMidLon / gridDeg);

            bool found = false;
            for (int dLat = -1; dLat <= 1 && !found; dLat++)
            {
                for (int dLon = -1; dLon <= 1 && !found; dLon++)
                {
                    var cellKey = (retCellLat + dLat, retCellLon + dLon);
                    if (!forwardGrid.TryGetValue(cellKey, out var fwdEdges)) continue;

                    foreach (var (fwdIdx, fwdBearing, fwdDist) in fwdEdges)
                    {
                        double fwdMidLat = (route[fwdIdx].Lat + route[fwdIdx + 1].Lat) / 2;
                        double fwdMidLon = (route[fwdIdx].Lon + route[fwdIdx + 1].Lon) / 2;
                        double dist = HaversineDistance(
                            new Coordinate(retMidLat, retMidLon),
                            new Coordinate(fwdMidLat, fwdMidLon));

                        if (dist > maxDistM) continue;

                        double bearingDelta = Math.Abs(retBearing - fwdBearing);
                        if (bearingDelta > 180) bearingDelta = 360 - bearingDelta;
                        if (bearingDelta > maxBearingDelta) continue;

                        overlapM += retDist;
                        overlappingIndices.Add(i);
                        found = true;
                        break;
                    }
                }
            }
        }

        return (overlapM, overlappingIndices);
    }
    private const double HairpinAngleThreshold = 150;
    private const double DefaultMaxBearingSpreadDegrees = 270.0;
    private const double MaxSectorsForCircularity = 12.0;
    private const double CircularitySpreadWeight = 0.5;
    private const double CircularitySectorWeight = 0.3;
    private const double CircularityCompactnessWeight = 0.2;

    /// <summary>
    /// §17: Piecewise-linear curvature score — rewards twistiness with a generous plateau.
    /// Replaces the old Gaussian that penalized routes for being twisty.
    /// Below rampStart → minScore (too straight); ramp → plateau at 100; plateau → decline past excessive.
    /// </summary>
    public static double CurvatureScore(double avgCurvature, MotoRouteFinder.Models.RouteGenerationOptions? options = null)
    {
        double rampStart    = options?.CurvatureRampStartRad    ?? 0.0005;
        double plateauStart = options?.CurvaturePlateauStartRad ?? 0.0018;
        double plateauEnd   = options?.CurvaturePlateauEndRad   ?? 0.0060;
        double excessive    = options?.CurvatureExcessiveRad    ?? 0.0120;
        double minScore     = options?.CurvatureMinScore        ?? 20.0;

        if (avgCurvature <= rampStart) return minScore;
        if (avgCurvature < plateauStart)
            return minScore + (100 - minScore) * (avgCurvature - rampStart) / (plateauStart - rampStart);
        if (avgCurvature <= plateauEnd) return 100;
        if (avgCurvature < excessive)
            return 100 - (100 - minScore) * (avgCurvature - plateauEnd) / (excessive - plateauEnd);
        return minScore;
    }

    public readonly struct EdgeKey : IEquatable<EdgeKey>
    {
        public readonly int Lat1, Lon1, Lat2, Lon2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EdgeKey(int lat1, int lon1, int lat2, int lon2)
        {
            Lat1 = lat1;
            Lon1 = lon1;
            Lat2 = lat2;
            Lon2 = lon2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EdgeKey(Coordinate a, Coordinate b)
        {
            int lat1 = (int)Math.Round(a.Lat / GeoConstants.EdgeSnapGridDegrees);
            int lon1 = (int)Math.Round(a.Lon / GeoConstants.EdgeSnapGridDegrees);
            int lat2 = (int)Math.Round(b.Lat / GeoConstants.EdgeSnapGridDegrees);
            int lon2 = (int)Math.Round(b.Lon / GeoConstants.EdgeSnapGridDegrees);
            // Normalize ordering so both directions of the same edge produce the same key
            if (((long)lat1 << 32 | (uint)lon1) > ((long)lat2 << 32 | (uint)lon2))
            {
                (lat1, lon1, lat2, lon2) = (lat2, lon2, lat1, lon1);
            }
            Lat1 = lat1; Lon1 = lon1; Lat2 = lat2; Lon2 = lon2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(EdgeKey other) =>
            Lat1 == other.Lat1 && Lon1 == other.Lon1 && Lat2 == other.Lat2 && Lon2 == other.Lon2;

        public override bool Equals(object? obj) => obj is EdgeKey other && Equals(other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() =>
            HashCode.Combine(Lat1, Lon1, Lat2, Lon2);

        public static bool operator ==(EdgeKey left, EdgeKey right) => left.Equals(right);
        public static bool operator !=(EdgeKey left, EdgeKey right) => !left.Equals(right);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EdgeKey Reversed() => new EdgeKey(Lat2, Lon2, Lat1, Lon1);
    }
    /// <summary>
    /// Equirectangular approximation of distance in meters. Named "Haversine" for historical reasons.
    /// Accurate to &lt;0.1% for distances under 5 km. Do NOT use for long-distance or high-latitude calculations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double HaversineDistance(Coordinate a, Coordinate b)
    {
        // Equirectangular approximation — <0.1% error for distances <5km
        // Work in degrees: dLon*cosLat and dLat are in degrees, MetersPerDegreeLat converts to meters.
        double dLat = b.Lat - a.Lat;
        double dLon = b.Lon - a.Lon;
        double cosLat = Math.Cos(a.Lat * Math.PI / 180);
        double x = dLon * cosLat;
        double y = dLat;
        return GeoConstants.MetersPerDegreeLat * Math.Sqrt(x * x + y * y);
    }

    public static double CalculateDistance(List<Coordinate> points)
    {
        double total = 0;
        for (int i = 1; i < points.Count; i++)
            total += HaversineDistance(points[i - 1], points[i]);
        return total;
    }

    public static Coordinate ComputeMidpoint(List<Coordinate> segment)
    {
        if (segment.Count == 0)
            return new Coordinate(0, 0);
        if (segment.Count == 1)
            return segment[0];

        double totalDist = CalculateDistance(segment);
        double halfDist = totalDist / 2;
        double cumulative = 0;

        for (int i = 0; i < segment.Count - 1; i++)
        {
            double d = HaversineDistance(segment[i], segment[i + 1]);
            cumulative += d;
            if (cumulative >= halfDist)
            {
                double ratio = (cumulative - halfDist) / d;
                // Interpolate from segment[i+1] backward (1-ratio from segment[i])
                double lat = segment[i + 1].Lat + ratio * (segment[i].Lat - segment[i + 1].Lat);
                double lon = segment[i + 1].Lon + ratio * (segment[i].Lon - segment[i + 1].Lon);
                return new Coordinate(lat, lon);
            }
        }

        return segment[segment.Count / 2];
    }

    public static int FindLoopMidpoint(List<Coordinate> geometry, Coordinate start)
    {
        if (geometry.Count < 4)
            return Math.Max(1, geometry.Count / 2);

        int midIndex = 0;
        double maxDist = 0;
        int searchStart = Math.Max(1, geometry.Count / 4);
        int searchEnd = Math.Min(geometry.Count - 2, geometry.Count * 3 / 4);

        for (int i = searchStart; i <= searchEnd; i++)
        {
            double dist = HaversineDistance(geometry[i], start);
            if (dist > maxDist)
            {
                maxDist = dist;
                midIndex = i;
            }
        }
        return midIndex;
    }

    public static double CalculateAverageCurvature(List<Coordinate> points)
    {
        if (points.Count < 3) return 0;

        double totalHeadingChange = 0;
        double totalDistance = 0;

        for (int i = 1; i < points.Count - 1; i++)
        {
            var prev = points[i - 1];
            var curr = points[i];
            var next = points[i + 1];

            double cosLat = Math.Cos(curr.Lat * Math.PI / 180);
            double metersPerDegLon = GeoConstants.MetersPerDegreeLat * Math.Max(cosLat, GeoConstants.MinCosLat);

            double dx1 = (curr.Lon - prev.Lon) * metersPerDegLon;
            double dy1 = (curr.Lat - prev.Lat) * GeoConstants.MetersPerDegreeLat;
            double dx2 = (next.Lon - curr.Lon) * metersPerDegLon;
            double dy2 = (next.Lat - curr.Lat) * GeoConstants.MetersPerDegreeLat;

            double angle1 = Math.Atan2(dy1, dx1);
            double angle2 = Math.Atan2(dy2, dx2);

            double dAngle = angle2 - angle1;
            dAngle = (dAngle + Math.PI) % (2 * Math.PI) - Math.PI;

            totalHeadingChange += Math.Abs(dAngle);
            totalDistance += HaversineDistance(prev, curr);
        }

        // Include final segment distance
        totalDistance += HaversineDistance(points[^2], points[^1]);

        return totalDistance > 0 ? totalHeadingChange / totalDistance : 0;
    }

    public static double ComputeBearing(Coordinate from, Coordinate to)
    {
        double lat1 = from.Lat * Math.PI / 180;
        double lat2 = to.Lat * Math.PI / 180;
        double dLon = (to.Lon - from.Lon) * Math.PI / 180;

        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        double bearing = Math.Atan2(y, x) * 180 / Math.PI;
        return (bearing + 360) % 360;
    }

    /// <summary>
    /// Converts a compass bearing (degrees, 0=North, CW) to a math angle (radians, 0=East, CCW)
    /// for use with Sin/Cos-based coordinate placement.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double BearingToMathAngle(double bearingDeg)
    {
        return Math.PI / 2 - bearingDeg * Math.PI / 180;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EdgeKey MakeEdgeKey(Coordinate a, Coordinate b) => new EdgeKey(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CountEdges(List<Coordinate> geometry) => Math.Max(0, geometry.Count - 1);

    public static List<Coordinate> SampleAlongGeometry(List<Coordinate> geometry, double intervalM)
    {
        var sampled = new List<Coordinate>();
        if (geometry.Count < 2) return sampled;

        sampled.Add(geometry[0]);
        double accumulated = 0;

        for (int i = 1; i < geometry.Count; i++)
        {
            double dist = HaversineDistance(geometry[i - 1], geometry[i]);
            accumulated += dist;

            while (accumulated >= intervalM && dist > 0)
            {
                double t = (accumulated - intervalM) / dist;
                // Interpolate from geometry[i] backward (1-t from geometry[i-1])
                double lat = geometry[i].Lat + t * (geometry[i - 1].Lat - geometry[i].Lat);
                double lon = geometry[i].Lon + t * (geometry[i - 1].Lon - geometry[i].Lon);
                sampled.Add(new Coordinate(lat, lon));
                accumulated -= intervalM;
            }
        }

        sampled.Add(geometry[^1]);
        return sampled;
    }

    public static List<Coordinate>? AssembleGeometry(List<List<Coordinate>> segments)
    {
        var geometry = new List<Coordinate>();
        for (int i = 0; i < segments.Count; i++)
        {
            if (i == 0)
                geometry.AddRange(segments[i]);
            else
                geometry.AddRange(segments[i].GetRange(1, segments[i].Count - 1));
        }
        return geometry.Count >= 2 ? geometry : null;
    }

    public static Coordinate? FindOverlapCenter(List<Coordinate> segment, HashSet<EdgeKey> usedEdges)
    {
        double totalOverlapDist = 0;
        double weightedLat = 0;
        double weightedLon = 0;

        for (int i = 0; i < segment.Count - 1; i++)
        {
            var key = new EdgeKey(segment[i], segment[i + 1]);
            if (usedEdges.Contains(key))
            {
                double edgeDist = HaversineDistance(segment[i], segment[i + 1]);
                double midLat = (segment[i].Lat + segment[i + 1].Lat) / 2;
                double midLon = (segment[i].Lon + segment[i + 1].Lon) / 2;
                weightedLat += midLat * edgeDist;
                weightedLon += midLon * edgeDist;
                totalOverlapDist += edgeDist;
            }
        }

        if (totalOverlapDist <= 0) return null;
        return new Coordinate(weightedLat / totalOverlapDist, weightedLon / totalOverlapDist);
    }

    /// <summary>
    /// Returns max distance from center to any overlapping edge midpoint.
    /// Used to size the push-point detour distance proportionally to overlap extent.
    /// </summary>
    public static double CalculateOverlapZoneRadius(List<Coordinate> segment, HashSet<EdgeKey> usedEdges, Coordinate center)
    {
        double maxRadius = 0;
        for (int i = 0; i < segment.Count - 1; i++)
        {
            var key = new EdgeKey(segment[i], segment[i + 1]);
            if (usedEdges.Contains(key))
            {
                double midLat = (segment[i].Lat + segment[i + 1].Lat) / 2;
                double midLon = (segment[i].Lon + segment[i + 1].Lon) / 2;
                double dist = HaversineDistance(center, new Coordinate(midLat, midLon));
                if (dist > maxRadius) maxRadius = dist;
            }
        }
        return maxRadius;
    }

    /// <summary>
    /// Finds the compass sector (from start) with fewest forward-path points.
    /// Used to steer the return path toward uncovered sectors for better circularity.
    /// Returns the center bearing of the most deficient 20° sector (0-360°).
    /// </summary>
    public static double GetMostDeficientReturnBearing(Coordinate start, List<Coordinate> forwardPath)
    {
        // 18 sectors × 20° each = 360°
        int[] sectorCounts = new int[18];
        foreach (var p in forwardPath)
        {
            double bearing = ComputeBearing(start, p);
            int sector = (int)(bearing / 20) % 18;
            sectorCounts[sector]++;
        }

        int minSector = 0;
        for (int i = 1; i < 18; i++)
        {
            if (sectorCounts[i] < sectorCounts[minSector])
                minSector = i;
        }

        return minSector * 20 + 10.0; // center of the 20° band
    }

    public static double ComputeSegmentDirection(List<Coordinate> segment, Coordinate nearPoint)
    {
        int closestIdx = 0;
        double closestDist = double.MaxValue;
        for (int i = 0; i < segment.Count; i++)
        {
            double d = HaversineDistance(segment[i], nearPoint);
            if (d < closestDist)
            {
                closestDist = d;
                closestIdx = i;
            }
        }

        int idx2 = Math.Min(closestIdx + 1, segment.Count - 1);
        int idx1 = Math.Max(closestIdx - 1, 0);
        if (idx1 == idx2) idx2 = Math.Min(idx1 + 1, segment.Count - 1);

        return Math.Atan2(
            segment[idx2].Lat - segment[idx1].Lat,
            segment[idx2].Lon - segment[idx1].Lon);
    }

    public static double CalculateSegmentOverlap(List<Coordinate> segment, HashSet<EdgeKey> usedEdges)
    {
        if (segment.Count < 2 || usedEdges.Count == 0) return 0;

        double totalDistance = 0;
        double overlapDistance = 0;

        for (int i = 0; i < segment.Count - 1; i++)
        {
            double edgeDist = HaversineDistance(segment[i], segment[i + 1]);
            totalDistance += edgeDist;

            var key = new EdgeKey(segment[i], segment[i + 1]);

            if (usedEdges.Contains(key))
                overlapDistance += edgeDist;
        }

        return totalDistance > 0 ? overlapDistance / totalDistance : 0;
    }

    public static (double overlap, List<EdgeKey> overlappingEdges) CalculateSegmentOverlapWithEdges(List<Coordinate> segment, HashSet<EdgeKey> usedEdges)
    {
        if (segment.Count < 2 || usedEdges.Count == 0) return (0, new List<EdgeKey>());

        double totalDistance = 0;
        double overlapDistance = 0;
        var overlappingEdges = new List<EdgeKey>();

        for (int i = 0; i < segment.Count - 1; i++)
        {
            double edgeDist = HaversineDistance(segment[i], segment[i + 1]);
            totalDistance += edgeDist;

            var key = new EdgeKey(segment[i], segment[i + 1]);

            if (usedEdges.Contains(key))
            {
                overlapDistance += edgeDist;
                overlappingEdges.Add(key);
            }
        }

        return (totalDistance > 0 ? overlapDistance / totalDistance : 0, overlappingEdges);
    }

    /// <summary>
    /// Calculates weighted overlap where recently-used edges have higher penalty.
    /// edgeAge maps each edge to how many segments ago it was used (0 = most recent).
    /// Recently-used edges (age 0-1) get 3x weight, older edges (age 2+) get 1x weight.
    /// </summary>
    public static double CalculateWeightedOverlap(List<Coordinate> segment, Dictionary<EdgeKey, int> edgeAge)
    {
        if (segment.Count < 2 || edgeAge.Count == 0) return 0;

        double totalDistance = 0;
        double weightedOverlap = 0;

        for (int i = 0; i < segment.Count - 1; i++)
        {
            double edgeDist = HaversineDistance(segment[i], segment[i + 1]);
            totalDistance += edgeDist;

            var key = new EdgeKey(segment[i], segment[i + 1]);
            int age = int.MaxValue;

            if (edgeAge.TryGetValue(key, out int fwdAge))
                age = Math.Min(age, fwdAge);

            if (age < int.MaxValue)
            {
                double weight = age <= 1 ? RecentEdgeWeight : OldEdgeWeight;
                weightedOverlap += edgeDist * weight;
            }
        }

        return totalDistance > 0 ? weightedOverlap / totalDistance : 0;
    }

    /// <summary>
    /// Calculates weighted overlap using birth-segment tracking (generation counter).
    /// edgeBirthSegment maps each edge to the segment number when it was first added.
    /// Age is computed as currentSegment - birthSegment, avoiding O(N) increment passes.
    /// </summary>
    public static double CalculateWeightedOverlap(List<Coordinate> segment, Dictionary<EdgeKey, int> edgeBirthSegment, int currentSegment)
    {
        if (segment.Count < 2 || edgeBirthSegment.Count == 0) return 0;

        double totalDistance = 0;
        double weightedOverlap = 0;

        for (int i = 0; i < segment.Count - 1; i++)
        {
            double edgeDist = HaversineDistance(segment[i], segment[i + 1]);
            totalDistance += edgeDist;

            var key = new EdgeKey(segment[i], segment[i + 1]);
            int age = int.MaxValue;

            if (edgeBirthSegment.TryGetValue(key, out int birthFwd))
                age = Math.Min(age, currentSegment - birthFwd);

            if (age < int.MaxValue)
            {
                double weight = age <= 1 ? RecentEdgeWeight : OldEdgeWeight;
                weightedOverlap += edgeDist * weight;
            }
        }

        return totalDistance > 0 ? weightedOverlap / totalDistance : 0;
    }

    /// <summary>
    /// Calculates how direct a route is (1.0 = perfectly straight, 0.0 = very circuitous).
    /// Ratio of haversine distance to actual routed distance.
    /// </summary>
    public static double CalculateDirectness(List<Coordinate> segment)
    {
        if (segment.Count < 2) return 1.0;

        double routedDistance = 0;
        for (int i = 0; i < segment.Count - 1; i++)
            routedDistance += HaversineDistance(segment[i], segment[i + 1]);

        double haversineDistance = HaversineDistance(segment[0], segment[^1]);

        return routedDistance > 0 ? Math.Min(1.0, haversineDistance / routedDistance) : 0;
    }

    /// <summary>
    /// Record for route shape analysis results.
    /// </summary>
    public record RouteShapeAnalysis(
        double ForwardBearingSpread,
        double ReturnBearingSpread,
        double TurnaroundBearing,
        double ForwardPathCurvature,
        double ReturnPathCurvature,
        double ForwardMaxDeviationFromLine,
        double ReturnMaxDeviationFromLine,
        int ForwardDistinctBearingCount,
        int ReturnDistinctBearingCount,
        int ForwardReturnSectorDifference,
        int ForwardPathWindingNumber,
        double ForwardBearingSpreadExHome,
        double ReturnBearingSpreadExHome,
        double TurnaroundAngle,
        double TurnaroundOffsetFromLine,
        double RouteEfficiency,
        double ForwardPathCompactness,
        double ReturnPathCompactness,
        double AvgSegmentBearing,
        double BearingVariance,
        int TotalRouteBearingChanges,
        double TurnaroundLat,
        double TurnaroundLon);

    /// <summary>
    /// Analyzes the shape of a route to determine if it's circular or straight-line.
    /// Splits the route at the point farthest from start (turnaround) into forward/return paths.
    /// </summary>
    public static RouteShapeAnalysis AnalyzeRouteShape(List<Coordinate> geometry, Coordinate start)
    {
        if (geometry.Count < 3)
            return new RouteShapeAnalysis(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0);

        // Find the point farthest from start (turnaround point)
        int turnaroundIndex = 0;
        double maxDist = 0;
        for (int i = 0; i < geometry.Count; i++)
        {
            double dist = HaversineDistance(start, geometry[i]);
            if (dist > maxDist) { maxDist = dist; turnaroundIndex = i; }
        }

        // Split into forward and return paths
        var forwardPath = geometry.GetRange(0, turnaroundIndex + 1);
        var returnPath = geometry.GetRange(turnaroundIndex, geometry.Count - turnaroundIndex);

        // Calculate bearing spreads
        double forwardSpread = CalculateBearingSpread(forwardPath);
        double returnSpread = CalculateBearingSpread(returnPath);

        // Calculate turnaround bearing
        double turnaroundBearing = ComputeBearing(start, geometry[turnaroundIndex]);

        // Calculate path curvatures (sum of heading changes / path length)
        double forwardCurvature = CalculatePathCurvature(forwardPath);
        double returnCurvature = CalculatePathCurvature(returnPath);

        // Calculate max deviation from straight line
        double forwardDeviation = CalculateMaxDeviationFromLine(forwardPath);
        double returnDeviation = CalculateMaxDeviationFromLine(returnPath);

        // Calculate distinct bearing counts (30° sectors)
        int forwardDistinct = CalculateDistinctBearingCount(forwardPath);
        int returnDistinct = CalculateDistinctBearingCount(returnPath);

        // Calculate sector difference
        int sectorDifference = Math.Abs(forwardDistinct - returnDistinct);

        // Calculate winding number (bearing changes >30°)
        int forwardWinding = CalculateWindingNumber(forwardPath);
        int totalWinding = CalculateWindingNumber(geometry);

        // Calculate bearing spreads excluding home (first/last 1km)
        double forwardSpreadExHome = CalculateBearingSpreadExcludingDistance(forwardPath, ExcludeDistanceForBearingSpreadM, true);
        double returnSpreadExHome = CalculateBearingSpreadExcludingDistance(returnPath, ExcludeDistanceForBearingSpreadM, false);

        // Calculate turnaround angle (angle between forward arrival and return departure)
        double turnaroundAngle = 0;
        if (forwardPath.Count >= 2 && returnPath.Count >= 2)
        {
            double forwardArrivalBearing = ComputeBearing(forwardPath[^2], forwardPath[^1]);
            double returnDepartureBearing = ComputeBearing(returnPath[0], returnPath[1]);
            turnaroundAngle = Math.Abs(returnDepartureBearing - forwardArrivalBearing);
            if (turnaroundAngle > 180) turnaroundAngle = 360 - turnaroundAngle;
        }

        // Calculate turnaround offset from line (perpendicular distance from start→end line)
        double turnaroundOffset = CalculatePerpendicularDistance(start, geometry[^1], geometry[turnaroundIndex]);

        // Calculate route efficiency (routed distance / haversine distance)
        double routedDistance = 0;
        for (int i = 0; i < geometry.Count - 1; i++)
            routedDistance += HaversineDistance(geometry[i], geometry[i + 1]);
        double haversineDistance = HaversineDistance(start, geometry[^1]);
        // For loop routes where start ≈ end, use bbox diagonal as denominator
        double efficiency;
        if (haversineDistance > 100) // >100m — not a loop closure
            efficiency = routedDistance / haversineDistance;
        else
        {
            // Loop route: use bounding box diagonal as reference
            double minLat = geometry.Min(c => c.Lat), maxLat = geometry.Max(c => c.Lat);
            double minLon = geometry.Min(c => c.Lon), maxLon = geometry.Max(c => c.Lon);
            double bboxDiag = HaversineDistance(new Coordinate(minLat, minLon), new Coordinate(maxLat, maxLon));
            efficiency = bboxDiag > 0 ? routedDistance / bboxDiag : 1.0;
        }

        // Calculate path compactness (bbox area / path length)
        double forwardCompactness = CalculatePathCompactness(forwardPath);
        double returnCompactness = CalculatePathCompactness(returnPath);

        // Calculate bearing statistics using circular mean (correct for angular quantities)
        var allBearings = new List<double>();
        for (int i = 0; i < geometry.Count - 1; i++)
            allBearings.Add(ComputeBearing(geometry[i], geometry[i + 1]));
        double avgBearing = 0;
        double bearingVariance = 0;
        if (allBearings.Count > 0)
        {
            // Circular mean via vector addition
            double sinSum = allBearings.Average(b => Math.Sin(b * Math.PI / 180));
            double cosSum = allBearings.Average(b => Math.Cos(b * Math.PI / 180));
            avgBearing = Math.Atan2(sinSum, cosSum) * 180 / Math.PI;
            if (avgBearing < 0) avgBearing += 360;

            // Circular variance: 1 - resultant length
            double resultantLength = Math.Sqrt(sinSum * sinSum + cosSum * cosSum);
            bearingVariance = 1.0 - resultantLength;
        }

        return new RouteShapeAnalysis(
            forwardSpread, returnSpread, turnaroundBearing,
            forwardCurvature, returnCurvature,
            forwardDeviation, returnDeviation,
            forwardDistinct, returnDistinct,
            sectorDifference, forwardWinding,
            forwardSpreadExHome, returnSpreadExHome,
            turnaroundAngle, turnaroundOffset,
            efficiency, forwardCompactness, returnCompactness,
            avgBearing, bearingVariance, totalWinding,
            geometry[turnaroundIndex].Lat, geometry[turnaroundIndex].Lon);
    }

    /// <summary>
    /// Calculates the turnaround angle: the angle between the arrival bearing at the turnaround
    /// point and the departure bearing from it. A sharp U-turn gives ~180°; a gradual curve
    /// gives a smaller angle.
    /// </summary>
    /// <param name="forwardPath">Path from start to turnaround point</param>
    /// <param name="returnPath">Path from turnaround point back to start (optional; if null, uses reverse of forwardPath tail)</param>
    public static double CalculateTurnaroundAngle(List<Coordinate> forwardPath, List<Coordinate>? returnPath = null)
    {
        if (forwardPath.Count < 3) return 0;

        // Arrival bearing: from second-to-last point to last point (the turnaround)
        int arrivalIdx = Math.Max(0, forwardPath.Count - 2);
        double arrivalBearing = ComputeBearing(forwardPath[arrivalIdx], forwardPath[^1]);

        // Departure bearing: from turnaround point to first point of return path
        double departureBearing;
        if (returnPath != null && returnPath.Count >= 2)
        {
            departureBearing = ComputeBearing(returnPath[0], returnPath[1]);
        }
        else
        {
            // Fallback: reverse of arrival (measures pure U-turn)
            departureBearing = ComputeBearing(forwardPath[^1], forwardPath[arrivalIdx]);
        }

        double angle = Math.Abs(departureBearing - arrivalBearing);
        if (angle > 180) angle = 360 - angle;
        return angle;
    }

    public static double CalculateBearingSpread(List<Coordinate> path)
    {
        if (path.Count < 2) return 0;

        // Sample bearings every ~500m, computing bearing between sample points (not consecutive coords)
        var bearings = new List<double>();
        double accumulatedDist = 0;
        double sampleInterval = BearingSpreadSampleIntervalM;
        int lastSampleIndex = 0;

        for (int i = 1; i < path.Count; i++)
        {
            accumulatedDist += HaversineDistance(path[i - 1], path[i]);
            if (accumulatedDist >= sampleInterval)
            {
                bearings.Add(ComputeBearing(path[lastSampleIndex], path[i]));
                lastSampleIndex = i;
                accumulatedDist -= sampleInterval;
            }
        }

        if (bearings.Count == 0) return 0;

        // Calculate angular coverage: what fraction of the 360° compass is covered
        // Sort bearings and find the largest gap — coverage = 360° - largest gap
        bearings.Sort();
        double maxGap = 0;
        for (int i = 1; i < bearings.Count; i++)
        {
            double gap = bearings[i] - bearings[i - 1];
            if (gap > maxGap) maxGap = gap;
        }
        // Check wrap-around gap (from last bearing back to first)
        double wrapGap = (360 - bearings[^1]) + bearings[0];
        if (wrapGap > maxGap) maxGap = wrapGap;

        return 360.0 - maxGap;
    }

    /// <summary>
    /// Calculates bearing spread excluding a distance from the start or end.
    /// </summary>
    private static double CalculateBearingSpreadExcludingDistance(List<Coordinate> path, double excludeDistanceM, bool fromStart)
    {
        if (path.Count < 2) return 0;

        double accumulatedDist = 0;
        var filteredPath = new List<Coordinate>();
        bool including = !fromStart;

        if (fromStart)
        {
            filteredPath.Add(path[0]);
            for (int i = 0; i < path.Count - 1; i++)
            {
                accumulatedDist += HaversineDistance(path[i], path[i + 1]);
                if (accumulatedDist >= excludeDistanceM)
                    including = true;
                if (including)
                    filteredPath.Add(path[i + 1]);
            }
        }
        else
        {
            accumulatedDist = 0;
            for (int i = 0; i < path.Count; i++)
                filteredPath.Add(path[i]);

            filteredPath.Reverse();
            var reversedFiltered = new List<Coordinate>();
            reversedFiltered.Add(filteredPath[0]);
            accumulatedDist = 0;
            including = false;
            for (int i = 0; i < filteredPath.Count - 1; i++)
            {
                accumulatedDist += HaversineDistance(filteredPath[i], filteredPath[i + 1]);
                if (accumulatedDist >= excludeDistanceM)
                    including = true;
                if (including)
                    reversedFiltered.Add(filteredPath[i + 1]);
            }
            reversedFiltered.Reverse();
            filteredPath = reversedFiltered;
        }

        return CalculateBearingSpread(filteredPath);
    }

    /// <summary>
    /// Calculates path curvature as sum of absolute heading changes / path length.
    /// Units: radians per meter. Higher = more curved.
    /// </summary>
    private static double CalculatePathCurvature(List<Coordinate> path)
    {
        if (path.Count < 3) return 0;

        double totalHeadingChange = 0;
        double totalDistance = 0;

        for (int i = 1; i < path.Count - 1; i++)
        {
            double bearing1 = ComputeBearing(path[i - 1], path[i]);
            double bearing2 = ComputeBearing(path[i], path[i + 1]);
            double headingChange = Math.Abs(bearing2 - bearing1);
            if (headingChange > 180) headingChange = 360 - headingChange;
            totalHeadingChange += headingChange * Math.PI / 180; // Convert to radians
            totalDistance += HaversineDistance(path[i - 1], path[i]);
        }

        // Include final segment distance
        totalDistance += HaversineDistance(path[^2], path[^1]);

        return totalDistance > 0 ? totalHeadingChange / totalDistance : 0;
    }

    /// <summary>
    /// Calculates max perpendicular distance from the start→end line.
    /// Higher values mean the path deviates more from a straight line.
    /// </summary>
    private static double CalculateMaxDeviationFromLine(List<Coordinate> path)
    {
        if (path.Count < 3) return 0;

        Coordinate lineStart = path[0];
        Coordinate lineEnd = path[^1];
        double maxDeviation = 0;

        for (int i = 1; i < path.Count - 1; i++)
        {
            double deviation = CalculatePerpendicularDistance(lineStart, lineEnd, path[i]);
            if (deviation > maxDeviation)
                maxDeviation = deviation;
        }

        return maxDeviation;
    }

    /// <summary>
    /// Calculates perpendicular distance from a point to a line (in meters).
    /// </summary>
    private static double CalculatePerpendicularDistance(Coordinate lineStart, Coordinate lineEnd, Coordinate point)
    {
        double lineLength = HaversineDistance(lineStart, lineEnd);
        if (lineLength == 0) return HaversineDistance(lineStart, point);

        // Work entirely in radians with cos-latitude scaling for longitude
        double lat1 = lineStart.Lat * Math.PI / 180;
        double lon1 = lineStart.Lon * Math.PI / 180;
        double lat2 = lineEnd.Lat * Math.PI / 180;
        double lon2 = lineEnd.Lon * Math.PI / 180;
        double lat3 = point.Lat * Math.PI / 180;
        double lon3 = point.Lon * Math.PI / 180;
        double cosLat = Math.Cos((lat1 + lat2) / 2);

        // Vector from lineStart to lineEnd (radians, cos-scaled lon)
        double dx = (lon2 - lon1) * cosLat;
        double dy = lat2 - lat1;

        // Vector from lineStart to point (radians, cos-scaled lon)
        double px = (lon3 - lon1) * Math.Cos((lat1 + lat3) / 2);
        double py = lat3 - lat1;

        // Project point onto line
        double t = (px * dx + py * dy) / (dx * dx + dy * dy);
        t = Math.Max(0, Math.Min(1, t)); // Clamp to line segment

        // Residual vector in radians (cos-scaled lon)
        double residX = px - t * dx;
        double residY = py - t * dy;

        // Convert radians to meters: multiply by (180/π) to get degrees, then by MetersPerDegreeLat
        return Math.Sqrt(residX * residX + residY * residY) * (180.0 / Math.PI) * GeoConstants.MetersPerDegreeLat;
    }

    /// <summary>
    /// Calculates how many distinct 30° bearing sectors a path visits.
    /// </summary>
    private static int CalculateDistinctBearingCount(List<Coordinate> path)
    {
        if (path.Count < 2) return 0;

        var sectors = new HashSet<int>();
        for (int i = 0; i < path.Count - 1; i++)
        {
            double bearing = ComputeBearing(path[i], path[i + 1]);
            int sector = (int)(bearing / 30) % 12;
            sectors.Add(sector);
        }

        return sectors.Count;
    }

    /// <summary>
    /// Calculates winding number (count of bearing changes >30°).
    /// </summary>
    private static int CalculateWindingNumber(List<Coordinate> path)
    {
        if (path.Count < 3) return 0;

        int windingCount = 0;
        for (int i = 1; i < path.Count - 1; i++)
        {
            double bearing1 = ComputeBearing(path[i - 1], path[i]);
            double bearing2 = ComputeBearing(path[i], path[i + 1]);
            double delta = Math.Abs(bearing2 - bearing1);
            if (delta > 180) delta = 360 - delta;
            if (delta > WindingNumberBearingThreshold) windingCount++;
        }

        return windingCount;
    }

    /// <summary>
    /// Calculates path compactness (bbox area / path length).
    /// </summary>
    private static double CalculatePathCompactness(List<Coordinate> path)
    {
        if (path.Count < 2) return 0;

        double minLat = path[0].Lat, maxLat = path[0].Lat;
        double minLon = path[0].Lon, maxLon = path[0].Lon;
        for (int i = 1; i < path.Count; i++)
        {
            if (path[i].Lat < minLat) minLat = path[i].Lat;
            if (path[i].Lat > maxLat) maxLat = path[i].Lat;
            if (path[i].Lon < minLon) minLon = path[i].Lon;
            if (path[i].Lon > maxLon) maxLon = path[i].Lon;
        }

        double bboxHeight = (maxLat - minLat) * GeoConstants.MetersPerDegreeLat;
        double avgLat = (minLat + maxLat) / 2;
        double bboxWidth = (maxLon - minLon) * GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(avgLat * Math.PI / 180), GeoConstants.MinCosLat);

        double bboxArea = bboxHeight * bboxWidth;

        double pathLength = 0;
        for (int i = 0; i < path.Count - 1; i++)
            pathLength += HaversineDistance(path[i], path[i + 1]);

        return pathLength > 0 ? bboxArea / pathLength : 0;
    }

    public static List<EdgeKey> ExtractEdges(List<Coordinate> geometry)
    {
        var edges = new List<EdgeKey>();
        for (int i = 0; i < geometry.Count - 1; i++)
            edges.Add(new EdgeKey(geometry[i], geometry[i + 1]));
        return edges;
    }

    /// <summary>
    /// Populates an existing HashSet with edge keys from geometry.
    /// Reuses the provided set to avoid allocation per call.
    /// </summary>
    public static void ExtractEdgesInto(List<Coordinate> geometry, HashSet<EdgeKey> target)
    {
        for (int i = 0; i < geometry.Count - 1; i++)
            target.Add(new EdgeKey(geometry[i], geometry[i + 1]));
    }

    /// <summary>
    /// Builds a unified edge key set from multiple segments.
    /// </summary>
    public static HashSet<EdgeKey> BuildForwardEdgeKeySet(List<List<Coordinate>> segments)
    {
        var edgeKeys = new HashSet<EdgeKey>();
        foreach (var seg in segments)
            foreach (var key in ExtractEdges(seg))
                edgeKeys.Add(key);
        return edgeKeys;
    }

    /// <summary>
    /// Calculates the total overlap ratio across all segments.
    /// Returns the fraction of edges that appear in more than one segment.
    /// Used for early repetition detection during forward path generation.
    /// </summary>
    public static double CalculateTotalOverlapRatio(List<List<Coordinate>> segments, Dictionary<EdgeKey, int>? edgeCounts = null)
    {
        if (segments.Count < 2) return 0;

        edgeCounts ??= new Dictionary<EdgeKey, int>();
        edgeCounts.Clear();
        int totalEdges = 0;

        foreach (var seg in segments)
        {
            for (int i = 0; i < seg.Count - 1; i++)
            {
                var key = new EdgeKey(seg[i], seg[i + 1]);
                edgeCounts[key] = edgeCounts.GetValueOrDefault(key) + 1;
                totalEdges++;
            }
        }

        if (totalEdges == 0) return 0;

        int reusedEdges = 0;
        foreach (var count in edgeCounts.Values)
        {
            if (count > 1) reusedEdges += count - 1;
        }

        return (double)reusedEdges / totalEdges;
    }

    /// <summary>
    /// Populates overlapping edge count and IDs on a DebugStemEvent.
    /// </summary>
    public static void CaptureOverlappingEdgeIds(Models.DebugStemEvent evt, List<Coordinate> segment, HashSet<EdgeKey> usedEdges)
    {
        var (overlap, overlappingEdges) = RouteGeometryUtils.CalculateSegmentOverlapWithEdges(segment, usedEdges);
        if (overlap > 0)
        {
            evt.OverlappingEdgeCount = overlappingEdges.Count;
            evt.OverlappingEdgeIds = overlappingEdges.Take(20)
                .Select(e => (uint)((e.Lat1 * 31 + e.Lon1) * 31 + e.Lat2) * 31 + (uint)e.Lon2).ToList();
        }
    }

    public static List<Coordinate> ExtractCoordinates(Itinero.Route route)
    {
        var coordinates = new List<Coordinate>();
        if (route?.Shape != null)
        {
            foreach (var point in route.Shape)
                coordinates.Add(new Coordinate(point.Latitude, point.Longitude));
        }
        return coordinates;
    }

    public static List<(Coordinate From, Coordinate To)> ExtractRepetitionSegments(List<Coordinate> fullRoute, int outAndBackIndex)
    {
        var segments = new List<(Coordinate From, Coordinate To)>();

        // Pass 1: Exact edge duplicates (same as original method)
        var seenEdges = new Dictionary<EdgeKey, Coordinate>();
        for (int i = 0; i < fullRoute.Count - 1; i++)
        {
            var key = new EdgeKey(fullRoute[i], fullRoute[i + 1]);

            if (seenEdges.ContainsKey(key))
                segments.Add((fullRoute[i], fullRoute[i + 1]));
            else
                seenEdges[key] = fullRoute[i];
        }

        // Pass 2: Out-and-back overlap (forward path edges matched against return path)
        if (outAndBackIndex > 0 && outAndBackIndex < fullRoute.Count)
        {
            var outboundEdges = new HashSet<EdgeKey>();
            for (int i = 0; i < outAndBackIndex - 1; i++)
            {
                var key = new EdgeKey(fullRoute[i], fullRoute[i + 1]);
                outboundEdges.Add(key);
            }

            for (int i = outAndBackIndex; i < fullRoute.Count - 1; i++)
            {
                var key = new EdgeKey(fullRoute[i], fullRoute[i + 1]);
                if (outboundEdges.Contains(key))
                    segments.Add((fullRoute[i], fullRoute[i + 1]));
            }

            // Pass 3: Proximity-based parallel detection for divided highways
            // For each return-path edge, check if any forward-path edge is within 25m
            // with similar bearing (within 30°). Catches overlap on divided roads where
            // forward/return paths are on opposite sides of the median.
            var (_, parallelIndices) = CalculateParallelOverlap(fullRoute, outAndBackIndex);
            foreach (var idx in parallelIndices)
                segments.Add((fullRoute[idx], fullRoute[idx + 1]));
        }

        return segments;
    }

    public static double DetectOutAndBackOverlap(List<Coordinate> fullRoute, int returnToStartIndex)
    {
        if (returnToStartIndex <= 0 || returnToStartIndex >= fullRoute.Count)
            return 0;

        int outboundCount = returnToStartIndex;
        int returnCount = fullRoute.Count - returnToStartIndex;

        if (outboundCount < 2 || returnCount < 2)
            return 0;

        var outboundEdges = new Dictionary<EdgeKey, double>();
        for (int i = 0; i < outboundCount - 1; i++)
        {
            var key = new EdgeKey(fullRoute[i], fullRoute[i + 1]);
            double dist = HaversineDistance(fullRoute[i], fullRoute[i + 1]);
            outboundEdges[key] = dist;
        }

        double overlapDist = 0;
        for (int i = returnToStartIndex; i < fullRoute.Count - 1; i++)
        {
            var key = new EdgeKey(fullRoute[i], fullRoute[i + 1]);
            double dist = HaversineDistance(fullRoute[i], fullRoute[i + 1]);

            if (outboundEdges.ContainsKey(key))
                overlapDist += dist;
        }

        return overlapDist;
    }

    private static double DistancePointToSegment(Coordinate point, double lat1, double lon1, double lat2, double lon2)
    {
        double cosLat = Math.Cos(point.Lat * Math.PI / 180);
        double metersPerDegLon = GeoConstants.MetersPerDegreeLat * Math.Max(cosLat, GeoConstants.MinCosLat);

        double px = (point.Lon - lon1) * metersPerDegLon;
        double py = (point.Lat - lat1) * GeoConstants.MetersPerDegreeLat;
        double ax = (lon2 - lon1) * metersPerDegLon;
        double ay = (lat2 - lat1) * GeoConstants.MetersPerDegreeLat;

        double lenSq = ax * ax + ay * ay;
        if (lenSq == 0) return Math.Sqrt(px * px + py * py);

        double t = Math.Max(0, Math.Min(1, (px * ax + py * ay) / lenSq));
        double projX = t * ax - px;
        double projY = t * ay - py;

        return Math.Sqrt(projX * projX + projY * projY);
    }

    public static (double centroidLat, double centroidLon) CalculateCentroid(List<Coordinate> points)
    {
        if (points.Count == 0) return (0, 0);
        double sumLat = 0, sumLon = 0;
        foreach (var p in points)
        {
            sumLat += p.Lat;
            sumLon += p.Lon;
        }
        return (sumLat / points.Count, sumLon / points.Count);
    }

    public static double CalculateBoundingBoxAreaKm2(List<Coordinate> points)
    {
        if (points.Count < 2) return 0;
        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;
        foreach (var p in points)
        {
            if (p.Lat < minLat) minLat = p.Lat;
            if (p.Lat > maxLat) maxLat = p.Lat;
            if (p.Lon < minLon) minLon = p.Lon;
            if (p.Lon > maxLon) maxLon = p.Lon;
        }
        double heightKm = (maxLat - minLat) * GeoConstants.KmPerDegreeLat;
        double avgLat = (minLat + maxLat) / 2;
        double widthKm = (maxLon - minLon) * GeoConstants.KmPerDegreeLat * Math.Max(Math.Cos(avgLat * Math.PI / 180), GeoConstants.MinCosLat);
        return heightKm * widthKm;
    }

    public static double CalculateMaxDistanceFromStart(List<Coordinate> points, Coordinate start)
    {
        double maxDist = 0;
        foreach (var p in points)
        {
            double dist = HaversineDistance(start, p);
            if (dist > maxDist) maxDist = dist;
        }
        return maxDist / 1000;
    }

    public static int CountSectorsVisited(List<Coordinate> points, Coordinate start)
    {
        var sectors = new HashSet<int>();
        foreach (var p in points)
        {
            double bearing = ComputeBearing(start, p);
            int sector = (int)(bearing / 20) % 18;
            sectors.Add(sector);
        }
        return sectors.Count;
    }

    public static (int turns, int sharp, int hairpins, double avgAngle, double straightRatio) AnalyzeTurns(
        List<Coordinate> points, double minAngleDeg = 30)
    {
        if (points.Count < 3) return (0, 0, 0, 0, 1.0);

        int turns = 0, sharp = 0, hairpins = 0;
        double totalAngle = 0;
        int angleCount = 0;
        double straightMeters = 0, totalMeters = 0;

        double prevBearing = ComputeBearing(points[0], points[1]);

        for (int i = 2; i < points.Count; i++)
        {
            double currBearing = ComputeBearing(points[i - 1], points[i]);
            double delta = Math.Abs(currBearing - prevBearing);
            if (delta > 180) delta = 360 - delta;

            double segDist = HaversineDistance(points[i - 2], points[i - 1]);
            totalMeters += segDist;

            if (delta < minAngleDeg)
            {
                straightMeters += segDist;
            }
            else
            {
                turns++;
                totalAngle += delta;
                angleCount++;
                if (delta > SharpTurnAngleThreshold) sharp++;
                if (delta > HairpinAngleThreshold) hairpins++;
            }

            prevBearing = currBearing;
        }

        double lastSegDist = HaversineDistance(points[points.Count - 2], points[points.Count - 1]);
        if (totalMeters > 0)
        {
            straightMeters += lastSegDist;
            totalMeters += lastSegDist;
        }
        double straightRatio = totalMeters > 0 ? straightMeters / totalMeters : 1.0;
        double avgAngle = angleCount > 0 ? totalAngle / angleCount : 0;

        return (turns, sharp, hairpins, Math.Round(avgAngle, 1), Math.Round(straightRatio, 3));
    }

    public static (double curvature, double overlapPct) AnalyzeReturnSegment(
        List<Coordinate> returnSegment, List<List<Coordinate>> forwardSegments)
    {
        double curvature = CalculateAverageCurvature(returnSegment);

        var forwardEdges = new HashSet<EdgeKey>();
        foreach (var seg in forwardSegments)
        {
            for (int i = 0; i < seg.Count - 1; i++)
                forwardEdges.Add(MakeEdgeKey(seg[i], seg[i + 1]));
        }

        double overlapM = 0;
        double totalM = 0;
        for (int i = 0; i < returnSegment.Count - 1; i++)
        {
            double dist = HaversineDistance(returnSegment[i], returnSegment[i + 1]);
            totalM += dist;
            var key = MakeEdgeKey(returnSegment[i], returnSegment[i + 1]);
            if (forwardEdges.Contains(key))
                overlapM += dist;
        }

        double overlapPct = totalM > 0 ? overlapM / totalM : 0;
        return (Math.Round(curvature, 4), Math.Round(overlapPct, 3));
    }

    /// <summary>
    /// Calculates circularity score with sub-component breakdown.
    /// Returns (total, spreadScore, sectorScore, compactnessScore).
    /// </summary>
    public static (double total, double spreadScore, double sectorScore, double compactnessScore) CalculateCircularityScoreWithSubScores(List<Coordinate> geometry, double maxBearingSpread = DefaultMaxBearingSpreadDegrees)
    {
        if (geometry.Count < 3) return (0, 0, 0, 0);

        // Bearing spread component (50% weight)
        double spread = CalculateBearingSpread(geometry);
        double spreadScore = Math.Min(100, spread / maxBearingSpread * 100);

        // Sector coverage component (30% weight) — how many 30° sectors are visited
        int distinctSectors = CalculateDistinctBearingCount(geometry);
        double sectorScore = Math.Min(100, distinctSectors / MaxSectorsForCircularity * 100);

        // Compactness component (20% weight) — Polsby-Popper isoperimetric quotient
        // PP = 4π × Area / Perimeter² — perfect circle = 1.0, out-and-back ≈ 0
        var hull = ComputeConvexHull(geometry);
        double sumLat = 0;
        for (int i = 0; i < geometry.Count; i++) sumLat += geometry[i].Lat;
        double centroidLat = sumLat / geometry.Count;
        double hullArea = ComputePolygonArea(hull, centroidLat);
        double totalDistanceM = CalculateDistance(geometry);

        double pp = totalDistanceM > 0
            ? (4.0 * Math.PI * hullArea) / (totalDistanceM * totalDistanceM)
            : 0;
        double compactnessScore = Math.Min(100, pp * 100);

        double total = spreadScore * CircularitySpreadWeight + sectorScore * CircularitySectorWeight + compactnessScore * CircularityCompactnessWeight;
        return (total, spreadScore, sectorScore, compactnessScore);
    }

    /// <summary>
    /// Computes the convex hull of a set of coordinates using Andrew's monotone chain algorithm.
    /// </summary>
    private static List<Coordinate> ComputeConvexHull(List<Coordinate> points)
    {
        if (points.Count <= 1) return new List<Coordinate>(points);

        // Sort points lexicographically (by lat, then lon)
        var sorted = points.OrderBy(p => p.Lat).ThenBy(p => p.Lon).ToList();

        var hull = new List<Coordinate>();

        // Build lower hull
        foreach (var p in sorted)
        {
            while (hull.Count >= 2 && CrossProduct(hull[^2], hull[^1], p) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }

        // Build upper hull
        int lowerCount = hull.Count + 1;
        for (int i = sorted.Count - 2; i >= 0; i--)
        {
            while (hull.Count >= lowerCount && CrossProduct(hull[^2], hull[^1], sorted[i]) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(sorted[i]);
        }

        hull.RemoveAt(hull.Count - 1); // Remove duplicate start point
        return hull;
    }

    private static double CrossProduct(Coordinate o, Coordinate a, Coordinate b)
    {
        return (a.Lat - o.Lat) * (b.Lon - o.Lon) - (a.Lon - o.Lon) * (b.Lat - o.Lat);
    }

    /// <summary>
    /// Computes the area of a polygon using the shoelace formula, with coordinates converted to meters.
    /// </summary>
    private static double ComputePolygonArea(List<Coordinate> polygon, double referenceLat)
    {
        if (polygon.Count < 3) return 0;

        double cosLat = Math.Max(Math.Cos(referenceLat * Math.PI / 180), GeoConstants.MinCosLat);
        double area = 0;

        for (int i = 0; i < polygon.Count; i++)
        {
            int j = (i + 1) % polygon.Count;
            double xi = polygon[i].Lon * cosLat;
            double yi = polygon[i].Lat;
            double xj = polygon[j].Lon * cosLat;
            double yj = polygon[j].Lat;
            area += (xi * yj) - (xj * yi);
        }

        return Math.Abs(area / 2.0) * GeoConstants.MetersPerDegreeLat * GeoConstants.MetersPerDegreeLat;
    }

    public static Coordinate ProjectPoint(Coordinate origin, double bearingRad, double distanceM)
    {
        double cosLat = Math.Cos(origin.Lat * Math.PI / 180);
        double lat = origin.Lat + (distanceM / GeoConstants.MetersPerDegreeLat) * Math.Sin(bearingRad);
        double lon = origin.Lon + (distanceM / (GeoConstants.MetersPerDegreeLat * Math.Max(cosLat, GeoConstants.MinCosLat))) * Math.Cos(bearingRad);
        return new Coordinate(lat, lon);
    }

    public static List<Coordinate> ConcatenatePaths(List<Coordinate> a, List<Coordinate> b)
    {
        var result = new List<Coordinate>(a.Count + b.Count - 1);
        result.AddRange(a);
        result.AddRange(b.GetRange(1, b.Count - 1));
        return result;
    }

    /// <summary>
    /// Grid-based spatial index for fast radius queries and nearest-edge lookups.
    /// Partitions edges into grid cells so CountEdgesInRadius
    /// only checks edges in nearby cells instead of the full set.
    /// </summary>
    public class EdgeSpatialIndex
    {
        // Grid cell size in degrees (~5km at equator, matching the 5000m query radius)
        private const double CellSizeDegrees = 0.05;
        private const double InvCellSize = 1.0 / CellSizeDegrees;

        private readonly Dictionary<(int, int), List<EdgeKey>> _grid = new();
        private int _edgeCount;

        public int EdgeCount => _edgeCount;

        public void Clear()
        {
            foreach (var cell in _grid.Values)
                cell.Clear();
            _grid.Clear();
            _edgeCount = 0;
        }

        public void Add(EdgeKey edge)
        {
            double lat1 = edge.Lat1 * GeoConstants.EdgeSnapGridDegrees;
            double lon1 = edge.Lon1 * GeoConstants.EdgeSnapGridDegrees;
            double lat2 = edge.Lat2 * GeoConstants.EdgeSnapGridDegrees;
            double lon2 = edge.Lon2 * GeoConstants.EdgeSnapGridDegrees;

            var cell1 = CellKey(lat1, lon1);
            var cell2 = CellKey(lat2, lon2);

            GetOrCreateCell(cell1).Add(edge);
            if (cell1 != cell2)
                GetOrCreateCell(cell2).Add(edge);

            _edgeCount++;
        }

        /// <summary>
        /// Counts edges with at least one endpoint within radiusM of center.
        /// Same semantics as CountEdgesInRadius but only checks nearby grid cells.
        /// </summary>
        public int CountInRadius(Coordinate center, double radiusM)
        {
            if (_grid.Count == 0) return 0;

            double radiusDeg = radiusM / GeoConstants.MetersPerDegreeLat;
            double cosLat = Math.Cos(center.Lat * Math.PI / 180);
            double radiusLonDeg = radiusDeg / Math.Max(cosLat, GeoConstants.MinCosLat);

            int centerCLat = (int)Math.Floor(center.Lat * InvCellSize);
            int centerCLon = (int)Math.Floor(center.Lon * InvCellSize);
            int cellRadius = (int)Math.Ceiling(Math.Max(radiusDeg, radiusLonDeg) * InvCellSize) + 1;

            int count = 0;
            for (int dLat = -cellRadius; dLat <= cellRadius; dLat++)
            {
                for (int dLon = -cellRadius; dLon <= cellRadius; dLon++)
                {
                    var key = (centerCLat + dLat, centerCLon + dLon);
                    if (!_grid.TryGetValue(key, out var cell)) continue;

                    foreach (var edge in cell)
                    {
                        double lat1 = edge.Lat1 * GeoConstants.EdgeSnapGridDegrees;
                        double lon1 = edge.Lon1 * GeoConstants.EdgeSnapGridDegrees;
                        double lat2 = edge.Lat2 * GeoConstants.EdgeSnapGridDegrees;
                        double lon2 = edge.Lon2 * GeoConstants.EdgeSnapGridDegrees;

                        double dLat1 = Math.Abs(lat1 - center.Lat);
                        double dLon1 = Math.Abs(lon1 - center.Lon);
                        double dLat2 = Math.Abs(lat2 - center.Lat);
                        double dLon2 = Math.Abs(lon2 - center.Lon);

                        if ((dLat1 < radiusDeg && dLon1 < radiusLonDeg) ||
                            (dLat2 < radiusDeg && dLon2 < radiusLonDeg))
                        {
                            count++;
                            if (count > 50) return count;
                        }
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// Returns true if any edge endpoint is within radiusM of center.
        /// Early-returns on first hit — use for proximity checks, not counting.
        /// </summary>
        public bool AnyEdgeWithin(Coordinate point, double radiusM)
        {
            if (_grid.Count == 0) return false;

            double radiusDeg = radiusM / GeoConstants.MetersPerDegreeLat;
            double cosLat = Math.Cos(point.Lat * Math.PI / 180);
            double radiusLonDeg = radiusDeg / Math.Max(cosLat, GeoConstants.MinCosLat);

            int centerCLat = (int)Math.Floor(point.Lat * InvCellSize);
            int centerCLon = (int)Math.Floor(point.Lon * InvCellSize);
            int cellRadius = (int)Math.Ceiling(Math.Max(radiusDeg, radiusLonDeg) * InvCellSize) + 1;

            for (int dLat = -cellRadius; dLat <= cellRadius; dLat++)
            {
                for (int dLon = -cellRadius; dLon <= cellRadius; dLon++)
                {
                    var key = (centerCLat + dLat, centerCLon + dLon);
                    if (!_grid.TryGetValue(key, out var cell)) continue;

                    foreach (var edge in cell)
                    {
                        double lat1 = edge.Lat1 * GeoConstants.EdgeSnapGridDegrees;
                        double lon1 = edge.Lon1 * GeoConstants.EdgeSnapGridDegrees;
                        double lat2 = edge.Lat2 * GeoConstants.EdgeSnapGridDegrees;
                        double lon2 = edge.Lon2 * GeoConstants.EdgeSnapGridDegrees;

                        if (DistancePointToSegment(point, lat1, lon1, lat2, lon2) < radiusM)
                            return true;
                    }
                }
            }
            return false;
        }

        private static (int, int) CellKey(double lat, double lon)
        {
            return ((int)Math.Floor(lat * InvCellSize), (int)Math.Floor(lon * InvCellSize));
        }

        private List<EdgeKey> GetOrCreateCell((int, int) key)
        {
            if (!_grid.TryGetValue(key, out var cell))
            {
                cell = new List<EdgeKey>();
                _grid[key] = cell;
            }
            return cell;
        }
    }

}
