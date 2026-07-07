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

    private const double SampleIntervalM = 400; // §11c: reduced from 200 (halves samples, still statistically valid)

    public RouteStatistics(RoadClassifier roadClassifier)
    {
        _roadClassifier = roadClassifier;
    }

    public RouteStats CalculateStats(List<Coordinate> fullRoute, IProfileInstance profile,
        double? targetDistanceKm = null,
        RepetitionBreakdown? precomputedRepetition = null)
    {
        double totalDistance = RouteGeometryUtils.CalculateDistance(fullRoute);
        double avgCurvature = RouteGeometryUtils.CalculateAverageCurvature(fullRoute);
        var repetition = precomputedRepetition ?? CalculateRepetition(fullRoute);

        var roadTypeDistances = new Dictionary<string, double>();
        var roadQualityDistances = new Dictionary<string, double>
        {
            ["Preferred"] = 0,
            ["Acceptable"] = 0,
            ["Poor"] = 0,
            ["Blocked"] = 0,
        };
        var sampledPoints = RouteGeometryUtils.SampleAlongGeometry(fullRoute, SampleIntervalM);

        // Single pass: road quality, road type distances, and transitions
        int transitions = 0;
        string? lastType = null;
        foreach (var point in sampledPoints)
        {
            var quality = _roadClassifier.ClassifyRoad(point, profile);
            string qualityKey = quality.ToString();
            roadQualityDistances[qualityKey] += SampleIntervalM;

            var highway = _roadClassifier.GetHighwayType(point, profile);
            if (highway != null)
            {
                if (!roadTypeDistances.ContainsKey(highway))
                    roadTypeDistances[highway] = 0;
                roadTypeDistances[highway] += SampleIntervalM;

                if (lastType != null && highway != lastType)
                    transitions++;
                lastType = highway;
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
        };
    }

    public RepetitionBreakdown CalculateRepetition(List<Coordinate> fullRoute)
    {
        var edgeCounts = new Dictionary<RouteGeometryUtils.EdgeKey, double>();
        var creditedIndices = new HashSet<int>();
        double totalDistance = 0;
        double edgeOverlap = 0;

        for (int i = 0; i < fullRoute.Count - 1; i++)
        {
            var key = RouteGeometryUtils.MakeEdgeKey(fullRoute[i], fullRoute[i + 1]);
            double dist = RouteGeometryUtils.HaversineDistance(fullRoute[i], fullRoute[i + 1]);
            totalDistance += dist;

            if (edgeCounts.TryGetValue(key, out double existingDist))
            {
                double weight = existingDist >= dist * 2 ? 3.0 : 1.0;
                edgeOverlap += dist * weight;
                edgeCounts[key] = existingDist + dist;
                creditedIndices.Add(i);
            }
            else
            {
                edgeCounts[key] = dist;
            }
        }

        // Out-and-back overlap is a subset of edgeOverlap (reversed-key match catches it).
        // Kept for informational use (§12 gate: OutAndBackM <= threshold) but not
        // included in totalOverlap to avoid double-counting.
        double outAndBackOverlap = RouteGeometryUtils.DetectOutAndBackOverlap(fullRoute, _returnToStartIndex);

        // Proximity-based parallel overlap detection for divided highways.
        // Filter out indices already credited by edgeOverlap to prevent double-counting.
        var (parallelOverlapRaw, overlappingIndices) = RouteGeometryUtils.CalculateParallelOverlap(fullRoute, _returnToStartIndex);
        double parallelOverlap = 0;
        foreach (var idx in overlappingIndices)
        {
            if (!creditedIndices.Contains(idx))
                parallelOverlap += RouteGeometryUtils.HaversineDistance(fullRoute[idx], fullRoute[idx + 1]);
        }

        // §15: outAndBackOverlap excluded — already folded into edgeOverlap via reversed-key match
        double totalOverlap = edgeOverlap + parallelOverlap;
        double ratio = totalDistance > 0 ? totalOverlap / totalDistance : 0;

        System.Diagnostics.Debug.WriteLine($"[REPETITION] Edge overlap: {edgeOverlap:F0}m, Out-and-back: {outAndBackOverlap:F0}m, Parallel overlap: {parallelOverlap:F0}m, Total: {totalDistance:F0}m, Ratio: {ratio:P1}");

        return new RepetitionBreakdown
        {
            TotalRatio = ratio,
            EdgeOverlapM = edgeOverlap,
            OutAndBackM = outAndBackOverlap,
            ParallelOverlapM = parallelOverlap,
            TotalDistanceM = totalDistance,
        };
    }
}
