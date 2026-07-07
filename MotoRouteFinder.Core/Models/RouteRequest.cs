using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;

namespace MotoRouteFinder.Models;

public enum DirectionBias { Any, North, South, East, West, Northeast, Southeast, Southwest, Northwest }

public class RouteRequest
{
    [Required] public Coordinate Start { get; set; } = new();
    public List<Coordinate> Waypoints { get; set; } = new();
    [Range(1, 500)] public double? TargetDistanceKm { get; set; }
    [Range(1, 480)] public double? TargetDurationMin { get; set; }
    public bool AvoidHighways { get; set; } = true;
    [Range(1, 20)] public int WaypointCount { get; set; } = 6;
    public DirectionBias Direction { get; set; } = DirectionBias.Any;
    public bool CollectDiagnostics { get; set; }
}
