using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MotoRouteFinder.Models;
using MotoRouteFinder.Services;

namespace MotoRouteFinder.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RouteController : ControllerBase
{
    private readonly RoutingService _routingService;
    private readonly ExportService _exportService;
    private readonly ILogger<RouteController> _logger;

    public RouteController(RoutingService routingService, ExportService exportService, ILogger<RouteController> logger)
    {
        _routingService = routingService;
        _exportService = exportService;
        _logger = logger;
    }

    [HttpGet("maps/status")]
    public IActionResult GetMapStatus()
    {
        return Ok(new
        {
            isLoaded = _routingService.IsLoaded,
            loadedMaps = _routingService.LoadedMaps,
            cachePath = _routingService.CachePath,
            memoryMB = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1)
        });
    }

    [HttpPost("maps/unload")]
    public IActionResult UnloadMap()
    {
        _routingService.ClearMaps();
        GC.Collect(2, GCCollectionMode.Forced, true);
        return Ok(new
        {
            status = "unloaded",
            memoryMB = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1)
        });
    }

    [HttpPost("maps/load")]
    public async Task<IActionResult> LoadMap([FromBody] LoadMapRequest request)
    {
        if (request.Paths == null || request.Paths.Length == 0)
            return BadRequest(new { error = "No file paths provided" });

        try
        {
            if (request.Paths.Length == 1)
                await _routingService.LoadMapAsync(request.Paths[0], request.AvoidHighways);
            else
                await _routingService.LoadMapsAsync(request.Paths, request.AvoidHighways);

            return Ok(new
            {
                status = "loaded",
                loadedMaps = _routingService.LoadedMaps,
                cachePath = _routingService.CachePath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load map");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("maps/load-server")]
    public async Task<IActionResult> LoadMapFromServer([FromBody] LoadServerMapRequest request)
    {
        if (string.IsNullOrEmpty(request.ServerPath))
            return BadRequest(new { error = "No server path provided" });

        try
        {
            if (request.ServerPath.EndsWith(".routerdb", StringComparison.OrdinalIgnoreCase))
                await _routingService.LoadCacheAsync(request.ServerPath);
            else
                await _routingService.LoadMapAsync(request.ServerPath, request.AvoidHighways);

            AddToSavedMaps(request.ServerPath);

            return Ok(new
            {
                status = "loaded",
                loadedMaps = _routingService.LoadedMaps,
                cachePath = _routingService.CachePath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load map from server path");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("maps/upload")]
    [RequestSizeLimit(524288000)]
    public async Task<IActionResult> UploadMap(IFormFile[] files, [FromQuery] bool avoidHighways = false)
    {
        if (!ModelState.IsValid)
        {
            var errors = string.Join("; ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            _logger.LogWarning("ModelState errors: {Errors}", errors);
            return BadRequest(new { error = "ModelState invalid", details = errors });
        }

        if (files == null || files.Length == 0)
            return BadRequest(new { error = "No files uploaded" });

        var uploadsDir = Environment.GetEnvironmentVariable("MAPS_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "uploads");
        Directory.CreateDirectory(uploadsDir);

        var filePaths = new List<string>();
        foreach (var file in files)
        {
            var filePath = Path.Combine(uploadsDir, file.FileName);
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }
            filePaths.Add(filePath);
        }

        try
        {
            if (filePaths.Count == 1)
            {
                if (filePaths[0].EndsWith(".routerdb", StringComparison.OrdinalIgnoreCase))
                    await _routingService.LoadCacheAsync(filePaths[0]);
                else
                    await _routingService.LoadMapAsync(filePaths[0], avoidHighways);
            }
            else
            {
                await _routingService.LoadMapsAsync(filePaths.ToArray(), avoidHighways);
            }

            return Ok(new
            {
                status = "loaded",
                loadedMaps = _routingService.LoadedMaps,
                cachePath = _routingService.CachePath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load uploaded map");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("maps/browse")]
    public IActionResult BrowseDirectory([FromQuery] string? path = null)
    {
        try
        {
            var targetPath = string.IsNullOrEmpty(path)
                ? (Environment.GetEnvironmentVariable("MAPS_DIR")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                : path;

            if (!Directory.Exists(targetPath))
                return BadRequest(new { error = $"Directory not found: {targetPath}" });

            var entries = new List<object>();

            // Add parent directory link
            var parent = Directory.GetParent(targetPath);
            if (parent != null)
            {
                entries.Add(new
                {
                    name = "..",
                    fullPath = parent.FullName,
                    isDirectory = true
                });
            }

            // Add subdirectories
            foreach (var dir in Directory.GetDirectories(targetPath).OrderBy(d => d))
            {
                entries.Add(new
                {
                    name = Path.GetFileName(dir),
                    fullPath = dir,
                    isDirectory = true
                });
            }

            // Add map files
            var mapExtensions = new[] { ".osm", ".pbf", ".routerdb" };
            foreach (var file in Directory.GetFiles(targetPath)
                .Where(f => mapExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f))
            {
                var info = new FileInfo(file);
                entries.Add(new
                {
                    name = Path.GetFileName(file),
                    fullPath = file,
                    isDirectory = false,
                    sizeMB = Math.Round(info.Length / 1048576.0, 1)
                });
            }

            return Ok(new
            {
                currentPath = targetPath,
                entries
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to browse directory");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("maps/clear")]
    public IActionResult ClearMaps()
    {
        _routingService.ClearMaps();
        return Ok(new { status = "cleared" });
    }

    // --- Saved Maps persistence ---

    private static readonly string SavedMapsPath = Path.Combine(
        Environment.GetEnvironmentVariable("MAPS_DIR") ?? AppContext.BaseDirectory,
        "saved_maps.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static List<SavedMapEntry> ReadSavedMaps()
    {
        if (!System.IO.File.Exists(SavedMapsPath)) return new();
        var json = System.IO.File.ReadAllText(SavedMapsPath);
        return JsonSerializer.Deserialize<List<SavedMapEntry>>(json, JsonOpts) ?? new();
    }

    private static void WriteSavedMaps(List<SavedMapEntry> maps)
    {
        System.IO.File.WriteAllText(SavedMapsPath, JsonSerializer.Serialize(maps, JsonOpts));
    }

    private static void AddToSavedMaps(string path)
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

    [HttpGet("maps/saved")]
    public IActionResult GetSavedMaps()
    {
        var maps = ReadSavedMaps();

        // Enrich with file size and existence check
        foreach (var m in maps)
        {
            try
            {
                if (System.IO.File.Exists(m.Path))
                {
                    m.Exists = true;
                    m.SizeMB = Math.Round(new System.IO.FileInfo(m.Path).Length / 1048576.0, 1);
                }
                else
                {
                    m.Exists = false;
                }
            }
            catch
            {
                m.Exists = false;
            }
        }

        return Ok(maps);
    }

    [HttpPost("maps/saved")]
    public IActionResult AddSavedMap([FromBody] AddSavedMapRequest request)
    {
        if (string.IsNullOrEmpty(request.Path))
            return BadRequest(new { error = "No path provided" });

        if (!System.IO.File.Exists(request.Path))
            return BadRequest(new { error = $"File not found: {request.Path}" });

        AddToSavedMaps(request.Path);
        return Ok(new { status = "saved" });
    }

    [HttpDelete("maps/saved")]
    public IActionResult RemoveSavedMap([FromQuery] string path)
    {
        var maps = ReadSavedMaps();
        maps.RemoveAll(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
        WriteSavedMaps(maps);
        return Ok(new { status = "removed" });
    }

    [HttpPost("maps/saved/load")]
    public async Task<IActionResult> LoadSavedMap([FromBody] AddSavedMapRequest request)
    {
        if (string.IsNullOrEmpty(request.Path))
            return BadRequest(new { error = "No path provided" });

        try
        {
            if (request.Path.EndsWith(".routerdb", StringComparison.OrdinalIgnoreCase))
                await _routingService.LoadCacheAsync(request.Path);
            else
                await _routingService.LoadMapAsync(request.Path, request.AvoidHighways);

            AddToSavedMaps(request.Path);

            return Ok(new
            {
                status = "loaded",
                loadedMaps = _routingService.LoadedMaps,
                cachePath = _routingService.CachePath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load saved map");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("routes/generate")]
    public async Task<IActionResult> GenerateRoute([FromBody] RouteRequest request)
    {
        try
        {
            var route = await _routingService.GenerateLoopRouteAsync(request);
            return Ok(route);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate route");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("routes/generate-candidates")]
    public async Task<IActionResult> GenerateRouteCandidates([FromBody] GenerateCandidatesRequest request)
    {
        try
        {
            var cachePath = _routingService.CachePath;
            if (string.IsNullOrEmpty(cachePath))
                return BadRequest(new { error = "No map loaded. Load a map first." });

            var poolSize = request.CandidateCount > 0 ? request.CandidateCount : 4;
            using var pool = new RouterDbPool(cachePath, poolSize);
            await pool.WarmUpAsync(msg => _logger.LogInformation(msg));

            _routingService.TouchIdleTimer();

            var (best, allCandidates) = RoutingService.GenerateLoopRouteCandidates(
                request.RouteRequest, pool, poolSize);

            return Ok(new
            {
                best,
                candidates = allCandidates,
                candidateCount = allCandidates.Length
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate route candidates");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("export/gpx")]
    public IActionResult ExportGpx([FromBody] ExportGpxRequest request)
    {
        if (request.RouteGeometry is not { Count: > 0 })
            return BadRequest(new { error = "No route geometry provided" });

        try
        {
            var gpx = _exportService.GenerateGpx(
                request.RouteGeometry,
                request.Start ?? request.RouteGeometry[0],
                request.Waypoints ?? new List<Coordinate>(),
                request.RouteName ?? "Moto Route");

            return Ok(new { gpx });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate GPX");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("export/gpx/download")]
    public IActionResult ExportGpxDownload([FromBody] ExportGpxRequest request)
    {
        if (request.RouteGeometry is not { Count: > 0 })
            return BadRequest(new { error = "No route geometry provided" });

        try
        {
            var gpx = _exportService.GenerateGpx(
                request.RouteGeometry,
                request.Start ?? request.RouteGeometry[0],
                request.Waypoints ?? new List<Coordinate>(),
                request.RouteName ?? "Moto Route");

            var filename = _exportService.GetGpxFilename(request.RouteName ?? "moto-route");
            var bytes = System.Text.Encoding.UTF8.GetBytes(gpx);
            return File(bytes, "application/gpx+xml", filename);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate GPX download");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("export/google-maps")]
    public IActionResult ExportGoogleMaps([FromBody] ExportGoogleMapsRequest request)
    {
        if (request.RouteGeometry is not { Count: > 0 })
            return BadRequest(new { error = "No route geometry provided" });

        try
        {
            var url = _exportService.GenerateGoogleMapsUrl(
                request.Start ?? request.RouteGeometry[0],
                request.RouteGeometry);

            return Ok(new { url });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Google Maps URL");
            return BadRequest(new { error = ex.Message });
        }
    }
}

// Request DTOs

public class LoadMapRequest
{
    public string[] Paths { get; set; } = Array.Empty<string>();
    public bool AvoidHighways { get; set; } = true;
}

public class LoadServerMapRequest
{
    public string ServerPath { get; set; } = "";
    public bool AvoidHighways { get; set; } = true;
}

public class GenerateCandidatesRequest
{
    public RouteRequest RouteRequest { get; set; } = new();
    public int CandidateCount { get; set; } = 4;
}

public class ExportGpxRequest
{
    public List<Coordinate>? RouteGeometry { get; set; }
    public Coordinate? Start { get; set; }
    public List<Coordinate>? Waypoints { get; set; }
    public string? RouteName { get; set; }
}

public class ExportGoogleMapsRequest
{
    public List<Coordinate>? RouteGeometry { get; set; }
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
