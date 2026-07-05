namespace MotoRouteFinder.Models;

public record MapPoint
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "waypoint";
    public Coordinate Coordinate { get; init; } = new();
    public string Label { get; init; } = "";
}
