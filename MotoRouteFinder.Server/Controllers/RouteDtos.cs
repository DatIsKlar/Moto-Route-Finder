using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Server.Controllers;

// Request DTOs

public class LoadMapRequest
{
    [Required] public string[] Paths { get; set; } = Array.Empty<string>();
    public bool AvoidHighways { get; set; } = true;
}

public class LoadServerMapRequest
{
    public string ServerPath { get; set; } = "";
    public bool AvoidHighways { get; set; } = true;
}

public class GenerateCandidatesRequest
{
    [Required] public RouteRequest RouteRequest { get; set; } = new();
    [Range(1, 8)] public int CandidateCount { get; set; } = 4;
}

public class ExportGpxRequest
{
    [Required] public List<Coordinate> RouteGeometry { get; set; } = new();
    public Coordinate? Start { get; set; }
    public List<Coordinate>? Waypoints { get; set; }
    public string? RouteName { get; set; }
}

public class ExportGoogleMapsRequest
{
    [Required] public List<Coordinate> RouteGeometry { get; set; } = new();
    public Coordinate? Start { get; set; }
}

public class SavedMapEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "osm";
    public DateTime LastLoaded { get; set; }
    [JsonIgnore] public bool Exists { get; set; }
    [JsonIgnore] public double SizeMB { get; set; }
}

public class AddSavedMapRequest
{
    public string Path { get; set; } = "";
    public bool AvoidHighways { get; set; } = true;
}

public class TestRunRequest
{
    [Required] public RouteRequest RouteRequest { get; set; } = new();
    [Range(1, 100)] public int TestCount { get; set; } = 5;
    [Range(1, 8)] public int CandidateCount { get; set; } = 4;
    public bool VerboseDiagnostics { get; set; } = true;
}
