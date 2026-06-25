using System.Collections.Generic;

namespace MotoRouteFinder.Models;

public enum DirectionBias { Any, North, South, East, West, Northeast, Southeast, Southwest, Northwest }

public class RouteRequest
{
    public Coordinate Start { get; set; } = new();
    public List<Coordinate> Waypoints { get; set; } = new();
    public double? TargetDistanceKm { get; set; }
    public double? TargetDurationMin { get; set; }
    public bool AvoidHighways { get; set; } = true;
    public int WaypointCount { get; set; } = 6;
    public DirectionBias Direction { get; set; } = DirectionBias.Any;
}
