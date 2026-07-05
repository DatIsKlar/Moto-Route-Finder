using System.Collections.Generic;

namespace MotoRouteFinder.Models;

public record DebugFinalSummary
{
    public string SegmentRole { get; set; } = "final_summary";
    public int WinnerAttempt { get; set; }
    public int TotalAttempts { get; set; }
    public double TargetDistanceKm { get; set; }
    public double BestRepetitionRatio { get; set; }
    public double BestTotalDistanceKm { get; set; }
    public int BestRequestedWaypointCount { get; set; }
    public int BestWaypointCount { get; set; }
    public string BestBuilderMethod { get; set; } = "";
    public double TotalElapsedMs { get; set; }
    public double[] StartPoint { get; set; } = new double[2];
    public RepetitionBreakdown? FinalRepetitionBreakdown { get; set; }
    public double QualityScore { get; set; }
    public int QualityFormulaVersion { get; set; } = RouteStats.QualityFormulaVersion;
    public bool UsedCapRoute { get; set; }

    // Aggregate Itinero operation counts across all attempts
    public int TotalResolveCount { get; set; }
    public int TotalRoutingCount { get; set; }
    public int TotalBlockEdgesCount { get; set; }

    // Attempt progression
    public List<double> AttemptElapsedMs { get; set; } = new();
    public List<double> AttemptRepetitionRatio { get; set; } = new();
    public List<double> AttemptQualityScore { get; set; } = new();
    public List<double> AttemptOverlapWithPrevious { get; set; } = new();

    // Final route quality
    public Dictionary<string, double>? FinalRoadQualityKm { get; set; }

    // Session 9: New diagnostic fields for cross-attempt analysis
    public List<int> AttemptNullDropCounts { get; set; } = new();
    public double BlockSectorEffectiveness { get; set; }
    public List<double> PerAttemptReturnOverlapM { get; set; } = new();

    // Direction diagnostics
    public string RequestedDirectionBias { get; set; } = "Any";
    public double RequestedBearing { get; set; }
    public string DominantRouteDirection { get; set; } = "";
    public double[] TurnaroundCoordinates { get; set; } = new double[2];
    public List<double[]> ForwardWaypointCoordinates { get; set; } = new();

    // Attempt rejection reasons
    public List<string> AttemptRejectionReasons { get; set; } = new();

    // Cache and motorway load-time diagnostics
    public bool MapLoadedFromCache { get; set; }
    public string MotorwayCacheFile { get; set; } = "";
    public int MotorwaysBlockedAtLoadTime { get; set; }
    public long MotorwayBlockLoadTimeMs { get; set; }
    public bool MotorwaysInCache { get; set; }
    public int MotorwaysFailedToBlock { get; set; }
    public bool MotorwayBlockValidationPassed { get; set; }
    public bool MotorwaysScanCompleted { get; set; }
    public bool MotorwayBlockingSuspect { get; set; }
    public int RoutableMotorwayEdgesOnLoad { get; set; }
    public bool CacheFileMissingAtGen { get; set; }
}

public record RepetitionBreakdown
{
    public double TotalRatio { get; set; }
    public double EdgeOverlapM { get; set; }
    public double OutAndBackM { get; set; }
    public double ParallelOverlapM { get; set; }
    public double TotalDistanceM { get; set; }
}
