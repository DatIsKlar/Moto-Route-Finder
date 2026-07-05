using System.Collections.Generic;

namespace MotoRouteFinder.Models;

public class RouteResponse
{
    public List<Coordinate> RouteGeometry { get; set; } = new();
    public RouteStats Stats { get; set; } = new();
    public List<Coordinate> Waypoints { get; set; } = new();
    public List<(Coordinate From, Coordinate To)> RepetitionSegments { get; set; } = new();
    public string? StemDiagnosticsJson { get; set; }
    public RepetitionBreakdown? RepetitionBreakdown { get; set; }
}
