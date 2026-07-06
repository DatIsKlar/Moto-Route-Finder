using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MotoRouteFinder.Models;
using MotoRouteFinder.Server.Services;
using MotoRouteFinder.Services;

namespace MotoRouteFinder.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RouteController : ControllerBase
{
    private readonly RoutingService _routingService;
    private readonly ExportService _exportService;
    private readonly SavedMapsService _savedMapsService;
    private readonly IOptions<RouteGenerationOptions> _options;
    private readonly ILogger<RouteController> _logger;

    private const double BytesToMB = 1048576.0;

    public RouteController(RoutingService routingService, ExportService exportService, SavedMapsService savedMapsService, IOptions<RouteGenerationOptions> options, ILogger<RouteController> logger)
    {
        _routingService = routingService;
        _exportService = exportService;
        _savedMapsService = savedMapsService;
        _options = options;
        _logger = logger;
    }

    private static IActionResult ErrorResponse(string error, string? details = null, int status = 400)
    {
        return new ObjectResult(new { error, details }) { StatusCode = status };
    }

    private static bool IsDebugEndpointsEnabled()
    {
        return string.Equals(Environment.GetEnvironmentVariable("ENABLE_DEBUG_ENDPOINTS"), "true", StringComparison.OrdinalIgnoreCase);
    }

    // H1: Path confinement — resolve the root directory for map files
    private static string GetMapsRoot()
    {
        return Path.GetFullPath(Environment.GetEnvironmentVariable("MAPS_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "uploads"));
    }

    // H1: Path confinement — reject any path that doesn't resolve under the maps root
    private static bool IsPathAllowed(string path)
    {
        var root = GetMapsRoot();
        var resolved = Path.GetFullPath(path);
        // Ensure trailing separator so /data/maps-secret doesn't match /data/maps
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return resolved == root || resolved.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }

    // H1: Map file extension allowlist (case-insensitive)
    private static bool IsMapExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".osm" or ".pbf" or ".routerdb";
    }

    private async Task<IActionResult> LoadMapByPath(string path, bool avoidHighways, bool addToSaved = false)
    {
        // Loading is restricted to map file types (allowlist), but not confined to MAPS_DIR:
        // this is a single-user tool that loads the user's own maps from arbitrary paths.
        // Directory browsing (/maps/browse) stays confined — see IsPathAllowed usage there.
        if (!IsMapExtension(path))
            return ErrorResponse("Access denied: file is not a supported map format", status: 403);

        try
        {
            if (path.EndsWith(".routerdb", StringComparison.OrdinalIgnoreCase))
                await _routingService.LoadCacheAsync(path);
            else
                await _routingService.LoadMapAsync(path, avoidHighways);

            if (addToSaved)
                _savedMapsService.AddToSavedMaps(path);

            return Ok(new
            {
                status = "loaded",
                loadedMaps = _routingService.LoadedMaps,
                cachePath = _routingService.CachePath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load map from {Path}", path);
            return ErrorResponse("Failed to load map. Check that the file exists and is a valid map format.");
        }
    }

    private string GenerateGpxContent(ExportGpxRequest request)
    {
        return _exportService.GenerateGpx(
            request.RouteGeometry,
            request.Start ?? request.RouteGeometry[0],
            request.Waypoints ?? new List<Coordinate>(),
            request.RouteName ?? "Moto Route");
    }

    [HttpGet("maps/status")]
    public IActionResult GetMapStatus()
    {
        return Ok(new
        {
            isLoaded = _routingService.IsLoaded,
            loadedMaps = _routingService.LoadedMaps,
            cachePath = _routingService.CachePath,
            memoryMB = Math.Round(GC.GetTotalMemory(false) / BytesToMB, 1)
        });
    }

    [HttpPost("maps/unload")]
    public IActionResult UnloadMap()
    {
        _routingService.ClearMaps();
        var memMB = Math.Round(GC.GetTotalMemory(false) / BytesToMB, 1);
        return Ok(new
        {
            status = "unloaded",
            memoryMB = memMB
        });
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "healthy",
            mapLoaded = _routingService.IsLoaded,
            memoryMb = Math.Round(GC.GetTotalMemory(false) / BytesToMB, 1),
            uptime = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"hh\:mm\:ss")
        });
    }

    [HttpGet("heartbeat")]
    [HttpPost("heartbeat")]
    public IActionResult Heartbeat()
    {
        _routingService.Heartbeat();
        return Ok(new { status = "ok" });
    }

