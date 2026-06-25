using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Itinero;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Helpers;

public static class RouteGeometryUtils
{
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
            Lat1 = (int)Math.Round(a.Lat / GeoConstants.EdgeSnapGridDegrees);
            Lon1 = (int)Math.Round(a.Lon / GeoConstants.EdgeSnapGridDegrees);
            Lat2 = (int)Math.Round(b.Lat / GeoConstants.EdgeSnapGridDegrees);
            Lon2 = (int)Math.Round(b.Lon / GeoConstants.EdgeSnapGridDegrees);
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
                double lat = segment[i].Lat + (segment[i + 1].Lat - segment[i].Lat) * ratio;
                double lon = segment[i].Lon + (segment[i + 1].Lon - segment[i].Lon) * ratio;
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
        double minDist = double.MaxValue;
        int searchStart = Math.Max(1, geometry.Count / 4);
        int searchEnd = Math.Min(geometry.Count - 2, geometry.Count * 3 / 4);

        for (int i = searchStart; i <= searchEnd; i++)
        {
            double dist = HaversineDistance(geometry[i], start);
            if (dist < minDist)
            {
                minDist = dist;
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
                double lat = geometry[i - 1].Lat + t * (geometry[i].Lat - geometry[i - 1].Lat);
                double lon = geometry[i - 1].Lon + t * (geometry[i].Lon - geometry[i - 1].Lon);
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
            if (usedEdges.Contains(key) || usedEdges.Contains(key.Reversed()))
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

            if (usedEdges.Contains(key) || usedEdges.Contains(key.Reversed()))
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

            if (usedEdges.Contains(key) || usedEdges.Contains(key.Reversed()))
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
            if (edgeAge.TryGetValue(key.Reversed(), out int revAge))
                age = Math.Min(age, revAge);

            if (age < int.MaxValue)
            {
                double weight = age <= 1 ? 3.0 : 1.0;
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
            if (edgeBirthSegment.TryGetValue(key.Reversed(), out int birthRev))
                age = Math.Min(age, currentSegment - birthRev);

            if (age < int.MaxValue)
            {
                double weight = age <= 1 ? 3.0 : 1.0;
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
        double forwardSpreadExHome = CalculateBearingSpreadExcludingDistance(forwardPath, 1000, true);
        double returnSpreadExHome = CalculateBearingSpreadExcludingDistance(returnPath, 1000, false);

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
        double efficiency = haversineDistance > 0 ? routedDistance / haversineDistance : 1.0;

        // Calculate path compactness (bbox area / path length)
        double forwardCompactness = CalculatePathCompactness(forwardPath);
        double returnCompactness = CalculatePathCompactness(returnPath);

        // Calculate bearing statistics
        var allBearings = new List<double>();
        for (int i = 0; i < geometry.Count - 1; i++)
            allBearings.Add(ComputeBearing(geometry[i], geometry[i + 1]));
        double avgBearing = allBearings.Count > 0 ? allBearings.Average() : 0;
        double bearingVariance = allBearings.Count > 1 ? allBearings.Average(b => Math.Pow(b - avgBearing, 2)) : 0;

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
    /// Calculates the spread of bearings in a path.
    /// Samples every ~500m to avoid low spread from consecutive coordinates on the same road.
    /// Computes bearing from the previous sample point to the current sample point (500m apart).
    /// Higher values mean the path covers more directions (more circular).
    /// </summary>
    public static double CalculateBearingSpread(List<Coordinate> path)
    {
        if (path.Count < 2) return 0;

        // Sample bearings every ~500m, computing bearing between sample points (not consecutive coords)
        var bearings = new List<double>();
        double accumulatedDist = 0;
        double sampleInterval = 500; // meters
        int lastSampleIndex = 0;

        for (int i = 1; i < path.Count; i++)
        {
            accumulatedDist += HaversineDistance(path[i - 1], path[i]);
            if (accumulatedDist >= sampleInterval)
            {
                bearings.Add(ComputeBearing(path[lastSampleIndex], path[i]));
                lastSampleIndex = i;
                accumulatedDist = 0;
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

        // Project point onto line and find perpendicular distance
        double lat1 = lineStart.Lat * Math.PI / 180;
        double lon1 = lineStart.Lon * Math.PI / 180;
        double lat2 = lineEnd.Lat * Math.PI / 180;
        double lon2 = lineEnd.Lon * Math.PI / 180;
        double lat3 = point.Lat * Math.PI / 180;
        double lon3 = point.Lon * Math.PI / 180;

        // Vector from lineStart to lineEnd
        double dx = (lon2 - lon1) * Math.Cos((lat1 + lat2) / 2);
        double dy = lat2 - lat1;

        // Vector from lineStart to point
        double px = (lon3 - lon1) * Math.Cos((lat1 + lat3) / 2);
        double py = lat3 - lat1;

        // Project point onto line
        double t = (px * dx + py * dy) / (dx * dx + dy * dy);
        t = Math.Max(0, Math.Min(1, t)); // Clamp to line segment

        // Closest point on line
        double closestLat = lat1 + t * dy;
        double closestLon = lon1 + t * dx;

        // Distance from point to closest point on line
        double distLat = (lat3 - closestLat) * GeoConstants.MetersPerDegreeLat;
        double distLon = (lon3 - closestLon) * GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(closestLat), GeoConstants.MinCosLat);

        return Math.Sqrt(distLat * distLat + distLon * distLon);
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
            if (delta > 30) windingCount++;
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

    public static int CountEdgesInRadius(Coordinate center, double radiusM, HashSet<EdgeKey> edges)
    {
        int count = 0;
        double radiusDeg = radiusM / 111320.0; // approx meters per degree at equator
        double cosLat = Math.Cos(center.Lat * Math.PI / 180);
        double radiusLonDeg = radiusDeg / Math.Max(cosLat, 0.01);

        foreach (var edge in edges)
        {
            // Convert edge key back to approximate coordinates
            double lat1 = edge.Lat1 * GeoConstants.EdgeSnapGridDegrees;
            double lon1 = edge.Lon1 * GeoConstants.EdgeSnapGridDegrees;
            double lat2 = edge.Lat2 * GeoConstants.EdgeSnapGridDegrees;
            double lon2 = edge.Lon2 * GeoConstants.EdgeSnapGridDegrees;

            // Check if either endpoint is within radius
            double dLat1 = Math.Abs(lat1 - center.Lat);
            double dLon1 = Math.Abs(lon1 - center.Lon);
            double dLat2 = Math.Abs(lat2 - center.Lat);
            double dLon2 = Math.Abs(lon2 - center.Lon);

            if ((dLat1 < radiusDeg && dLon1 < radiusLonDeg) ||
                (dLat2 < radiusDeg && dLon2 < radiusLonDeg))
            {
                count++;
                if (count > 50) return count; // early exit for performance
            }
        }
        return count;
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

    public static List<(Coordinate From, Coordinate To)> ExtractRepetitionSegments(List<Coordinate> fullRoute)
    {
        var segments = new List<(Coordinate, Coordinate)>();
        var seenEdges = new Dictionary<EdgeKey, Coordinate>();

        for (int i = 0; i < fullRoute.Count - 1; i++)
        {
            var key = new EdgeKey(fullRoute[i], fullRoute[i + 1]);
            var revKey = key.Reversed();

            if (seenEdges.ContainsKey(key) || seenEdges.ContainsKey(revKey))
                segments.Add((fullRoute[i], fullRoute[i + 1]));
            else
                seenEdges[key] = fullRoute[i];
        }

        return segments;
    }

    /// <summary>
    /// Extracts repetition segments including both exact edge duplicates AND out-and-back overlap.
    /// The outAndBackIndex splits the route into forward/return paths and detects edges
    /// that appear in both directions.
    /// </summary>
    public static List<(Coordinate From, Coordinate To)> ExtractRepetitionSegments(List<Coordinate> fullRoute, int outAndBackIndex)
    {
        var segments = new List<(Coordinate, Coordinate)>();

        // Pass 1: Exact edge duplicates (same as original method)
        var seenEdges = new Dictionary<EdgeKey, Coordinate>();
        for (int i = 0; i < fullRoute.Count - 1; i++)
        {
            var key = new EdgeKey(fullRoute[i], fullRoute[i + 1]);
            var revKey = key.Reversed();

            if (seenEdges.ContainsKey(key) || seenEdges.ContainsKey(revKey))
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
                outboundEdges.Add(key.Reversed());
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
            double maxDistM = 25.0;
            double maxBearingDelta = 30.0;

            // Build grid index of forward-path edges
            const double gridDeg = 0.0005;
            var forwardGrid = new Dictionary<(int, int), List<(int index, double bearing)>>();
            for (int i = 0; i < outAndBackIndex - 1; i++)
            {
                double midLat = (fullRoute[i].Lat + fullRoute[i + 1].Lat) / 2;
                double midLon = (fullRoute[i].Lon + fullRoute[i + 1].Lon) / 2;
                double bearing = ComputeBearing(fullRoute[i], fullRoute[i + 1]);
                int cellLat = (int)Math.Floor(midLat / gridDeg);
                int cellLon = (int)Math.Floor(midLon / gridDeg);
                var cellKey = (cellLat, cellLon);
                if (!forwardGrid.TryGetValue(cellKey, out var list))
                {
                    list = new List<(int, double)>();
                    forwardGrid[cellKey] = list;
                }
                list.Add((i, bearing));
            }

            // Check each return-path edge against nearby forward-path edges
            for (int i = outAndBackIndex; i < fullRoute.Count - 1; i++)
            {
                double retMidLat = (fullRoute[i].Lat + fullRoute[i + 1].Lat) / 2;
                double retMidLon = (fullRoute[i].Lon + fullRoute[i + 1].Lon) / 2;
                double retBearing = ComputeBearing(fullRoute[i], fullRoute[i + 1]);
                int retCellLat = (int)Math.Floor(retMidLat / gridDeg);
                int retCellLon = (int)Math.Floor(retMidLon / gridDeg);

                // Check nearby cells (±1 in each direction)
                for (int dLat = -1; dLat <= 1; dLat++)
                {
                    for (int dLon = -1; dLon <= 1; dLon++)
                    {
                        var cellKey = (retCellLat + dLat, retCellLon + dLon);
                        if (!forwardGrid.TryGetValue(cellKey, out var fwdEdges)) continue;

                        foreach (var (fwdIdx, fwdBearing) in fwdEdges)
                        {
                            double fwdMidLat = (fullRoute[fwdIdx].Lat + fullRoute[fwdIdx + 1].Lat) / 2;
                            double fwdMidLon = (fullRoute[fwdIdx].Lon + fullRoute[fwdIdx + 1].Lon) / 2;
                            double dist = HaversineDistance(
                                new Coordinate(retMidLat, retMidLon),
                                new Coordinate(fwdMidLat, fwdMidLon));

                            if (dist > maxDistM) continue;

                            double bearingDelta = Math.Abs(retBearing - fwdBearing);
                            if (bearingDelta > 180) bearingDelta = 360 - bearingDelta;
                            if (bearingDelta > maxBearingDelta) continue;

                            // Parallel edge found — add return segment to repetition list
                            segments.Add((fullRoute[i], fullRoute[i + 1]));
                            break; // Already counted this return edge
                        }
                    }
                }
            }
        }

        return segments;
    }

    public static double DetectOutAndBackOverlap(List<Coordinate> fullRoute, int returnToStartIndex)
    {
        if (returnToStartIndex <= 0 || returnToStartIndex >= fullRoute.Count)
            return 0;

        var outbound = fullRoute.Take(returnToStartIndex).ToList();
        var returnPath = fullRoute.Skip(returnToStartIndex).ToList();

        if (outbound.Count < 2 || returnPath.Count < 2)
            return 0;

        var outboundEdges = new Dictionary<EdgeKey, double>();
        for (int i = 0; i < outbound.Count - 1; i++)
        {
            var key = new EdgeKey(outbound[i], outbound[i + 1]);
            var revKey = key.Reversed();
            double dist = HaversineDistance(outbound[i], outbound[i + 1]);
            outboundEdges[key] = dist;
            outboundEdges[revKey] = dist;
        }

        double overlapDist = 0;
        for (int i = 0; i < returnPath.Count - 1; i++)
        {
            var key = new EdgeKey(returnPath[i], returnPath[i + 1]);
            double dist = HaversineDistance(returnPath[i], returnPath[i + 1]);

            if (outboundEdges.ContainsKey(key))
                overlapDist += dist;
        }

        return overlapDist;
    }

    /// <summary>
    /// Calculates the distance from a point to the nearest used edge.
    /// Used for edge avoidance in waypoint generation.
    /// </summary>
    public static double DistanceToNearestUsedEdge(Coordinate point, HashSet<EdgeKey> edges)
    {
        if (edges.Count == 0) return double.MaxValue;

        double minDist = double.MaxValue;

        foreach (var edge in edges)
        {
            double lat1 = edge.Lat1 * GeoConstants.EdgeSnapGridDegrees;
            double lon1 = edge.Lon1 * GeoConstants.EdgeSnapGridDegrees;
            double lat2 = edge.Lat2 * GeoConstants.EdgeSnapGridDegrees;
            double lon2 = edge.Lon2 * GeoConstants.EdgeSnapGridDegrees;

            double dist = DistancePointToSegment(point, lat1, lon1, lat2, lon2);
            if (dist < minDist) minDist = dist;
        }

        return minDist;
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
                if (delta > 90) sharp++;
                if (delta > 150) hairpins++;
            }

            prevBearing = currBearing;
        }

        if (totalMeters > 0) straightMeters += HaversineDistance(points[points.Count - 2], points[points.Count - 1]);
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
            var revKey = key.Reversed();
            if (forwardEdges.Contains(key) || forwardEdges.Contains(revKey))
                overlapM += dist;
        }

        double overlapPct = totalM > 0 ? overlapM / totalM : 0;
        return (Math.Round(curvature, 4), Math.Round(overlapPct, 3));
    }

    /// <summary>
    /// Calculates a circularity score (0-100) for a route geometry.
    /// Higher values indicate more circular routes (covers more compass directions).
    /// Components: bearing spread (50%), sector coverage (30%), compactness (20%).
    /// </summary>
    public static double CalculateCircularityScore(List<Coordinate> geometry)
    {
        if (geometry.Count < 3) return 0;

        // Bearing spread component (50% weight)
        double spread = CalculateBearingSpread(geometry);
        double spreadScore = Math.Min(100, spread / 180.0 * 100);

        // Sector coverage component (30% weight) — how many 30° sectors are visited
        int distinctSectors = CalculateDistinctBearingCount(geometry);
        double sectorScore = Math.Min(100, distinctSectors / 12.0 * 100);

        // Compactness component (20% weight) — convex hull area vs bounding circle
        // This better captures true circularity than bbox aspect ratio
        double sumLat = 0, sumLon = 0;
        for (int i = 0; i < geometry.Count; i++)
        {
            sumLat += geometry[i].Lat;
            sumLon += geometry[i].Lon;
        }
        double centroidLat = sumLat / geometry.Count;
        double centroidLon = sumLon / geometry.Count;
        double sumRadius = 0;
        for (int i = 0; i < geometry.Count; i++)
            sumRadius += HaversineDistance(new Coordinate(centroidLat, centroidLon), geometry[i]);
        double avgRadius = sumRadius / geometry.Count;
        double boundingCircleArea = Math.PI * avgRadius * avgRadius;

        // Compute convex hull area using shoelace formula
        var hull = ComputeConvexHull(geometry);
        double hullArea = ComputePolygonArea(hull, centroidLat);

        // Fill ratio: how much of the bounding circle the convex hull covers
        // A perfect circle would be ~1.0, a thin line would be ~0.0
        double compactnessScore = boundingCircleArea > 0
            ? Math.Min(100, (hullArea / boundingCircleArea) * 100)
            : 0;

        return spreadScore * 0.5 + sectorScore * 0.3 + compactnessScore * 0.2;
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

    /// <summary>
    /// Grid-based spatial index for fast radius queries and nearest-edge lookups.
    /// Partitions edges into grid cells so CountEdgesInRadius and DistanceToNearestUsedEdge
    /// only check edges in nearby cells instead of the full set.
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

        public void AddRange(HashSet<EdgeKey> edges)
        {
            foreach (var edge in edges)
                Add(edge);
        }

        /// <summary>
        /// Counts edges with at least one endpoint within radiusM of center.
        /// Same semantics as CountEdgesInRadius but only checks nearby grid cells.
        /// </summary>
        public int CountInRadius(Coordinate center, double radiusM)
        {
            if (_grid.Count == 0) return 0;

            double radiusDeg = radiusM / 111320.0;
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
        /// Finds the minimum distance from a point to any edge in the index.
        /// Uses expanding ring search for early termination.
        /// </summary>
        public double DistanceToNearest(Coordinate point)
        {
            if (_grid.Count == 0) return double.MaxValue;

            int centerCLat = (int)Math.Floor(point.Lat * InvCellSize);
            int centerCLon = (int)Math.Floor(point.Lon * InvCellSize);

            double minDist = double.MaxValue;
            // Expand outward up to 3 cells (~15km) — covers the 300m homing threshold easily
            for (int ring = 0; ring <= 3; ring++)
            {
                double ringMinPossibleDist = ring > 0 ? (ring - 1) * CellSizeDegrees * GeoConstants.MetersPerDegreeLat * 0.5 : 0;

                for (int dLat = -ring; dLat <= ring; dLat++)
                {
                    for (int dLon = -ring; dLon <= ring; dLon++)
                    {
                        // Only check cells on the ring boundary (not interior)
                        if (ring > 0 && Math.Abs(dLat) < ring && Math.Abs(dLon) < ring)
                            continue;

                        var key = (centerCLat + dLat, centerCLon + dLon);
                        if (!_grid.TryGetValue(key, out var cell)) continue;

                        foreach (var edge in cell)
                        {
                            double lat1 = edge.Lat1 * GeoConstants.EdgeSnapGridDegrees;
                            double lon1 = edge.Lon1 * GeoConstants.EdgeSnapGridDegrees;
                            double lat2 = edge.Lat2 * GeoConstants.EdgeSnapGridDegrees;
                            double lon2 = edge.Lon2 * GeoConstants.EdgeSnapGridDegrees;

                            double dist = DistancePointToSegment(point, lat1, lon1, lat2, lon2);
                            if (dist < minDist) minDist = dist;
                        }
                    }
                }

                // Early exit: if the minimum possible distance in the next ring exceeds current best, stop
                if (ring > 0 && minDist < double.MaxValue)
                {
                    double nextRingMinDist = ring * CellSizeDegrees * GeoConstants.MetersPerDegreeLat * 0.5;
                    if (nextRingMinDist > minDist)
                        break;
                }
            }
            return minDist;
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
