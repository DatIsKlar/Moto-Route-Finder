using System;
using System.Collections.Generic;
using System.Linq;
using Itinero.Profiles;
using MotoRouteFinder.Helpers;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Services;

/// <summary>
/// Calculates route statistics and repetition metrics.
/// </summary>
public class RouteStatistics
{
    private readonly RoadClassifier _roadClassifier;
    private int _returnToStartIndex = -1;

    public int ReturnToStartIndex
    {
        get => _returnToStartIndex;
        set => _returnToStartIndex = value;
    }

    public RouteStatistics(RoadClassifier roadClassifier)
    {
        _roadClassifier = roadClassifier;
    }

    public RouteStats CalculateStats(List<Coordinate> fullRoute, IProfileInstance profile,
        int stemsDropped = 0, int stemsFixed = 0, double? targetDistanceKm = null)
    {
        double totalDistance = RouteGeometryUtils.CalculateDistance(fullRoute);
        double avgCurvature = RouteGeometryUtils.CalculateAverageCurvature(fullRoute);
        var repetition = CalculateRepetition(fullRoute);

        var roadTypeDistances = new Dictionary<string, double>();
        var roadQualityDistances = new Dictionary<string, double>
        {
            ["Preferred"] = 0,
            ["Acceptable"] = 0,
            ["Poor"] = 0,
            ["Blocked"] = 0,
        };
        var sampledPoints = RouteGeometryUtils.SampleAlongGeometry(fullRoute, 200);

        foreach (var point in sampledPoints)
        {
            // ClassifyRoad resolves edge and caches highway type — call first
            var quality = _roadClassifier.ClassifyRoad(point, profile);
            string qualityKey = quality.ToString();
            roadQualityDistances[qualityKey] += 200;

            // GetHighwayType hits cache from ClassifyRoad — no additional TryResolve
            var highway = _roadClassifier.GetHighwayType(point, profile);
            if (highway != null)
            {
                if (!roadTypeDistances.ContainsKey(highway))
                    roadTypeDistances[highway] = 0;
                roadTypeDistances[highway] += 200;
            }
        }

        double totalDistanceKm = totalDistance / 1000;
        double motorwayKm = roadTypeDistances.TryGetValue("motorway", out double mwKm) ? Math.Round(mwKm / 1000, 2) : 0;
        double motorwayPct = totalDistanceKm > 0 ? Math.Round(motorwayKm / totalDistanceKm * 100, 1) : 0;

        // Phase 1: Geographic spread
        var (centroidLat, centroidLon) = RouteGeometryUtils.CalculateCentroid(fullRoute);
        double bboxArea = RouteGeometryUtils.CalculateBoundingBoxAreaKm2(fullRoute);
        double compactness = bboxArea > 0 ? Math.Round(totalDistanceKm / bboxArea, 3) : 0;
        double maxDistFromStart = RouteGeometryUtils.CalculateMaxDistanceFromStart(fullRoute, fullRoute[0]);
        int sectorsVisited = RouteGeometryUtils.CountSectorsVisited(fullRoute, fullRoute[0]);

        // Phase 1: Road type diversity
        string dominantType = "";
        double dominantPct = 0;
        double totalRoadKm = roadTypeDistances.Values.Sum();
        if (totalRoadKm > 0)
        {
            var best = roadTypeDistances.OrderByDescending(kvp => kvp.Value).First();
            dominantType = best.Key;
            dominantPct = Math.Round(best.Value / totalRoadKm * 100, 1);
        }
        double residentialKm = roadTypeDistances.TryGetValue("residential", out double resKm) ? Math.Round(resKm / 1000, 2) : 0;
        double livingStreetKm = roadTypeDistances.TryGetValue("living_street", out double lsKm) ? Math.Round(lsKm / 1000, 2) : 0;

        // Phase 2: Turning analysis
        var (turns, sharp, hairpins, avgAngle, straightRatio) = RouteGeometryUtils.AnalyzeTurns(fullRoute);

        // Phase 3: Road type transitions
        int transitions = 0;
        string? lastType = null;
        foreach (var point in sampledPoints)
        {
            string? type = _roadClassifier.GetHighwayType(point, profile);
            if (type != null && lastType != null && type != lastType)
                transitions++;
            if (type != null)
                lastType = type;
        }

        // Quality component breakdown
        double repScore = 100.0 / (1.0 + repetition.TotalRatio * 30);
        double curvScore = Math.Exp(-Math.Pow((avgCurvature - 0.001) / 0.002, 2)) * 100;
        double stemPen = Math.Max(0, 100 - stemsDropped * 20 - stemsFixed * 5);
        double distAcc = 100;
        if (targetDistanceKm.HasValue && targetDistanceKm.Value > 0)
        {
            double deviation = Math.Abs(totalDistanceKm - targetDistanceKm.Value) / targetDistanceKm.Value;
            distAcc = Math.Max(0, 100 - deviation * 100);
        }

        return new RouteStats
        {
            TotalDistanceKm = Math.Round(totalDistanceKm, 2),
            TotalDurationMin = Math.Round(totalDistanceKm / GeoConstants.KmPerHour * 60, 1),
            AverageCurvature = Math.Round(avgCurvature, 4),
            RepetitionRatio = Math.Round(repetition.TotalRatio, 3),
            DeadEndsDetected = 0,
            RoadTypes = roadTypeDistances
                .ToDictionary(kvp => kvp.Key, kvp => Math.Round(kvp.Value / 1000, 2)),
            RoadQualityKm = roadQualityDistances
                .ToDictionary(kvp => kvp.Key, kvp => Math.Round(kvp.Value / 1000, 2)),
            MotorwayKm = motorwayKm,
            MotorwayPct = motorwayPct,
            CentroidLat = Math.Round(centroidLat, 4),
            CentroidLon = Math.Round(centroidLon, 4),
            BoundingBoxAreaKm2 = Math.Round(bboxArea, 1),
            CompactnessRatio = compactness,
            MaxDistanceFromStartKm = Math.Round(maxDistFromStart, 2),
            SectorsVisited = sectorsVisited,
            DominantRoadType = dominantType,
            DominantRoadTypePct = dominantPct,
            ResidentialKm = residentialKm,
            LivingStreetKm = livingStreetKm,
            TurnCount = turns,
            SharpTurnCount = sharp,
            HairpinCount = hairpins,
            AverageTurnAngle = avgAngle,
            StraightLineRatio = straightRatio,
            RoadTypeTransitions = transitions,
            RepetitionScoreComponent = Math.Round(repScore, 1),
            CurvatureScoreComponent = Math.Round(curvScore, 1),
            StemPenaltyComponent = Math.Round(stemPen, 1),
            DistAccuracyComponent = Math.Round(distAcc, 1),
        };
    }

