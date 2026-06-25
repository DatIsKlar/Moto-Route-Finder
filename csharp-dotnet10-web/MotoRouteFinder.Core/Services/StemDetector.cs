using System;
using System.Collections.Generic;
using Itinero;
using Itinero.Profiles;
using MotoRouteFinder.Helpers;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Services;

/// <summary>
/// Static methods for detecting stems (backtracking geometry) in route segments
/// and analyzing their root cause.
/// </summary>
public static class StemDetector
{
    // Reusable buffers for IsStemSegment to avoid per-call allocations.
    // Each thread gets its own set via [ThreadStatic].
    [ThreadStatic] private static List<Coordinate>? _tFirstHalf;
    [ThreadStatic] private static List<Coordinate>? _tSecondHalf;
    [ThreadStatic] private static List<int>? _tSecondHalfOrigIdx;
    [ThreadStatic] private static Dictionary<(int, int), List<int>>? _tBucket;

    private static List<Coordinate> GetFirstHalf() => _tFirstHalf ??= new List<Coordinate>(64);
    private static List<Coordinate> GetSecondHalf() => _tSecondHalf ??= new List<Coordinate>(64);
    private static List<int> GetSecondHalfOrigIdx() => _tSecondHalfOrigIdx ??= new List<int>(64);
    private static Dictionary<(int, int), List<int>> GetBucket() => _tBucket ??= new Dictionary<(int, int), List<int>>(64);

    public static bool IsStemSegment(List<Coordinate> segment, bool strict = false)
        => IsStemSegment(segment, strict, out _, out _, out _, out _, out _, out _);

    public static bool IsStemSegment(List<Coordinate> segment, int start, int count)
        => IsStemSegment(segment, start, count, false, out _, out _, out _, out _, out _, out _);

