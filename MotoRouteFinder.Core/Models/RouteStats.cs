using System;
using System.Collections.Generic;
using MotoRouteFinder.Helpers;

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
    public double DistAccuracyComponent { get; set; }
    public double CircularityScoreComponent { get; set; }
    public double CircularitySpreadSubScore { get; set; }
    public double CircularitySectorSubScore { get; set; }
    public double CircularityCompactnessSubScore { get; set; }

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
    public const int QualityFormulaVersion = 4;

    public static double CalculateQualityScore(
        RouteStats stats,
        RepetitionBreakdown? breakdown,
        double? targetDistanceKm,
        RouteGenerationOptions? options = null)
    {
        double repWeight = options?.RepetitionWeight ?? 0.3529;
        double circWeight = options?.CircularityWeight ?? 0.2353;
        double curvWeight = options?.CurvatureWeight ?? 0.1176;
        double roadWeight = options?.RoadTypeWeight ?? 0.1765;
        double distWeight = options?.DistAccuracyWeight ?? 0.1176;

        // Repetition score: 0% = 100, 10%+ = 0 (linear)
        double repetitionScore = Math.Max(0, 100 - stats.RepetitionRatio * 1000);

        // §17: Curvature score — one-sided plateau rewards twistiness
        double curvatureScore = RouteGeometryUtils.CurvatureScore(stats.AverageCurvature, options);

        // Road type score: preferred roads get full credit, acceptable roads get partial credit
        double totalKm = stats.TotalDistanceKm > 0 ? stats.TotalDistanceKm : 1;
        double preferredKm = stats.RoadQualityKm.TryGetValue("Preferred", out double pk) ? pk : 0;
        double acceptableKm = stats.RoadQualityKm.TryGetValue("Acceptable", out double ak) ? ak : 0;
        double acceptableCreditFactor = options?.AcceptableRoadCreditFactor ?? 0.6;
        double roadTypeScore = ((preferredKm + acceptableKm * acceptableCreditFactor) / totalKm) * 100;

        // Distance accuracy: within 5% = 90+ score
        double distAccuracy = 100;
        if (targetDistanceKm.HasValue && targetDistanceKm.Value > 0)
        {
            double deviation = Math.Abs(stats.TotalDistanceKm - targetDistanceKm.Value) / targetDistanceKm.Value;
            distAccuracy = Math.Max(0, 100 - deviation * 100);
        }

        return Math.Round(
            repetitionScore * repWeight +
            stats.CircularityScoreComponent * circWeight +
            curvatureScore * curvWeight +
            roadTypeScore * roadWeight +
            distAccuracy * distWeight,
            1);
    }

    /// <summary>
    /// Computes and assigns RepetitionScoreComponent, CurvatureScoreComponent,
    /// and DistAccuracyComponent on the stats object. Single source of truth.
    /// </summary>
    public static void CalculateScoreComponents(
        RouteStats stats,
        double targetDistanceKm,
        RouteGenerationOptions options)
    {
        stats.RepetitionScoreComponent = Math.Round(Math.Max(0, 100 - stats.RepetitionRatio * 1000), 1);
        // §17: Curvature score — one-sided plateau rewards twistiness
        stats.CurvatureScoreComponent = Math.Round(RouteGeometryUtils.CurvatureScore(stats.AverageCurvature, options), 1);
        double deviation = targetDistanceKm > 0
            ? Math.Abs(stats.TotalDistanceKm - targetDistanceKm) / targetDistanceKm
            : 0;
        stats.DistAccuracyComponent = Math.Round(Math.Max(0, 100 - deviation * 100), 1);
    }
}
