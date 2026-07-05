using System.Collections.Generic;

namespace MotoRouteFinder.Models;

public record DebugRouteSummary
{
    public string SegmentRole { get; set; } = "route_summary";
    public int AttemptNumber { get; set; }
    public double TargetDistanceKm { get; set; }
    public double TotalDistanceKm { get; set; }
    public int RequestedWaypointCount { get; set; }
    public int WaypointCount { get; set; }
    public string BuilderMethod { get; set; } = "";
    public double RepetitionRatio { get; set; }
    public double EdgeOverlapM { get; set; }
    public double OutAndBackOverlapM { get; set; }
    public double TotalOverlapM { get; set; }
    public int SegmentsTotal { get; set; }
    public int SegmentsOverlapTriggered { get; set; }
    public int PushReroutesSucceeded { get; set; }
    public int MaxEdgeReuse { get; set; }
    public double AvgEdgeReuse { get; set; }
    public double ElapsedMs { get; set; }
    public string Resolution { get; set; } = "";
    public double[] StartPoint { get; set; } = new double[2];

    // New diagnostic aggregates
    public int NearMissCount { get; set; }
    public int PrivateRoadDetectedCount { get; set; }
    public Dictionary<string, int> FixFailureByReasonCode { get; set; } = new();
    public double OvershootRatio { get; set; }

    // Sector blocking history (0-17, each 20 degrees)
    public int[] BlockedSectorCounts { get; set; } = new int[18];

    // Push-reroute stats
    public int PushAttemptsUsed { get; set; }

    // Attempt improvement
    public double RepetitionDeltaFromPrior { get; set; }

    // Edge efficiency
    public int UniqueEdgeCount { get; set; }
    public int TotalEdgeCount { get; set; }

    // Segment length stats
    public double AverageSegmentLengthM { get; set; }

    // Geographic spread
    public double MinLat { get; set; }
    public double MaxLat { get; set; }
    public double MinLon { get; set; }
    public double MaxLon { get; set; }

    // Waypoint rejection reasons
    public Dictionary<string, int> WaypointRejectionReasons { get; set; } = new();

    // Composite quality score (0-100)
    public double QualityScore { get; set; }
    public int QualityFormulaVersion { get; set; } = RouteStats.QualityFormulaVersion;

    // Performance timing breakdown
    public long FindMotorwayMs { get; set; }
    public long CalculateStatsMs { get; set; }
    public long CalculateRepetitionMs { get; set; }
    public long WaypointGenMs { get; set; }
    public long ConnectivityCheckMs { get; set; }
    public long OverlapCalcMs { get; set; }
    public long RoutingCallsMs { get; set; }
    public long IntermediateTotalMs { get; set; }

    // Itinero operation counts
    public int ResolveCount { get; set; }
    public int RoutingCount { get; set; }
    public int BlockEdgesCount { get; set; }

    // Route attempt diversity
    public double OverlapWithPreviousAttempt { get; set; }
    public bool IsDuplicateGeometry { get; set; }

    // Cache stats
    public int ResolveCacheHitCount { get; set; }
    public int ConnectivityCacheHitCount { get; set; }

    // Edge set tracking
    public int FinalEdgeSetSize { get; set; }

    // Motorway blocking stats
    public int MotorwayEdgesFound { get; set; }
    public int GridPointsSampled { get; set; }

    // Waypoint efficiency
    public int TotalWaypointAttempts { get; set; }
    public long TotalSegmentBuildMs { get; set; }

    // Part 1: Return pre-planning
    public int HomingWaypointsUsed { get; set; }

    // Session 9: New diagnostic fields for route quality improvement
    public double ReturnOverlapM { get; set; }
    public double ReturnSegmentLengthM { get; set; }
    public Dictionary<string, double> OverlapBySegmentPosition { get; set; } = new();
    public double EdgeSaturationRatio { get; set; }
    public bool AttemptFailedEarlyAbort { get; set; }
    public int WastedRoutingCalls { get; set; }
    public double WaypointDistributionUniformity { get; set; }
    public double HomingSuccessRate { get; set; }
    public double MotorwayKm { get; set; }
    public double MotorwayPct { get; set; }

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
    public double AverageCurvature { get; set; }
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

    // Return path diagnostics
    public int ReturnPushAttempts { get; set; }
    public double ReturnOverlapBeforePush { get; set; }
    public double ReturnOverlapAfterPush { get; set; }
    public double EstReturnAtLoopExit { get; set; }
    public double ActualReturnVsEstimate { get; set; }
    public double ForwardHaversineAtExit { get; set; }
    public int SectorsBlockedAtReturn { get; set; }
    public int ReturnSegmentRerouteCount { get; set; }