    public static bool IsStemSegment(List<Coordinate> segment, int start, int count, bool strict,
        out int closeCount, out int opposedCount, out int examined,
        out int firstHalfPoints, out int secondHalfPoints,
        out double nearestNearMissM)
    {
        closeCount = 0;
        opposedCount = 0;
        examined = 0;
        firstHalfPoints = 0;
        secondHalfPoints = 0;
        nearestNearMissM = double.MaxValue;

        if (count < 8) return false;

        double totalDist = 0;
        for (int i = start; i < start + count - 1; i++)
            totalDist += RouteGeometryUtils.HaversineDistance(segment[i], segment[i + 1]);
        if (totalDist < 300) return false;

        double halfDist = totalDist / 2;
        double cumulative = 0;

        var firstHalf = GetFirstHalf();
        var secondHalf = GetSecondHalf();
        var secondHalfOrigIdx = GetSecondHalfOrigIdx();
        firstHalf.Clear();
        secondHalf.Clear();
        secondHalfOrigIdx.Clear();

        for (int i = start; i < start + count - 1; i++)
        {
            double d = RouteGeometryUtils.HaversineDistance(segment[i], segment[i + 1]);
            cumulative += d;
            if (cumulative < halfDist)
            {
                firstHalf.Add(segment[i]);
            }
            else
            {
                secondHalf.Add(segment[i]);
                secondHalfOrigIdx.Add(i);
            }
        }

        firstHalfPoints = firstHalf.Count;
        secondHalfPoints = secondHalf.Count;

        if (firstHalf.Count < 3 || secondHalf.Count < 3) return false;

        const double closeThreshold = 300;
        const double opposedThreshold = 500;

        const double bucketDeg = 0.005;
        var bucket = GetBucket();
        bucket.Clear();
        for (int j = 0; j < firstHalf.Count; j++)
        {
            var k = ((int)(firstHalf[j].Lat / bucketDeg), (int)(firstHalf[j].Lon / bucketDeg));
            if (!bucket.TryGetValue(k, out var list))
            {
                list = new List<int>(4);
                bucket[k] = list;
            }
            list.Add(j);
        }

        for (int i = 0; i < secondHalf.Count; i++)
        {
            int nearest = 0;
            double nearestDist = double.MaxValue;

            int bLat = (int)(secondHalf[i].Lat / bucketDeg);
            int bLon = (int)(secondHalf[i].Lon / bucketDeg);
            bool foundInBucket = false;
            for (int dLat = -1; dLat <= 1 && !foundInBucket; dLat++)
                for (int dLon = -1; dLon <= 1 && !foundInBucket; dLon++)
                    if (bucket.ContainsKey((bLat + dLat, bLon + dLon))) foundInBucket = true;

            if (foundInBucket)
            {
                for (int dLat = -1; dLat <= 1; dLat++)
                    for (int dLon = -1; dLon <= 1; dLon++)
                    {
                        if (!bucket.TryGetValue((bLat + dLat, bLon + dLon), out var candidates)) continue;
                        foreach (int j in candidates)
                        {
                            double d = RouteGeometryUtils.HaversineDistance(secondHalf[i], firstHalf[j]);
                            if (d < nearestDist) { nearestDist = d; nearest = j; }
                        }
                    }
            }
            else
            {
                for (int j = 0; j < firstHalf.Count; j++)
                {
                    double d = RouteGeometryUtils.HaversineDistance(secondHalf[i], firstHalf[j]);
                    if (d < nearestDist) { nearestDist = d; nearest = j; }
                }
            }

            examined++;

            if (nearestDist < nearestNearMissM)
                nearestNearMissM = nearestDist;

            if (nearestDist < closeThreshold)
                closeCount++;

            if (nearestDist < opposedThreshold)
            {
                int origIdx = secondHalfOrigIdx[i];
                double bearing2 = RouteGeometryUtils.ComputeBearing(segment[origIdx], segment[Math.Min(origIdx + 1, start + count - 2)]);
                double bearing1 = RouteGeometryUtils.ComputeBearing(segment[start + nearest], segment[Math.Min(start + nearest + 1, start + count - 2)]);
                double diff = Math.Abs(bearing2 - bearing1);
                if (diff > 180) diff = 360 - diff;
                if (Math.Abs(diff - 180) < 30)
                    opposedCount++;
            }
        }

        double minCloseRatio = strict ? 0.30 : 0.25;
        double minOpposedRatio = strict ? 0.12 : 0.08;
        return examined > 0 && (double)closeCount / examined > minCloseRatio && (double)opposedCount / examined > minOpposedRatio;
    }

    public static bool IsStemSegment(List<Coordinate> segment, bool strict,
        out int closeCount, out int opposedCount, out int examined,
        out int firstHalfPoints, out int secondHalfPoints,
        out double nearestNearMissM)
    {
        // Optimization: Delegate to the full overload with start=0, count=segment.Count
        // This eliminates code duplication and reduces maintenance risk
        return IsStemSegment(segment, 0, segment.Count, strict, out closeCount, out opposedCount, out examined, out firstHalfPoints, out secondHalfPoints, out nearestNearMissM);
    }

