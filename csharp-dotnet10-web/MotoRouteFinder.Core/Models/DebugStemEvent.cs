using System.Collections.Generic;

namespace MotoRouteFinder.Models;

public enum FailureReasonCode
{
    None = 0,
    StemEdgesUnresolvable = 1,
    NoEdgesToBlock = 2,
    NoAlternativeRoute = 3,
    AlternativeStillOverlaps = 4,
    WaypointOnBadRoad = 5,
    NoReplacementWaypoint = 6,
    ReplacementAlsoStem = 7,
    NoRouteToReplacement = 8,
    IntermediateAlsoStem = 9,
    IntermediateOnBadRoad = 10,
    NoRoadNearMidpoint = 11,
    MidpointTooClose = 12,
    FixedStillOverlaps = 13,
    MultiHopStillOverlaps = 14,
    NoAlternativeReturn = 15,
    ReturnRerouteStillOverlaps = 16,
    MultiHopSplitFailed = 17,
}

public enum StemCause
{
    None,
    Backtracking,
    DeadEnd,
    LongSegment,
    OverlapWithPrior,
}

public enum StemRootCause
{
    Unknown,
    OneWayStreet,       // Route follows same road back (one-way forces loop)
    DeadEndRoad,        // Endpoint has low connectivity (dead-end road)
    NoDirectRoad,       // No road exists in the target direction
    PrivateRoad,        // Direct path blocked by private/restricted access
    TerrainDetour,      // Long detour around obstacle (river, hill)
    OvershootBacktrack, // Router overshot and had to backtrack to reach waypoint
}

public record DebugStemEvent
{
    public int AttemptNumber { get; set; }
    public string SegmentRole { get; set; } = "";
    public int SegmentIndex { get; set; }
    public double SegmentLengthM { get; set; }
    public double CumulativeDistanceM { get; set; }
    public double SegmentBearing { get; set; }
    public int FirstHalfPoints { get; set; }
    public int SecondHalfPoints { get; set; }
    public int CloseCount { get; set; }
    public int OpposedCount { get; set; }
    public int Examined { get; set; }
    public double CloseRatio { get; set; }
    public double OpposedRatio { get; set; }
    public bool IsStem { get; set; }
    public double OverlapWithPriorSegments { get; set; }

    public DebugFixStep? TryFixStem { get; set; }
    public DebugFixStep? GenerateReplacement { get; set; }
    public DebugFixStep? Intermediate { get; set; }

    public string Resolution { get; set; } = "";
    public double[] OriginalWaypoint { get; set; } = new double[2];
    public double[]? FinalWaypoint { get; set; }
    public double FinalWaypointDistanceM { get; set; }
    public int HopCount { get; set; }
    public bool PushRerouted { get; set; }
    public double PushOverlapBefore { get; set; }
    public double PushOverlapAfter { get; set; }

    public string? RoadType { get; set; }
    public double MidpointLat { get; set; }
    public double MidpointLon { get; set; }
    public double DistanceFromStartM { get; set; }
    public double SectorFromStart { get; set; }
    public double SegmentCurvature { get; set; }
    public int EdgeCount { get; set; }
    public string? CandidateRoadClass { get; set; }
    public StemCause StemCause { get; set; }
    public long SegmentBuildMs { get; set; }
    public long FixPipelineMs { get; set; }

    // Stem root cause analysis
    public StemRootCause RootCause { get; set; }
    public double StartBearing { get; set; }
    public double EndBearing { get; set; }
    public double BearingDelta { get; set; }
    public int EndpointConnectivity { get; set; }
    public double MaxDeviationFromLine { get; set; }

    // Near-miss tracking (stem detection sensitivity)
    public double NearestNearMissM { get; set; }

    // Return distance accuracy
    public double EstReturnHaversineKm { get; set; }
    public double EstReturnWithMultiplierKm { get; set; }
    public double ActualReturnRoutedKm { get; set; }

    // Which strategy actually succeeded
    public string? FixStrategy { get; set; }

