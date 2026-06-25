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
    private readonly DiagnosticsOutput _output = new();
    private string? _cachedJson;

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

    public string ToJson()
    {
        if (_cachedJson != null) return _cachedJson;
        if (_output.StemEvents.Count == 0 && _output.RouteSummaries.Count == 0 && _output.FinalSummary == null)
            return "[]";
        _cachedJson = JsonSerializer.Serialize(_output, new JsonSerializerOptions { WriteIndented = true });
        return _cachedJson;
    }

    public (int stemsDetected, int stemsFixed, int stemsDropped, int overlapTriggered, int pushReroutes) CountAll()
    {
        int stemsDetected = 0, stemsFixed = 0, stemsDropped = 0, overlapTriggered = 0, pushReroutes = 0;

        foreach (var e in _output.StemEvents)
        {
            if (e.IsStem) stemsDetected++;
            if (e.Resolution is "fixedAndKept" or "replaced" or "intermediate" or "multiHop") stemsFixed++;
            if (e.Resolution == "dropped") stemsDropped++;
            if (e.OverlapWithPriorSegments > 0.10) overlapTriggered++;
            if (e.PushRerouted) pushReroutes++;
        }

        return (stemsDetected, stemsFixed, stemsDropped, overlapTriggered, pushReroutes);
    }

    public int CountSegmentsTotal() =>
        _output.StemEvents.Count;

    public int CountStemsTimedOut() =>
        _output.StemEvents.Count(e => e.Resolution == "timeout_accepted");

    public int CountNearMisses() =>
        _output.StemEvents.Count(e => e.NearestNearMissM < 200 && e.NearestNearMissM > 0);

    public int CountPrivateRoads() =>
        _output.StemEvents.Count(e => e.RootCause == StemRootCause.PrivateRoad);

    public Dictionary<string, int> GetFixFailureByReasonCode() =>
        _output.StemEvents
            .SelectMany(e => new[] { e.TryFixStem, e.GenerateReplacement, e.Intermediate }
                .Where(f => f?.ReasonCode.HasValue == true && !f.Succeeded))
            .GroupBy(f => f!.ReasonCode!.Value)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

    public int SumResolveCount() =>
        _output.RouteSummaries.Sum(s => s.ResolveCount);

    public int SumRoutingCount() =>
        _output.RouteSummaries.Sum(s => s.RoutingCount);

    public int SumBlockEdgesCount() =>
        _output.RouteSummaries.Sum(s => s.BlockEdgesCount);

    public IEnumerable<DebugStemEvent> GetAllStemEvents() => _output.StemEvents;

    public IEnumerable<DebugRouteSummary> GetAllRouteSummaries() => _output.RouteSummaries;
}
