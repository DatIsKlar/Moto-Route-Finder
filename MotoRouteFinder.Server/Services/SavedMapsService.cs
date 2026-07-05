using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MotoRouteFinder.Server.Controllers;

namespace MotoRouteFinder.Server.Services;

public class SavedMapsService
{
    private readonly string _savedMapsPath;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    // M3: Lock for thread-safe read-modify-write on saved_maps.json
    private readonly object _lock = new();

    public SavedMapsService()
    {
        _savedMapsPath = Path.Combine(
            Environment.GetEnvironmentVariable("MAPS_DIR") ?? AppContext.BaseDirectory,
            "saved_maps.json");
    }

    public List<SavedMapEntry> ReadSavedMaps()
    {
        if (!File.Exists(_savedMapsPath)) return new();
        var json = File.ReadAllText(_savedMapsPath);
        return JsonSerializer.Deserialize<List<SavedMapEntry>>(json, JsonOpts) ?? new();
    }

    public void WriteSavedMaps(List<SavedMapEntry> maps)
    {
        File.WriteAllText(_savedMapsPath, JsonSerializer.Serialize(maps, JsonOpts));
    }

    public void AddToSavedMaps(string path)
    {
        lock (_lock)
        {
            var maps = ReadSavedMaps();
            var existing = maps.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.LastLoaded = DateTime.UtcNow;
            }
            else
            {
                var isCache = path.EndsWith(".routerdb", StringComparison.OrdinalIgnoreCase);
                maps.Add(new SavedMapEntry
                {
                    Path = path,
                    Name = Path.GetFileName(path),
                    Type = isCache ? "cache" : "osm",
                    LastLoaded = DateTime.UtcNow
                });
            }
            WriteSavedMaps(maps);
        }
    }

    public void RemoveFromSavedMaps(string path)
    {
        lock (_lock)
        {
            var maps = ReadSavedMaps();
            maps.RemoveAll(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
            WriteSavedMaps(maps);
        }
    }
}
