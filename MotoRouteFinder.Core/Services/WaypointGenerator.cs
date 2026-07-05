using System;
using System.Collections.Generic;
using Itinero.Profiles;
using MotoRouteFinder.Helpers;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Services;

/// <summary>
/// Generates waypoint candidates at specified angles and manages sector blocking.
/// </summary>
public class WaypointGenerator
{
    private readonly RoadClassifier _roadClassifier;
    private readonly RouteGeometryUtils.EdgeSpatialIndex _edgeSpatialIndex;
    private readonly RouteGenerationOptions _options;
    private const int SectorCount = 18;
    private const double SectorSizeDegrees = 20;
    private readonly int[] _blockedSectorCounts = new int[SectorCount];

    private const int MaxWaypointAttempts = 8;
    private const double AngleJitterRange = 0.5;
    private const double WaypointDistanceScaleMin = 0.7;
    private const double WaypointDistanceScaleRange = 0.6;
    private const int MaxHomingAttempts = 5;
    private const double HomingDistanceScaleMin = 0.8;
    private const double HomingDistanceScaleRange = 0.4;
    private const double MinEdgeDistanceForHomingM = 300;
    private const double ResolveRadiusM = 3000;

    public int[] BlockedSectorCounts => _blockedSectorCounts;

    public WaypointGenerator(RoadClassifier roadClassifier, RouteGeometryUtils.EdgeSpatialIndex edgeSpatialIndex, RouteGenerationOptions options)
    {
        _roadClassifier = roadClassifier;
        _edgeSpatialIndex = edgeSpatialIndex;
        _options = options;
    }

    public void ResetCounters()
    {
        Array.Clear(_blockedSectorCounts, 0, _blockedSectorCounts.Length);
    }

    private static int GetSector(double angleRadians)
    {
        double degrees = (angleRadians * 180 / Math.PI + 360) % 360;
        return (int)(degrees / SectorSizeDegrees); // 18 sectors of 20° each
    }

    public static bool IsSectorBlocked(double angleRadians, HashSet<int> blocked)
    {
        return blocked.Contains(GetSector(angleRadians));
    }

    public void BlockSector(double angleRadians, HashSet<int> blocked)
    {
        int sector = GetSector(angleRadians);
        blocked.Add(sector);
        _blockedSectorCounts[sector]++;
    }

    public Coordinate? GenerateWaypointAtAngle(
        IProfileInstance profile,
        Coordinate center,
        double radiusKm,
        double targetAngle,
        double cosLat,
        bool avoidHighways)
    {
        for (int attempt = 0; attempt < MaxWaypointAttempts; attempt++)
        {
            double angleJitter = (Random.Shared.NextDouble() - 0.5) * AngleJitterRange;
            double angle = targetAngle + angleJitter;
            double distanceScale = WaypointDistanceScaleMin + Random.Shared.NextDouble() * WaypointDistanceScaleRange;
            double r = radiusKm * distanceScale;

            double wpLat = center.Lat + (r / GeoConstants.KmPerDegreeLat) * Math.Sin(angle);
            double wpLon = center.Lon + (r / (GeoConstants.KmPerDegreeLat * Math.Max(cosLat, GeoConstants.MinCosLat))) * Math.Cos(angle);

            var resolved = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(wpLat, wpLon), (float)ResolveRadiusM);
            if (resolved != null)
            {
                var quality = _roadClassifier.ClassifyRoad(resolved, profile, avoidHighways);
                if (quality == RoadClassifier.RoadQuality.Preferred || quality == RoadClassifier.RoadQuality.Acceptable)
                {
                    int density = _edgeSpatialIndex.CountInRadius(resolved, _options.EdgeDensityCheckRadiusM);
                    if (density <= _options.EdgeDensityThreshold)
                        return resolved;
                    // Dense — try next jitter attempt instead
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Generates a homing waypoint specifically for return path planning.
    /// Targets roads leading back toward start while avoiding used edges.
    /// </summary>
    public Coordinate? GenerateHomingWaypoint(
        IProfileInstance profile,
        Coordinate currentPos,
        Coordinate start,
        double distanceKm,
        double bearingToStart,
        double angleOffset,
        bool avoidHighways,
        double perpOffset = 0,
        double perpAngle = 0)
    {
        // M1: Convert compass bearing to math angle, then add radian offset
        double angle = RouteGeometryUtils.BearingToMathAngle(bearingToStart) + angleOffset;
        double cosLat = Math.Cos(currentPos.Lat * Math.PI / 180);

        for (int attempt = 0; attempt < MaxHomingAttempts; attempt++)
        {
            double distanceScale = HomingDistanceScaleMin + Random.Shared.NextDouble() * HomingDistanceScaleRange;
            double r = distanceKm * distanceScale;

            double wpLat = currentPos.Lat + (r / GeoConstants.KmPerDegreeLat) * Math.Sin(angle);
            double wpLon = currentPos.Lon + (r / (GeoConstants.KmPerDegreeLat * Math.Max(cosLat, GeoConstants.MinCosLat))) * Math.Cos(angle);

            if (perpOffset != 0)
            {
                // M1: perpAngle is already a math angle in radians from caller
                wpLat += (perpOffset / GeoConstants.MetersPerDegreeLat) * Math.Sin(perpAngle);
                wpLon += (perpOffset / (GeoConstants.MetersPerDegreeLat * Math.Max(cosLat, GeoConstants.MinCosLat))) * Math.Cos(perpAngle);
            }

            var resolved = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(wpLat, wpLon), (float)ResolveRadiusM);
            if (resolved != null)
            {
                var quality = _roadClassifier.ClassifyRoad(resolved, profile, avoidHighways);
                if (quality != RoadClassifier.RoadQuality.Blocked)
                {
                    // Check proximity to used edges via spatial index (O(cells) vs O(N))
                    if (!_edgeSpatialIndex.AnyEdgeWithin(resolved, MinEdgeDistanceForHomingM))
                    {
                        return resolved;
                    }
                }
            }
        }

        return null;
    }
}