    public static (StemRootCause cause, double startBearing, double endBearing, double bearingDelta, int connectivity, double maxDeviation, double straightLineRatio) AnalyzeStemRootCause(List<Coordinate> segment, Coordinate candidate, Router router, RouterDb routerDb, IProfileInstance profile)
    {
        if (segment.Count < 4) return (StemRootCause.Unknown, 0, 0, 0, 0, 0, 0);

        double startBearing = RouteGeometryUtils.ComputeBearing(segment[0], segment[1]);
        double endBearing = RouteGeometryUtils.ComputeBearing(segment[^2], segment[^1]);
        double bearingDelta = Math.Abs(startBearing - endBearing);
        if (bearingDelta > 180) bearingDelta = 360 - bearingDelta;

        double totalDist = RouteGeometryUtils.CalculateDistance(segment);
        double straightDist = RouteGeometryUtils.HaversineDistance(segment[0], segment[^1]);
        double maxDeviation = 0;
        double dx = segment[^1].Lon - segment[0].Lon;
        double dy = segment[^1].Lat - segment[0].Lat;
        double lineLenSq = dx * dx + dy * dy;
        for (int i = 1; i < segment.Count - 1; i++)
        {
            double px = segment[i].Lon - segment[0].Lon;
            double py = segment[i].Lat - segment[0].Lat;
            double t = Math.Max(0, Math.Min(1, (px * dx + py * dy) / lineLenSq));
            double projLon = segment[0].Lon + t * dx;
            double projLat = segment[0].Lat + t * dy;
            double dist = RouteGeometryUtils.HaversineDistance(segment[i], new Coordinate(projLat, projLon));
            if (dist > maxDeviation) maxDeviation = dist;
        }

        int connectivity = 0;
        var resolved = router.TryResolve(profile, (float)candidate.Lat, (float)candidate.Lon, 2000);
        if (!resolved.IsError)
        {
            double[] probeAngles = { 0, 45, 90, 135, 180, 225, 270, 315 };
            foreach (double angle in probeAngles)
            {
                double probeLat = candidate.Lat + (500 / GeoConstants.MetersPerDegreeLat) * Math.Sin(angle * Math.PI / 180);
                double probeLon = candidate.Lon + (500 / (GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(candidate.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(angle * Math.PI / 180);
                var probeResult = router.TryResolve(profile, (float)probeLat, (float)probeLon, 2000);
                if (!probeResult.IsError) connectivity++;
            }
        }

        StemRootCause cause;
        if (bearingDelta > 140 && totalDist > straightDist * 1.5)
            cause = StemRootCause.OvershootBacktrack;
        else if (IsPrivateOrRestricted(router, routerDb, profile, candidate) || IsPrivateOrRestricted(router, routerDb, profile, segment[segment.Count / 2]))
            cause = StemRootCause.PrivateRoad;
        else if (connectivity <= 1)
            cause = StemRootCause.DeadEndRoad;
        else if (bearingDelta > 100 && totalDist > 3000 && maxDeviation < totalDist * 0.4)
            cause = StemRootCause.OneWayStreet;
        else if (maxDeviation > totalDist * 0.5 && totalDist > straightDist * 3)
            cause = StemRootCause.TerrainDetour;
        else if (connectivity <= 3 && totalDist > 5000)
            cause = StemRootCause.NoDirectRoad;
        else if (totalDist > straightDist * 2.0 && maxDeviation < totalDist * 0.3)
            cause = StemRootCause.OvershootBacktrack;
        else if (connectivity <= 5 && totalDist > 3000)
            cause = StemRootCause.NoDirectRoad;
        else
            cause = StemRootCause.Unknown;

        return (cause, startBearing, endBearing, bearingDelta, connectivity, maxDeviation, straightDist > 0 ? totalDist / straightDist : 0);
    }

    public static bool IsPrivateOrRestricted(Router router, RouterDb routerDb, IProfileInstance profile, Coordinate point)
    {
        var result = router.TryResolve(profile, (float)point.Lat, (float)point.Lon, 500);
        if (result.IsError) return false;

        var edge = routerDb.Network.GetEdge(result.Value.EdgeId);
        var tags = routerDb.EdgeProfiles.Get(edge.Data.Profile);
        if (tags == null) return false;

        if (tags.TryGetValue("access", out string access))
        {
            access = access.ToLowerInvariant();
            if (access is "private" or "no" or "customers" or "military" or "restricted")
                return true;
        }

        if (tags.TryGetValue("motor_vehicle", out string motorVehicle) && motorVehicle == "no")
            return true;

        if (tags.TryGetValue("highway", out string highway) && highway == "service")
        {
            if (tags.TryGetValue("service", out string serviceType))
            {
                serviceType = serviceType.ToLowerInvariant();
                if (serviceType is "driveway" or "private" or "parking_aisle")
                    return true;
            }
        }

        return false;
    }
}