    // Route shape diagnostics
    public double ForwardBearingSpread { get; set; }
    public double ReturnBearingSpread { get; set; }
    public double TurnaroundBearing { get; set; }
    public double ForwardPathCurvature { get; set; }
    public double ReturnPathCurvature { get; set; }
    public double ForwardMaxDeviationFromLine { get; set; }
    public double ReturnMaxDeviationFromLine { get; set; }
    public int ForwardDistinctBearingCount { get; set; }
    public int ReturnDistinctBearingCount { get; set; }
    public int ForwardReturnSectorDifference { get; set; }
    public int ForwardPathWindingNumber { get; set; }
    public double ForwardBearingSpreadExHome { get; set; }
    public double ReturnBearingSpreadExHome { get; set; }
    public double TurnaroundAngle { get; set; }
    public double TurnaroundOffsetFromLine { get; set; }
    public double RouteEfficiency { get; set; }
    public double ForwardPathCompactness { get; set; }
    public double ReturnPathCompactness { get; set; }
    public double AvgSegmentBearing { get; set; }
    public double BearingVariance { get; set; }
    public int TotalRouteBearingChanges { get; set; }

    // Circularity diagnostics
    public int ForwardSegmentCount { get; set; }
    public int ReturnPathSectorCoverage { get; set; }
    public int ForwardWaypointCount { get; set; }
    public List<double> ForwardSegmentBearings { get; set; } = new();

    // Direction diagnostics
    public string RequestedDirectionBias { get; set; } = "Any";
    public double RequestedBearing { get; set; }
    public string DominantRouteDirection { get; set; } = "";
    public double[] TurnaroundCoordinates { get; set; } = new double[2];
    public List<double[]> ForwardWaypointCoordinates { get; set; } = new();

    // Cache effectiveness
    public int RoutingCacheHits { get; set; }
    public int RoutingCacheSize { get; set; }

    // Attempt comparison
    public double QualityDeltaFromPrior { get; set; }
    public bool Attempt1FailedHighRepetition { get; set; }
    public bool Attempt1FailedOvershoot { get; set; }

    // Phase 4: Performance - CPU profiling
    public long MemoryBytesStart { get; set; }
    public long MemoryBytesEnd { get; set; }
    public long MemoryBytesDelta { get; set; }
    public int GcCollections { get; set; }
    public int ProcessorCount { get; set; }

    // Phase 4: Performance - Memory diagnostics
    public long WorkingSetBytes { get; set; }
    public long PrivateMemoryBytes { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public long RouterDbFileSize { get; set; }
    public long PeakMemoryBytes { get; set; }

    // Phase 4: Performance - Per-method timing
    public long RouteAssemblyMs { get; set; }
    public long RoadClassificationMs { get; set; }
    public long CoordinateResolutionMs { get; set; }
    public long TotalWallClockMs { get; set; }

    // Phase 4: Performance - Bottleneck analysis
    public string TopBottleneck { get; set; } = "";
    public double BottleneckPct { get; set; }
    public string ParallelizationOpportunity { get; set; } = "";

    // Return path decision diagnostics
    public double ReturnPathNormalOverlap { get; set; }
    public bool ReturnPathVeryHighPenaltyApplied { get; set; }
    public int ReturnPathVeryHighPenaltyEdgeCount { get; set; }
    public double ReturnPathVeryHighPenaltyOverlap { get; set; }
    public bool ReturnPathVeryHighPenaltyAccepted { get; set; }
    public bool ReturnPathHighPenaltyApplied { get; set; }
    public int ReturnPathHighPenaltyEdgeCount { get; set; }
    public double ReturnPathHighPenaltyOverlap { get; set; }
    public bool ReturnPathHighPenaltyAccepted { get; set; }
    public bool ReturnPathPushFallbackApplied { get; set; }
    public double ReturnPathPushFallbackBestOverlap { get; set; }
    public string ReturnPathPenaltyLevelUsed { get; set; } = "";

    // Repetition root cause
    public string RepetitionRootCause { get; set; } = "";
    public double ForwardPathTurnaroundAngle { get; set; }
    public double ForwardPathDetourRatio { get; set; }
    public int ForwardPathEdgeDensity { get; set; }

    // Routing call timing breakdown (AlternativePathFinder instrumentation)
    public long ForwardPathRoutingMs { get; set; }
    public int ForwardPathRoutingCalls { get; set; }
    public long ReturnPathNormalRoutingMs { get; set; }
    public long ReturnPathVeryHighRoutingMs { get; set; }
    public long ReturnPathHighRoutingMs { get; set; }
    public long ReturnPathPushFallbackRoutingMs { get; set; }
    public int ReturnPathRoutingCalls { get; set; }

    // TryResolve bypass timing
    public long FindEdgesAlongLineMs { get; set; }
    public int FindEdgesAlongLineCalls { get; set; }
    public long ResolveForwardEdgeIdsMs { get; set; }
    public int ResolveForwardEdgeIdsCalls { get; set; }
    public long CountEdgesNearPointMs { get; set; }
    public int CountEdgesNearPointCalls { get; set; }

    // EdgeBlocker penalty/restore timing
    public long PenaltyEdgesMs { get; set; }
    public int PenaltyEdgesEdgeCount { get; set; }
    public long RestoreEdgesMs { get; set; }
    public int RestoreEdgesEdgeCount { get; set; }

    // Turnaround density check timing
    public long TurnaroundDensityCheckMs { get; set; }
    public int TurnaroundDensityCheckCalls { get; set; }
}
