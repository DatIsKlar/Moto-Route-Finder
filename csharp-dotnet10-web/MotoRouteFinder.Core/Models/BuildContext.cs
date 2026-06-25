using System;
using System.Collections.Generic;

namespace MotoRouteFinder.Models;

public class BuildContext
{
    public double MinLat, MaxLat, MinLon, MaxLon;
    public List<double> SegmentLengths = new();
    public int[] BlockedSectorCounts = new int[18];
    public int TotalPushAttemptsUsed;
    public Dictionary<string, int> WaypointRejectionReasons = new();
    public long IntermediateTotalMs, WaypointGenMs, ConnectivityCheckMs, StemDetectionMs, OverlapCalcMs;
    public long TotalSegmentBuildMs;
    public int TotalWaypointAttempts;
    public int FinalEdgeSetSize;
    public int HomingWaypointsUsed;
    public bool AttemptFailedEarlyAbort;
    public int MaxConsecutiveStemSegments;
    public int NullSegmentDrops;
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
    public List<double> ForwardWaypointAngles = new();
    public List<double> ForwardSegmentBearings = new();

    // Builder method tracking
    public string BuilderMethod = "";

    public BuildContext(Coordinate start)
    {
        MinLat = start.Lat; MaxLat = start.Lat;
        MinLon = start.Lon; MaxLon = start.Lon;
    }

    public void Reset(Coordinate start)
    {
        MinLat = start.Lat; MaxLat = start.Lat;
        MinLon = start.Lon; MaxLon = start.Lon;
        SegmentLengths.Clear();
        Array.Clear(BlockedSectorCounts, 0, BlockedSectorCounts.Length);
        TotalPushAttemptsUsed = 0;
        WaypointRejectionReasons.Clear();
        IntermediateTotalMs = 0; WaypointGenMs = 0; ConnectivityCheckMs = 0;
        StemDetectionMs = 0; OverlapCalcMs = 0;
        TotalSegmentBuildMs = 0;
        TotalWaypointAttempts = 0;
        FinalEdgeSetSize = 0;
        HomingWaypointsUsed = 0;
        AttemptFailedEarlyAbort = false;
        MaxConsecutiveStemSegments = 0;
        NullSegmentDrops = 0;
        ForwardDistanceKm = 0;
        ReturnDistanceKm = 0;
        ReturnRatio = 0;
        ForwardLoopExitReason = "";
        ReturnSegmentCurvature = 0;
        ReturnSegmentRoadType = "";
        ReturnSegmentOverlapPct = 0;
        ReturnPushAttempts = 0;
        ReturnOverlapBeforePush = 0;
        ReturnOverlapAfterPush = 0;
        EstReturnAtLoopExit = 0;
        ActualReturnVsEstimate = 0;
        ForwardHaversineAtExit = 0;
        SectorsBlockedAtReturn = 0;
        ReturnSegmentRerouteCount = 0;
        ForwardSegmentCount = 0;
        ReturnPathSectorCoverage = 0;
        ForwardWaypointCount = 0;
        ForwardWaypointAngles.Clear();
        ForwardSegmentBearings.Clear();
        BlockedMotorwayEdges.Clear();
        BuilderMethod = "";
    }
}