    [HttpPost("maps/load")]
    public async Task<IActionResult> LoadMap([FromBody] LoadMapRequest request)
    {
        if (request.Paths == null || request.Paths.Length == 0)
            return ErrorResponse("No file paths provided");

        if (request.Paths.Length == 1)
            return await LoadMapByPath(request.Paths[0], request.AvoidHighways);

        try
        {
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
            _logger.LogError(ex, "Failed to load maps");
            return ErrorResponse("Failed to load maps. Check that the files exist and are valid map formats.");
        }
    }

    [HttpPost("maps/load-server")]
    public async Task<IActionResult> LoadMapFromServer([FromBody] LoadServerMapRequest request)
    {
        if (string.IsNullOrEmpty(request.ServerPath))
            return ErrorResponse("No server path provided");

        return await LoadMapByPath(request.ServerPath, request.AvoidHighways, addToSaved: true);
    }

    [HttpPost("maps/upload")]
    public async Task<IActionResult> UploadMap(IFormFile[] files, [FromQuery] bool avoidHighways = true)
    {
        if (!ModelState.IsValid)
        {
            var errors = string.Join("; ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            _logger.LogWarning("ModelState errors: {Errors}", errors);
            return ErrorResponse("ModelState invalid", details: errors);
        }

        if (files == null || files.Length == 0)
            return ErrorResponse("No files uploaded");

        var uploadsDir = Environment.GetEnvironmentVariable("MAPS_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "uploads");
        Directory.CreateDirectory(uploadsDir);

        var filePaths = new List<string>();
        foreach (var file in files)
        {
            // H2: Sanitize filename to prevent path traversal; validate extension
            var safeName = Path.GetFileName(file.FileName);
            if (string.IsNullOrEmpty(safeName) || !IsMapExtension(safeName))
                return ErrorResponse($"Invalid file: {file.FileName} (must be .osm, .pbf, or .routerdb)");

            var filePath = Path.Combine(uploadsDir, safeName);
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }
            filePaths.Add(filePath);
        }

        if (filePaths.Count == 1)
            return await LoadMapByPath(filePaths[0], avoidHighways);

        try
        {
            await _routingService.LoadMapsAsync(filePaths.ToArray(), avoidHighways);
            return Ok(new
            {
                status = "loaded",
                loadedMaps = _routingService.LoadedMaps,
                cachePath = _routingService.CachePath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load uploaded maps");
            return ErrorResponse("Failed to load uploaded map. The file may be corrupted or in an unsupported format.");
        }
    }

    [HttpGet("maps/browse")]
    public IActionResult BrowseDirectory([FromQuery] string? path = null)
    {
        try
        {
            var mapsRoot = GetMapsRoot();

            var targetPath = string.IsNullOrEmpty(path)
                ? mapsRoot
                : path;

            // H1: Directory browsing stays confined to the maps root (enumeration is the real recon vector)
            targetPath = Path.GetFullPath(targetPath);
            if (!IsPathAllowed(targetPath))
                return ErrorResponse("Access denied: path is outside the maps directory", status: 403);

            if (!Directory.Exists(targetPath))
                return ErrorResponse($"Directory not found: {targetPath}");

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
                    sizeMB = Math.Round(info.Length / BytesToMB, 1)
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
            return ErrorResponse("Failed to browse directory.");
        }
    }

    [HttpPost("maps/clear")]
    public IActionResult ClearMaps()
    {
        _routingService.ClearMaps();
        return Ok(new { status = "cleared" });
    }

    // --- Saved Maps persistence ---

    [HttpGet("maps/saved")]
    public IActionResult GetSavedMaps()
    {
        var maps = _savedMapsService.ReadSavedMaps();

        // Enrich with file size and existence check
        foreach (var m in maps)
        {
            try
            {
                if (System.IO.File.Exists(m.Path))
                {
                    m.Exists = true;
                    m.SizeMB = Math.Round(new System.IO.FileInfo(m.Path).Length / BytesToMB, 1);
                }
                else
                {
                    m.Exists = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to check file existence for saved map {Path}", m.Path);
                m.Exists = false;
            }
        }

        return Ok(maps);
    }

    [HttpPost("maps/saved")]
    public IActionResult AddSavedMap([FromBody] AddSavedMapRequest request)
    {
        if (string.IsNullOrEmpty(request.Path))
            return ErrorResponse("No path provided");

        // Allowlist map file types, but do not confine to MAPS_DIR (user's own maps live anywhere).
        if (!IsMapExtension(request.Path))
            return ErrorResponse("Access denied: file is not a supported map format", status: 403);

        if (!System.IO.File.Exists(request.Path))
            return ErrorResponse($"File not found: {request.Path}");

        _savedMapsService.AddToSavedMaps(request.Path);
        return Ok(new { status = "saved" });
    }

    [HttpDelete("maps/saved")]
    public IActionResult RemoveSavedMap([FromQuery] string path)
    {
        if (string.IsNullOrEmpty(path))
            return ErrorResponse("No path provided");

        // Removing a saved-map list entry only edits saved_maps.json — it deletes no files,
        // so path confinement is unnecessary here and would block cleanup of stale out-of-root entries.
        _savedMapsService.RemoveFromSavedMaps(path);
        return Ok(new { status = "removed" });
    }

    [HttpPost("maps/saved/load")]
    public async Task<IActionResult> LoadSavedMap([FromBody] AddSavedMapRequest request)
    {
        if (string.IsNullOrEmpty(request.Path))
            return ErrorResponse("No path provided");

        return await LoadMapByPath(request.Path, request.AvoidHighways, addToSaved: true);
    }

    [HttpPost("routes/generate")]
    public async Task<IActionResult> GenerateRoute([FromBody] RouteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var route = await _routingService.GenerateLoopRouteAsync(request, cancellationToken);
            return Ok(route);
        }
        catch (OperationCanceledException)
        {
            return Ok(new { status = "cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate route");
            return ErrorResponse(ex.Message);
        }
    }

    [HttpPost("routes/generate-candidates")]
    public async Task<IActionResult> GenerateRouteCandidates([FromBody] GenerateCandidatesRequest request, CancellationToken cancellationToken)
    {
        try
        {
            request.CandidateCount = Math.Clamp(request.CandidateCount, 1, 8);

            var cachePath = _routingService.CachePath;
            if (string.IsNullOrEmpty(cachePath))
                return ErrorResponse("No map loaded. Load a map first.");

            var poolSize = request.CandidateCount > 0 ? request.CandidateCount : 4;

            // Ensure singleton is loaded before creating pool (prevents pool/singleton divergence)
            await _routingService.EnsureMapLoadedAsync();

            var (best, allCandidates) = await _routingService.GenerateCandidatesAsync(
                request.RouteRequest, poolSize, _options, cancellationToken);

            return Ok(new
            {
                best,
                candidates = allCandidates,
                candidateCount = allCandidates.Length
            });
        }
        catch (OperationCanceledException)
        {
            return Ok(new { status = "cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate route candidates");
            return ErrorResponse(ex.Message);
        }
    }

    [HttpPost("export/gpx")]
    public IActionResult ExportGpx([FromBody] ExportGpxRequest request)
    {
        if (request.RouteGeometry is not { Count: > 0 })
            return ErrorResponse("No route geometry provided");

        try
        {
            var gpx = GenerateGpxContent(request);
            return Ok(new { gpx });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate GPX");
            return ErrorResponse(ex.Message);
        }
    }

    [HttpPost("export/gpx/download")]
    public IActionResult ExportGpxDownload([FromBody] ExportGpxRequest request)
    {
        if (request.RouteGeometry is not { Count: > 0 })
            return ErrorResponse("No route geometry provided");

        try
        {
            var gpx = GenerateGpxContent(request);
            var filename = _exportService.GetGpxFilename(request.RouteName ?? "moto-route");
            var bytes = System.Text.Encoding.UTF8.GetBytes(gpx);
            return File(bytes, "application/gpx+xml", filename);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate GPX download");
            return ErrorResponse(ex.Message);
        }
    }

    [HttpPost("export/google-maps")]
    public IActionResult ExportGoogleMaps([FromBody] ExportGoogleMapsRequest request)
    {
        if (request.RouteGeometry is not { Count: > 0 })
            return ErrorResponse("No route geometry provided");

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
            return ErrorResponse(ex.Message);
        }
    }

    private static readonly DirectionBias[] AllDirectionBiases = [
        DirectionBias.Any, DirectionBias.North, DirectionBias.South,
        DirectionBias.East, DirectionBias.West,
        DirectionBias.Northeast, DirectionBias.Southeast,
        DirectionBias.Southwest, DirectionBias.Northwest
    ];

    private static double GenerateRandomDistance(double? previousDistanceKm)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            double distance = Random.Shared.Next(50, 201);
            if (!previousDistanceKm.HasValue || Math.Abs(distance - previousDistanceKm.Value) >= 25)
                return distance;
        }
        return Random.Shared.Next(50, 201);
    }

    [HttpPost("routes/test-run")]
    public async Task<IActionResult> RunTest([FromBody] TestRunRequest request, CancellationToken cancellationToken)
    {
        if (!IsDebugEndpointsEnabled())
            return NotFound();

        try
        {
            var cachePath = _routingService.CachePath;
            if (string.IsNullOrEmpty(cachePath))
                return ErrorResponse("No map loaded. Load a map first.");

            var testCount = request.TestCount > 0 ? request.TestCount : 5;
            var candidateCount = request.CandidateCount > 0 ? request.CandidateCount : 4;

            // Ensure singleton is loaded before creating pool (prevents pool/singleton divergence)
            await _routingService.EnsureMapLoadedAsync();

            var testRunDir = Path.Combine(
                Environment.GetEnvironmentVariable("MAPS_DIR") ?? AppContext.BaseDirectory,
                "stem_diagnostics", $"test_run_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(testRunDir);

            _logger.LogInformation("Test run starting: {Count} routes x {Candidates} candidates -> {Dir}",
                testCount, candidateCount, testRunDir);

            var results = new List<object>();
            double? previousDistanceKm = null;
            double totalQuality = 0;
            int validCount = 0;

            var routeRequest = request.RouteRequest ?? new RouteRequest();

            for (int i = 0; i < testCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                double targetDist = GenerateRandomDistance(previousDistanceKm);
                previousDistanceKm = targetDist;

                int wpCount = routeRequest.WaypointCount > 0 ? routeRequest.WaypointCount : 5;
                var bias = AllDirectionBiases[i % AllDirectionBiases.Length];

                var testRequest = new RouteRequest
                {
                    Start = routeRequest.Start,
                    Waypoints = routeRequest.Waypoints,
                    TargetDistanceKm = targetDist,
                    TargetDurationMin = null,
                    AvoidHighways = routeRequest.AvoidHighways,
                    WaypointCount = wpCount,
                    Direction = bias,
                };

                var (best, candidates) = await _routingService.GenerateCandidatesAsync(
                    testRequest, candidateCount, _options, cancellationToken);

                for (int c = 0; c < candidates.Length; c++)
                {
                    if (candidates[c]?.StemDiagnosticsJson != null)
                    {
                        var diagPath = Path.Combine(testRunDir, $"diagnostics_{i + 1:D3}_c{c}.json");
                        await System.IO.File.WriteAllTextAsync(diagPath, candidates[c]!.StemDiagnosticsJson, cancellationToken);
                    }
                }

                var quality = best?.Stats?.QualityScore ?? 0;
                var distance = best?.Stats?.TotalDistanceKm ?? 0;
                var repetition = best?.Stats?.RepetitionRatio ?? 0;

                totalQuality += quality;
                validCount++;

                results.Add(new
                {
                    index = i + 1,
                    qualityScore = Math.Round(quality, 1),
                    distanceKm = Math.Round(distance, 1),
                    repetitionPct = Math.Round(repetition * 100, 1),
                    direction = bias.ToString(),
                    candidates = candidateCount
                });

                _logger.LogInformation("Test route {Index}/{Total}: quality={Quality:F1}, distance={Distance:F1}km, repetition={Repetition:P1}",
                    i + 1, testCount, quality, distance, repetition);
            }

            var totalFiles = testCount * candidateCount;
            var avgQuality = validCount > 0 ? totalQuality / validCount : 0;

            return Ok(new
            {
                testRunDir,
                totalRoutes = testCount,
                candidatesPerRoute = candidateCount,
                totalFiles,
                avgQuality = Math.Round(avgQuality, 1),
                routes = results
            });
        }
        catch (OperationCanceledException)
        {
            return Ok(new { status = "cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run test");
            return ErrorResponse(ex.Message);
        }
    }

    // TEMPORARY: Step 3 probe — verify edge walk IDs block motorways correctly.
    // Remove after verification is complete.
    [HttpPost("probe-edge-walk")]
    public IActionResult ProbeEdgeWalk()
    {
        if (!IsDebugEndpointsEnabled())
            return NotFound();

        if (!_routingService.IsLoaded)
            return ErrorResponse("No map loaded", status: 400);

        try
        {
            var result = _routingService.ProbeMotorwayEdgeWalk();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Edge walk probe failed");
            return ErrorResponse(ex.Message);
        }
    }
}
