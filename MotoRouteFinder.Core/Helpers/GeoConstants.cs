using MotoRouteFinder.Models;

namespace MotoRouteFinder.Helpers;

public static class GeoConstants
{
    public const double MetersPerDegreeLat = 111_319.9;
    public const double KmPerDegreeLat = MetersPerDegreeLat / 1000;
    public const double KmPerHour = 30.0;
    public const double MinCosLat = 0.01;
    public const double EdgeSnapGridDegrees = 0.0002;

    public static double DirectionToBearing(DirectionBias direction) => direction switch
    {
        DirectionBias.North => 0,
        DirectionBias.Northeast => 45,
        DirectionBias.East => 90,
        DirectionBias.Southeast => 135,
        DirectionBias.South => 180,
        DirectionBias.Southwest => 225,
        DirectionBias.West => 270,
        DirectionBias.Northwest => 315,
        _ => -1,
    };

    public static string BearingToDirection(double bearing)
    {
        bearing = ((bearing % 360) + 360) % 360;
        return bearing switch
        {
            >= 337.5 or < 22.5 => "North",
            >= 22.5 and < 67.5 => "Northeast",
            >= 67.5 and < 112.5 => "East",
            >= 112.5 and < 157.5 => "Southeast",
            >= 157.5 and < 202.5 => "South",
            >= 202.5 and < 247.5 => "Southwest",
            >= 247.5 and < 292.5 => "West",
            >= 292.5 and < 337.5 => "Northwest",
            _ => ""
        };
    }
}
