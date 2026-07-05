using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using MotoRouteFinder.Helpers;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Services;

public class ExportService
{
    public string GenerateGoogleMapsUrl(Coordinate start, List<Coordinate> routeGeometry)
    {
        // Sample route geometry down to max 25 points for better accuracy
        var sampled = SampleForGoogleMaps(routeGeometry, maxPoints: 25);

        var path = string.Join("/", sampled.Select(c => $"{c.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{c.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        return $"https://www.google.com/maps/dir/{path}";
    }

    private static List<Coordinate> SampleForGoogleMaps(List<Coordinate> geometry, int maxPoints)
    {
        if (geometry.Count <= maxPoints)
            return geometry;

        var sampled = new List<Coordinate> { geometry[0] };
        double totalDist = RouteGeometryUtils.CalculateDistance(geometry);
        double interval = totalDist / (maxPoints - 1);
        double accum = 0;

        for (int i = 1; i < geometry.Count; i++)
        {
            accum += RouteGeometryUtils.HaversineDistance(geometry[i - 1], geometry[i]);
            if (accum >= interval && sampled.Count < maxPoints - 1)
            {
                sampled.Add(geometry[i]);
                accum = 0;
            }
        }

        // Always include the last point (which should be close to start for a loop)
        sampled.Add(geometry[^1]);
        return sampled;
    }

    public string GenerateGpx(
        List<Coordinate> routeGeometry,
        Coordinate start,
        List<Coordinate> waypoints,
        string routeName = "Moto Route")
    {
        var ns = XNamespace.Get("http://www.topografix.com/GPX/1/1");
        var invariant = System.Globalization.CultureInfo.InvariantCulture;

        var gpx = new XElement(ns + "gpx",
            new XAttribute("version", "1.1"),
            new XAttribute("creator", "Moto Route Finder"),
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),

            // Metadata
            new XElement(ns + "metadata",
                new XElement(ns + "name", routeName),
                new XElement(ns + "time", DateTime.UtcNow.ToString("o"))
            ),

            // Waypoints
            new[] { new XElement(ns + "wpt",
                new XAttribute("lat", start.Lat.ToString(invariant)),
                new XAttribute("lon", start.Lon.ToString(invariant)),
                new XElement(ns + "name", "Start")
            ) }.Concat(waypoints.Select((wp, i) => new XElement(ns + "wpt",
                new XAttribute("lat", wp.Lat.ToString(invariant)),
                new XAttribute("lon", wp.Lon.ToString(invariant)),
                new XElement(ns + "name", $"Waypoint {i + 1}")
            ))),

            // Route
            new XElement(ns + "rte",
                new XElement(ns + "name", routeName),
                routeGeometry.Select(c => new XElement(ns + "rtept",
                    new XAttribute("lat", c.Lat.ToString(invariant)),
                    new XAttribute("lon", c.Lon.ToString(invariant))
                ))
            ),

            // Track
            new XElement(ns + "trk",
                new XElement(ns + "name", routeName),
                new XElement(ns + "type", "Motorcycle"),
                new XElement(ns + "trkseg",
                    routeGeometry.Select(c => new XElement(ns + "trkpt",
                        new XAttribute("lat", c.Lat.ToString(invariant)),
                        new XAttribute("lon", c.Lon.ToString(invariant))
                    ))
                )
            )
        );

        return new XDeclaration("1.0", "utf-8", null) + gpx.ToString();
    }

    public void SaveGpxFile(
        string filePath,
        List<Coordinate> routeGeometry,
        Coordinate start,
        List<Coordinate> waypoints,
        string routeName = "Moto Route")
    {
        var gpxContent = GenerateGpx(routeGeometry, start, waypoints, routeName);
        File.WriteAllText(filePath, gpxContent, Encoding.UTF8);
    }

    public string GetGpxFilename(string routeName = "moto-route")
    {
        var safeName = new string(routeName
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
            .ToArray());
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return $"{safeName}_{timestamp}.gpx";
    }
}
