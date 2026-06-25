using System.Collections.Generic;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Services;

/// <summary>
/// Typed wrapper for diagnostic serialization.
/// </summary>
public class DiagnosticsOutput
{
    public List<DebugStemEvent> StemEvents { get; set; } = new();
    public List<DebugRouteSummary> RouteSummaries { get; set; } = new();
    public DebugFinalSummary? FinalSummary { get; set; }
}
