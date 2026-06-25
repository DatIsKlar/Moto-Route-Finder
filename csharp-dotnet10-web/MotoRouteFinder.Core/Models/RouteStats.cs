using System;
using System.Collections.Generic;

namespace MotoRouteFinder.Models;

public class RouteStats
{
    public double TotalDistanceKm { get; set; }
    public double TotalDurationMin { get; set; }
    public double AverageCurvature { get; set; }
    public double RepetitionRatio { get; set; }
    public int DeadEndsDetected { get; set; }
    public Dictionary<string, double> RoadTypes { get; set; } = new();

    // Road quality distribution (Preferred/Acceptable/Poor/Blocked km)
    public Dictionary<string, double> RoadQualityKm { get; set; } = new();

    // Motorway usage
    public double MotorwayKm { get; set; }
    public double MotorwayPct { get; set; }

    // Composite quality score (0-100)
    public double QualityScore { get; set; }

    // Phase 1: Geographic spread
    public double CentroidLat { get; set; }
    public double CentroidLon { get; set; }
    public double BoundingBoxAreaKm2 { get; set; }
    public double CompactnessRatio { get; set; }
    public double MaxDistanceFromStartKm { get; set; }
    public int SectorsVisited { get; set; }

    // Phase 1: Road type diversity
    public string DominantRoadType { get; set; } = "";
    public double DominantRoadTypePct { get; set; }
    public double ResidentialKm { get; set; }
    public double LivingStreetKm { get; set; }

    // Phase 2: Turning analysis
    public int TurnCount { get; set; }
    public int SharpTurnCount { get; set; }
    public int HairpinCount { get; set; }
    public double AverageTurnAngle { get; set; }
    public double StraightLineRatio { get; set; }

    // Phase 3: Road variety
    public int RoadTypeTransitions { get; set; }

    // Quality component breakdown
    public double RepetitionScoreComponent { get; set; }
    public double CurvatureScoreComponent { get; set; }
    public double StemPenaltyComponent { get; set; }
    public double DistAccuracyComponent { get; set; }
    public double CircularityScoreComponent { get; set; }

    // Overshoot analysis
    public double ForwardDistanceKm { get; set; }
    public double ReturnDistanceKm { get; set; }
    public double ForwardPctOfTotal { get; set; }
    public double ReturnRatio { get; set; }
    public string ForwardLoopExitReason { get; set; } = "";

    // Return segment quality
    public double ReturnSegmentCurvature { get; set; }
    public string ReturnSegmentRoadType { get; set; } = "";
    public double ReturnSegmentOverlapPct { get; set; }

    // Cache effectiveness
    public int RoutingCacheHits { get; set; }
    public int RoutingCacheSize { get; set; }

    // Quality formula version (increment when formula changes)
    public const int QualityFormulaVersion = 2;

    public static double CalculateQualityScore(
        RouteStats stats,
        RepetitionBreakdown? breakdown,
        int stemsDetected,
        int stemsDropped,
        int stemsFixed,
        double? targetDistanceKm)
    {
        // Repetition score: 0% = 100, 10%+ = 0 (linear)
        double repetitionScore = Math.Max(0, 100 - stats.RepetitionRatio * 1000);

        // Curvature score: sweet spot ~0.001 rad/m
        double curvatureScore = Math.Exp(-Math.Pow((stats.AverageCurvature - 0.001) / 0.002, 2)) * 100;

        // Road type score: % of route on preferred roads
        double totalKm = stats.TotalDistanceKm > 0 ? stats.TotalDistanceKm : 1;
        double preferredKm = stats.RoadQualityKm.TryGetValue("Preferred", out double pk) ? pk : 0;
        double roadTypeScore = (preferredKm / totalKm) * 100;

        // Stem penalty: dropped = -20 each (unresolved), fixed = -5 each (resolved but needed fixing)
        double stemPenalty = Math.Max(0, 100 - stemsDropped * 20 - stemsFixed * 5);

        // Distance accuracy: within 5% = 90+ score
        double distAccuracy = 100;
        if (targetDistanceKm.HasValue && targetDistanceKm.Value > 0)
        {
            double deviation = Math.Abs(stats.TotalDistanceKm - targetDistanceKm.Value) / targetDistanceKm.Value;
            distAccuracy = Math.Max(0, 100 - deviation * 100);
        }

        return Math.Round(
            repetitionScore * 0.30 +
            stats.CircularityScoreComponent * 0.20 +
            curvatureScore * 0.10 +
            roadTypeScore * 0.15 +
            stemPenalty * 0.15 +
            distAccuracy * 0.10,
            1);
    }
}
