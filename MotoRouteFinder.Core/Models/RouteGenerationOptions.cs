namespace MotoRouteFinder.Models;

/// <summary>
/// Configurable parameters for route generation. Defaults match the current hardcoded values.
/// Override via appsettings.json under the "RouteGeneration" section.
/// </summary>
public class RouteGenerationOptions
{
    // Routing
    public int MaxRouteAttempts { get; set; } = 3;
    public double MaxRepetitionRatio { get; set; } = 0.05;
    public int IdleTimeoutSeconds { get; set; } = 120;
    public int HeartbeatTimeoutSeconds { get; set; } = 60;

    // Quality scoring weights (stem penalty removed — weights redistributed)
    public double RepetitionWeight { get; set; } = 0.3529;
    public double CircularityWeight { get; set; } = 0.2353;
    public double CurvatureWeight { get; set; } = 0.1176;
    public double RoadTypeWeight { get; set; } = 0.1765;
    public double DistAccuracyWeight { get; set; } = 0.1176;

    // Quality scoring parameters
    public double CurvatureSweetSpot { get; set; } = 0.001;
    public double CurvatureSigma { get; set; } = 0.003;
    public double AcceptableRoadCreditFactor { get; set; } = 0.6;

    // §17: Curvature score plateau parameters
    public double CurvatureRampStartRad { get; set; } = 0.0005;
    public double CurvaturePlateauStartRad { get; set; } = 0.0018;
    public double CurvaturePlateauEndRad { get; set; } = 0.0060;
    public double CurvatureExcessiveRad { get; set; } = 0.0120;
    public double CurvatureMinScore { get; set; } = 20.0;

    // §18: Circularity spread normalization
    public double MaxBearingSpreadDegrees { get; set; } = 270;

    // Forward loop (RouteBuilder)
    public double ForwardLoopTargetRatio { get; set; } = 0.85;
    public double ReturnDistanceCapMultiplier { get; set; } = 1.4;
    public double EarlyAbortFailureRatio { get; set; } = 0.6;
    public double EarlyAbortDistanceRatio { get; set; } = 0.4;
    public double MinWaypointDistanceM { get; set; } = 200;
    public double BacktrackingBearingDelta { get; set; } = 150;
    public double MaxWaypointDistanceRatio { get; set; } = 0.40;
    public double EdgeDensityCheckRadiusM { get; set; } = 5000;
    public int EdgeDensityThreshold { get; set; } = 25;
    public double CascadeDetectionProximityM { get; set; } = 5000;
    public double EarlyRepetitionOverlapThreshold { get; set; } = 0.12;
    public long FixPipelineTimeoutMs { get; set; } = 8000;

    // Homing
    public double HomingPerpendicularOffsetM { get; set; } = 1500;
    public int MaxHomingAngleAttempts { get; set; } = 13;
    public double HomingAngleStepDegrees { get; set; } = 5;
    public double MinHomingSegmentDistanceKm { get; set; } = 3;
    public double HomingOverlapThreshold { get; set; } = 0.20;

    // Alternative path (AlternativePathFinder)
    public double MaxShareFactor { get; set; } = 0.10;
    public double EstimatedDetourFactor { get; set; } = 1.6;
    public double DistanceCapMultiplier { get; set; } = 1.3;
    public double CorridorWidthPrimary { get; set; } = 1000;
    public double CorridorWidthSecondary { get; set; } = 700;
    public double EdgePenaltyFactor { get; set; } = 10.0;
    public double MinimumSpreadThreshold { get; set; } = 200.0;
    public double PerpendicularOffsetFraction { get; set; } = 0.6;
    public double OverlapAcceptanceThreshold { get; set; } = 0.15;
    public double TurnaroundRandomOffsetDegrees { get; set; } = 30;
    public double TurnaroundDistanceVariation { get; set; } = 0.2;

    // Turnaround candidate scoring penalties (§13c — normalized to 0-1, weighted against BearingScoreWeight=1000)
    public double TurnaroundDensityPenaltyWeight { get; set; } = 300;
    public double TurnaroundAvoidBearingPenaltyWeight { get; set; } = 200;

    // Return path penalty escalation
    public double VeryHighPenaltyFactor { get; set; } = 20.0;
    public double HighReturnPenaltyFactor { get; set; } = 8.0;
    public double LowPenaltyFallbackFactor { get; set; } = 3.0;
    public double ShorterReturnPenaltyFactor { get; set; } = 5.0;
    public double GraduatedPenaltyBase { get; set; } = 5.0;
    public double GraduatedPenaltyStep { get; set; } = 5.0;
    public double PushFallbackPenaltyFactor { get; set; } = 5.0;
    public double PushFallbackMinDistanceM { get; set; } = 1500;
    public double PushFallbackDistanceMultiplier { get; set; } = 2.0;

    // Start-funnel overlap exclusion (scoring only)
    public double StartFunnelRadiusM { get; set; } = 500;

    // Triangle loop
    public bool TriangleLoopEnabled { get; set; } = false;
    public double TriangleLegDistanceFraction { get; set; } = 0.33;

    // Plateau scoring
    public double PlateauNormalizationDivisor { get; set; } = 5.0;
    public int MinPlateauLength { get; set; } = 3;

    // Repetition tiebreaker
    public double RepetitionTiebreakerRatio { get; set; } = 1.05;
    public double OvershootThresholdMultiplier { get; set; } = 1.5;

    // Absolute out-and-back overlap gate
    public double OutAndBackOverlapThresholdM { get; set; } = 2000;

    // §20: Early-accept quality floor
    public double EarlyAcceptQualityScore { get; set; } = 90;
}
