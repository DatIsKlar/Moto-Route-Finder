namespace MotoRouteFinder.Models;

// Return path decision diagnostics — carried from AlternativePathFinder to DebugRouteSummary
public class ReturnPathDiagnostics
{
    public double NormalOverlap { get; set; }
    public bool VeryHighPenaltyApplied { get; set; }
    public int VeryHighPenaltyEdgeCount { get; set; }
    public double VeryHighPenaltyOverlap { get; set; }
    public bool VeryHighPenaltyAccepted { get; set; }
    public bool HighPenaltyApplied { get; set; }
    public int HighPenaltyEdgeCount { get; set; }
    public double HighPenaltyOverlap { get; set; }
    public bool HighPenaltyAccepted { get; set; }
    public bool PushFallbackApplied { get; set; }
    public double PushFallbackBestOverlap { get; set; }
    public string PenaltyLevelUsed { get; set; } = "";
    public string RepetitionRootCause { get; set; } = "";
    public double ForwardPathTurnaroundAngle { get; set; }
    public double ForwardPathDetourRatio { get; set; }
    public int ForwardPathEdgeDensity { get; set; }

    // Routing call timing breakdown (instrumentation)
    public long NormalRoutingMs { get; set; }
    public long VeryHighPenaltyRoutingMs { get; set; }
    public long HighPenaltyRoutingMs { get; set; }
    public long PushFallbackRoutingMs { get; set; }
    public int TotalRoutingCalls { get; set; }
}
