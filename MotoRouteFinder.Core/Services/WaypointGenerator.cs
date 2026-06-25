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
    private readonly int[] _blockedSectorCounts = new int[18];

    public int[] BlockedSectorCounts => _blockedSectorCounts;

    public WaypointGenerator(RoadClassifier roadClassifier)
    {
        _roadClassifier = roadClassifier;
    }

    public void ResetCounters()
    {
        Array.Clear(_blockedSectorCounts, 0, _blockedSectorCounts.Length);
    }

    public static int GetSector(double angleRadians)
    {
        double degrees = (angleRadians * 180 / Math.PI + 360) % 360;
        return (int)(degrees / 20); // 18 sectors of 20° each
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
        for (int attempt = 0; attempt < 8; attempt++)
        {
            double angleJitter = (Random.Shared.NextDouble() - 0.5) * 0.5;
            double angle = targetAngle + angleJitter;
            double distanceScale = 0.7 + Random.Shared.NextDouble() * 0.6;
            double r = radiusKm * distanceScale;

            double wpLat = center.Lat + (r / GeoConstants.KmPerDegreeLat) * Math.Sin(angle);
            double wpLon = center.Lon + (r / (GeoConstants.KmPerDegreeLat * Math.Max(cosLat, GeoConstants.MinCosLat))) * Math.Cos(angle);

            var resolved = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(wpLat, wpLon), 3000);
            if (resolved != null)
            {
                var quality = _roadClassifier.ClassifyRoad(resolved, profile, avoidHighways);
                if (quality == RoadClassifier.RoadQuality.Preferred || quality == RoadClassifier.RoadQuality.Acceptable)
                    return resolved;
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
        HashSet<RouteGeometryUtils.EdgeKey> usedEdges)
    {
        double angle = bearingToStart + angleOffset;
        double cosLat = Math.Cos(currentPos.Lat * Math.PI / 180);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            double distanceScale = 0.8 + Random.Shared.NextDouble() * 0.4;
            double r = distanceKm * distanceScale;

            double wpLat = currentPos.Lat + (r / GeoConstants.KmPerDegreeLat) * Math.Sin(angle);
            double wpLon = currentPos.Lon + (r / (GeoConstants.KmPerDegreeLat * Math.Max(cosLat, GeoConstants.MinCosLat))) * Math.Cos(angle);

            var resolved = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(wpLat, wpLon), 3000);
            if (resolved != null)
            {
                var quality = _roadClassifier.ClassifyRoad(resolved, profile, avoidHighways);
                if (quality != RoadClassifier.RoadQuality.Blocked)
                {
                    // Check proximity to used edges
                    double edgeDist = RouteGeometryUtils.DistanceToNearestUsedEdge(resolved, usedEdges);
                    if (edgeDist >= 300) // At least 300m from used edges
                    {
                        return resolved;
                    }
                }
            }
        }

        return null;
    }
}