    public RepetitionBreakdown CalculateRepetition(List<Coordinate> fullRoute)
    {
        var edgeCounts = new Dictionary<RouteGeometryUtils.EdgeKey, double>();
        double totalDistance = 0;
        double edgeOverlap = 0;

        for (int i = 0; i < fullRoute.Count - 1; i++)
        {
            var key = RouteGeometryUtils.MakeEdgeKey(fullRoute[i], fullRoute[i + 1]);
            var revKey = key.Reversed();
            double dist = RouteGeometryUtils.HaversineDistance(fullRoute[i], fullRoute[i + 1]);
            totalDistance += dist;

            if (edgeCounts.TryGetValue(key, out double existingDist))
            {
                double weight = existingDist >= dist * 2 ? 3.0 : 1.0;
                edgeOverlap += dist * weight;
                edgeCounts[key] = existingDist + dist;
            }
            else if (edgeCounts.TryGetValue(revKey, out existingDist))
            {
                double weight = existingDist >= dist * 2 ? 3.0 : 1.0;
                edgeOverlap += dist * weight;
                edgeCounts[revKey] = existingDist + dist;
            }
            else
            {
                edgeCounts[key] = dist;
            }
        }

        double outAndBackOverlap = RouteGeometryUtils.DetectOutAndBackOverlap(fullRoute, _returnToStartIndex);

        double stemOverlap = 0;
        int windowSize = Math.Min(30, fullRoute.Count / 4);
        if (windowSize >= 6)
        {
            var stemEdgeSet = new HashSet<RouteGeometryUtils.EdgeKey>();
            for (int i = 0; i < fullRoute.Count - windowSize; i += Math.Max(1, windowSize / 2))
            {
                if (StemDetector.IsStemSegment(fullRoute, i, windowSize))
                {
                    for (int j = i; j < i + windowSize - 1; j++)
                    {
                        var key = RouteGeometryUtils.MakeEdgeKey(fullRoute[j], fullRoute[j + 1]);
                        var revKey = key.Reversed();
                        if (!edgeCounts.ContainsKey(key) && !edgeCounts.ContainsKey(revKey)
                            && stemEdgeSet.Add(key))
                        {
                            stemOverlap += RouteGeometryUtils.HaversineDistance(fullRoute[j], fullRoute[j + 1]);
                        }
                    }
                }
            }
        }

        // Proximity-based parallel overlap detection for divided highways
        double parallelOverlap = 0;
        if (_returnToStartIndex > 0 && _returnToStartIndex < fullRoute.Count)
        {
            double maxDistM = 25.0;
            double maxBearingDelta = 30.0;
            const double gridDeg = 0.0005;

            // Build grid index of forward-path edges
            var forwardGrid = new Dictionary<(int, int), List<(int index, double bearing, double dist)>>();
            for (int i = 0; i < _returnToStartIndex - 1; i++)
            {
                double midLat = (fullRoute[i].Lat + fullRoute[i + 1].Lat) / 2;
                double midLon = (fullRoute[i].Lon + fullRoute[i + 1].Lon) / 2;
                double bearing = RouteGeometryUtils.ComputeBearing(fullRoute[i], fullRoute[i + 1]);
                double dist = RouteGeometryUtils.HaversineDistance(fullRoute[i], fullRoute[i + 1]);
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
            for (int i = _returnToStartIndex; i < fullRoute.Count - 1; i++)
            {
                double retMidLat = (fullRoute[i].Lat + fullRoute[i + 1].Lat) / 2;
                double retMidLon = (fullRoute[i].Lon + fullRoute[i + 1].Lon) / 2;
                double retBearing = RouteGeometryUtils.ComputeBearing(fullRoute[i], fullRoute[i + 1]);
                double retDist = RouteGeometryUtils.HaversineDistance(fullRoute[i], fullRoute[i + 1]);
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
                            double fwdMidLat = (fullRoute[fwdIdx].Lat + fullRoute[fwdIdx + 1].Lat) / 2;
                            double fwdMidLon = (fullRoute[fwdIdx].Lon + fullRoute[fwdIdx + 1].Lon) / 2;
                            double dist = RouteGeometryUtils.HaversineDistance(
                                new Coordinate(retMidLat, retMidLon),
                                new Coordinate(fwdMidLat, fwdMidLon));

                            if (dist > maxDistM) continue;

                            double bearingDelta = Math.Abs(retBearing - fwdBearing);
                            if (bearingDelta > 180) bearingDelta = 360 - bearingDelta;
                            if (bearingDelta > maxBearingDelta) continue;

                            parallelOverlap += retDist;
                            found = true;
                            break;
                        }
                    }
                }
            }
        }

        double totalOverlap = edgeOverlap + outAndBackOverlap + stemOverlap + parallelOverlap;
        double ratio = totalDistance > 0 ? totalOverlap / totalDistance : 0;

        System.Diagnostics.Debug.WriteLine($"[REPETITION] Edge overlap: {edgeOverlap:F0}m, Out-and-back: {outAndBackOverlap:F0}m, Stem overlap: {stemOverlap:F0}m, Parallel overlap: {parallelOverlap:F0}m, Total: {totalDistance:F0}m, Ratio: {ratio:P1}");

        return new RepetitionBreakdown
        {
            TotalRatio = ratio,
            EdgeOverlapM = edgeOverlap,
            OutAndBackM = outAndBackOverlap,
            StemOverlapM = stemOverlap,
            ParallelOverlapM = parallelOverlap,
            TotalDistanceM = totalDistance,
        };
    }
}