    // Gap 1: StraightLineRatio (totalDist / straightDist) — stem severity signal
    public double StraightLineRatio { get; set; }

    // Gap 2: Consecutive stem count at time of detection
    public int ConsecutiveStemCount { get; set; }

    // Gap 4: Edge identity in overlap
    public int OverlappingEdgeCount { get; set; }
    public List<uint>? OverlappingEdgeIds { get; set; }

    // Gap 6: Root cause analysis on non-stem segments
    public bool RootCauseAnalyzed { get; set; }

    // Gap 3: Failed waypoint attempts for this segment
    public List<FailedWaypoint>? FailedWaypoints { get; set; }

    // Session 9: New diagnostic fields for route quality improvement
    public double PostFixOverlap { get; set; }                    // Overlap after all fixes applied
    public double HaversineToRoutedRatio { get; set; }            // Terrain detour indicator (haversineDist / routedDist)
    public bool IsReturnPathOverlap { get; set; }                 // Flag for return-only overlap
    public string? RoadQualityAtMidpoint { get; set; }            // Road quality at segment center
    public int SegmentEdgeDensityAtMidpoint { get; set; }         // Used-edge density at midpoint
    public int ConnectivityAtStart { get; set; }                  // Connectivity at segment start
    public int ConnectivityAtEnd { get; set; }                    // Connectivity at segment end
    public int FixCascadeCount { get; set; }                      // How many fix steps were tried
    public bool OneWayStreetDetected { get; set; }                // Explicit one-way detection
    public bool SegmentRoadClassChange { get; set; }              // Road class changes mid-segment
    public int TimeSinceLastStem { get; set; }                    // Gap since last stem (segment index)
}

