namespace MotoRouteFinder.Models;

public record Coordinate(double Lat = 0, double Lon = 0)
{
    public override string ToString() => $"{Lat:F6}, {Lon:F6}";
}
