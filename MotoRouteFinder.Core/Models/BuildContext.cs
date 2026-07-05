using System;
using System.Collections.Generic;

namespace MotoRouteFinder.Models;

public class BuildContext
{
    public double MinLat, MaxLat, MinLon, MaxLon;
    public List<double> SegmentLengths = new();
    public int[] BlockedSectorCounts = new int[18];
    public int TotalPushAttemptsUsed;
    public int TotalPushReroutesSucceeded;
    public Dictionary<string, int> WaypointRejectionReasons = new();
    public long IntermediateTotalMs, WaypointGenMs, ConnectivityCheckMs, OverlapCalcMs;
    public long TotalSegmentBuildMs;
    public int TotalWaypointAttempts;
    public int FinalEdgeSetSize;
    public int HomingWaypointsUsed;
    public bool AttemptFailedEarlyAbort;
    public HashSet<uint> BlockedMotorwayEdges = new();

    // Overshoot analysis
    public double ForwardDistanceKm;
    public double ReturnDistanceKm;
    public double ReturnRatio;
    public string ForwardLoopExitReason = "";

    // Return segment quality
    public double ReturnSegmentCurvature;
    public string ReturnSegmentRoadType = "";
    public double ReturnSegmentOverlapPct;

    // Return path diagnostics
    public int ReturnPushAttempts;
    public double ReturnOverlapBeforePush;
    public double ReturnOverlapAfterPush;
    public double EstReturnAtLoopExit;
    public double ActualReturnVsEstimate;
    public double ForwardHaversineAtExit;
    public int SectorsBlockedAtReturn;
    public int ReturnSegmentRerouteCount;

    // Circularity diagnostics
    public int ForwardSegmentCount;
    public int ReturnPathSectorCoverage;
    public int ForwardWaypointCount;
    public List<double> ForwardSegmentBearings = new();
    public double WaypointDistributionUniformity;

    // Builder method tracking
    public string BuilderMethod = "";

    // Return path decision diagnostics (from AlternativePathFinder)
    public ReturnPathDiagnostics? ReturnPathDiagnostics;

    // Forward-path routing timing (§11a instrumentation)
    public long ForwardPathRoutingMs;
    public int ForwardPathRoutingCalls;

    // §11e: TryResolve bypass timing
    public long FindEdgesAlongLineMs;
    public int FindEdgesAlongLineCalls;
    public long ResolveForwardEdgeIdsMs;
    public int ResolveForwardEdgeIdsCalls;
    public long CountEdgesNearPointMs;
    public int CountEdgesNearPointCalls;

    // §11e: EdgeBlocker penalty/restore timing
    public long PenaltyEdgesMs;
    public int PenaltyEdgesEdgeCount;
    public long RestoreEdgesMs;
    public int RestoreEdgesEdgeCount;

    // §13a: Turnaround density check timing
    public long TurnaroundDensityCheckMs;
    public int TurnaroundDensityCheckCalls;

    // §13b: Cross-attempt bearing avoidance
    public double? FailedTurnaroundBearing;

    public BuildContext(Coordinate start)
    {
        MinLat = start.Lat; MaxLat = start.Lat;
        MinLon = start.Lon; MaxLon = start.Lon;
    }
}