public record DebugFixStep
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public string? FailureReason { get; set; }
    public FailureReasonCode? ReasonCode { get; set; }
    public Dictionary<string, double>? Details { get; set; }
    public double OverlapBefore { get; set; }
    public double OverlapAfter { get; set; }
    public long FixAttemptMs { get; set; }
}

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
    public double StemOverlapM { get; set; }
    public double TotalOverlapM { get; set; }
    public int SegmentsTotal { get; set; }
    public int StemsDetected { get; set; }
    public int StemsFixed { get; set; }
    public int StemsDropped { get; set; }
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

    // Stems by road type
    public Dictionary<string, int> StemsByRoadType { get; set; } = new();

    // Stems by distance band (0-25%, 25-50%, 50-75%, 75-100% of target)
    public Dictionary<string, int> StemsByDistanceBand { get; set; } = new();

    // Sector blocking history (0-17, each 20 degrees)
    public int[] BlockedSectorCounts { get; set; } = new int[18];

    // Fix pipeline timing breakdown
    public long FixStemTotalMs { get; set; }
    public long ReplacementTotalMs { get; set; }
    public long MultiHopTotalMs { get; set; }

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

    // Stem counts by role
    public int ForwardStemCount { get; set; }
    public int ReturnStemCount { get; set; }

    // Waypoint rejection reasons
    public Dictionary<string, int> WaypointRejectionReasons { get; set; } = new();

    // Composite quality score (0-100)
    public double QualityScore { get; set; }
    public int QualityFormulaVersion { get; set; } = RouteStats.QualityFormulaVersion;

    // Stems that timed out (>5s fix pipeline)
    public int StemsTimedOut { get; set; }

    // Performance timing breakdown
    public long FindMotorwayMs { get; set; }
    public long CalculateStatsMs { get; set; }
    public long CalculateRepetitionMs { get; set; }
    public long WaypointGenMs { get; set; }
    public long ConnectivityCheckMs { get; set; }
    public long StemDetectionMs { get; set; }
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

    // Fix pipeline aggregates
    public int FixStemCallCount { get; set; }
    public int FixStemSuccessCount { get; set; }
    public int ReplacementCallCount { get; set; }
    public int ReplacementSuccessCount { get; set; }
    public int MultiHopCallCount { get; set; }
    public int MultiHopSuccessCount { get; set; }

    // Root cause distribution
    public Dictionary<string, int> StemsByRootCause { get; set; } = new();

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

    // Gap 5: Geographic stem clustering
    public List<StemCluster> StemClusters { get; set; } = new();

    // Session 9: New diagnostic fields for route quality improvement
    public double ReturnOverlapM { get; set; }                    // Return segment vs forward overlap
    public double ReturnSegmentLengthM { get; set; }              // Return segment distance
    public Dictionary<string, double> OverlapBySegmentPosition { get; set; } = new(); // Overlap by quarter (0-25%, 25-50%, 50-75%, 75-100%)
    public int StemsAcceptedWithHighOverlap { get; set; }         // Stems accepted despite failed fixes
    public double EdgeSaturationRatio { get; set; }               // UniqueEdges / TotalEdges
    public bool AttemptFailedEarlyAbort { get; set; }             // Early abort flag
    public int MaxConsecutiveStemSegments { get; set; }           // Worst cascade length
    public int NullSegmentDrops { get; set; }                     // Segments silently dropped
    public Dictionary<string, int> FixStrategyDistribution { get; set; } = new(); // Strategy usage counts
    public Dictionary<string, double> FixStrategySuccessRates { get; set; } = new(); // Strategy success rates
    public double AvgFixPipelineMsPerStem { get; set; }           // Average fix cost
    public int WastedRoutingCalls { get; set; }                   // Null-routing-call count
    public double WaypointDistributionUniformity { get; set; }    // Angular spread quality (0-1)
    public double HomingSuccessRate { get; set; }                 // Homing waypoints attempted vs succeeded
    public double MotorwayKm { get; set; }                        // Motorway distance in route
    public double MotorwayPct { get; set; }                       // Motorway percentage of route

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
    public List<double> ForwardWaypointAngles { get; set; } = new();
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
}

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
    public int StemsTimedOut { get; set; }

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
    public List<int> AttemptStemCounts { get; set; } = new();           // Per-attempt stem count progression
    public List<int> AttemptNullDropCounts { get; set; } = new();       // Per-attempt null segment drops
    public double BlockSectorEffectiveness { get; set; }                // Whether carryover helped
    public List<double> PerAttemptReturnOverlapM { get; set; } = new(); // Return overlap per attempt

    // Direction diagnostics
    public string RequestedDirectionBias { get; set; } = "Any";
    public double RequestedBearing { get; set; }
    public string DominantRouteDirection { get; set; } = "";
    public double[] TurnaroundCoordinates { get; set; } = new double[2];
    public List<double[]> ForwardWaypointCoordinates { get; set; } = new();

    // Cache and motorway load-time diagnostics
    public bool MapLoadedFromCache { get; set; }
    public string MotorwayCacheFile { get; set; } = "";
    public int MotorwaysBlockedAtLoadTime { get; set; }
    public long MotorwayBlockLoadTimeMs { get; set; }
    public bool MotorwaysInCache { get; set; }
    public int MotorwaysFailedToBlock { get; set; }
    public bool MotorwayBlockValidationPassed { get; set; }
    public bool MotorwaysScanCompleted { get; set; }
}

public record RepetitionBreakdown
{
    public double TotalRatio { get; set; }
    public double EdgeOverlapM { get; set; }
    public double OutAndBackM { get; set; }
    public double StemOverlapM { get; set; }
    public double ParallelOverlapM { get; set; }
    public double TotalDistanceM { get; set; }
}

// Gap 3: Failed waypoint attempt record
public record FailedWaypoint
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string Reason { get; set; } = "";
    public double DistanceFromCurrentPosM { get; set; }
}

// Gap 5: Geographic stem cluster record
public record StemCluster
{
    public int GridLat { get; set; }
    public int GridLon { get; set; }
    public int StemCount { get; set; }
    public double AvgOverlapRatio { get; set; }
    public List<int> SegmentIndices { get; set; } = new();
}
