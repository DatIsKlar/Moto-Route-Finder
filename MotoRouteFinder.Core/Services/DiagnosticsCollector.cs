using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Services;

/// <summary>
/// Manages stem diagnostics collection and serialization.
/// </summary>
public class DiagnosticsCollector
{
    private static readonly JsonSerializerOptions sIndentedOptions = new() { WriteIndented = true };
    private readonly DiagnosticsOutput _output = new();
    private string? _cachedJson;

    public bool Enabled { get; set; }

    public int Count => _output.StemEvents.Count + _output.RouteSummaries.Count + (_output.FinalSummary != null ? 1 : 0);

    public void Clear()
    {
        _cachedJson = null;
        _output.StemEvents.Clear();
        _output.RouteSummaries.Clear();
        _output.FinalSummary = null;
    }

    public void Add(object item)
    {
        if (!Enabled) return;
        _cachedJson = null;
        switch (item)
        {
            case DebugStemEvent evt:
                _output.StemEvents.Add(evt);
                break;
            case DebugRouteSummary summary:
                _output.RouteSummaries.Add(summary);
                break;
            case DebugFinalSummary final_:
                _output.FinalSummary = final_;
                break;
        }
    }

    public string? ToJson()
    {
        if (!Enabled) return null;
        if (_cachedJson != null) return _cachedJson;
        if (_output.StemEvents.Count == 0 && _output.RouteSummaries.Count == 0 && _output.FinalSummary == null)
            return "[]";
        _cachedJson = JsonSerializer.Serialize(_output, sIndentedOptions);
        return _cachedJson;
    }

    public int CountOverlapTriggered()
    {
        int overlapTriggered = 0;

        foreach (var e in _output.StemEvents)
        {
            if (e.OverlapWithPriorSegments > 0.10) overlapTriggered++;
        }

        return overlapTriggered;
    }

    public int CountSegmentsTotal() =>
        _output.StemEvents.Count;

    public int CountNearMisses() =>
        _output.StemEvents.Count(e => e.NearestNearMissM < 200 && e.NearestNearMissM > 0);

    public int CountPrivateRoads() =>
        0;

    public int SumResolveCount() =>
        _output.RouteSummaries.Sum(s => s.ResolveCount);

    public int SumRoutingCount() =>
        _output.RouteSummaries.Sum(s => s.RoutingCount);

    public int SumBlockEdgesCount() =>
        _output.RouteSummaries.Sum(s => s.BlockEdgesCount);

    public IEnumerable<DebugStemEvent> GetAllStemEvents() => _output.StemEvents;

    public IEnumerable<DebugRouteSummary> GetAllRouteSummaries() => _output.RouteSummaries;
}
