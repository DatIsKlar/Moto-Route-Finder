using System.ComponentModel.DataAnnotations;

namespace MotoRouteFinder.Models;

public record Coordinate(
    [Range(-90.0, 90.0)] double Lat = 0,
    [Range(-180.0, 180.0)] double Lon = 0)
{
    public override string ToString() => $"{Lat:F6}, {Lon:F6}";
}
