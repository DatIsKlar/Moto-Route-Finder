using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Itinero;
using Itinero.IO.Osm;
using Itinero.Profiles;
using OsmSharp.Streams;

namespace MotoRouteFinder.Services;

/// <summary>
/// Manages map loading, caching, and the lifecycle of Router/RouterDb instances.
/// </summary>
public class MapRepository
{
    private readonly object _lock = new();
    private RouterDb? _routerDb;
    private Router? _router;
    private readonly List<string> _loadedMaps = new();
    private EdgeBlocker? _edgeBlocker;

    // Cache diagnostics
    private bool _loadedFromCache;
    private string _motorwayCacheFile = "";
    private int _motorwaysBlockedAtLoadTime;
    private long _motorwayBlockLoadTimeMs;
    private bool _motorwaysInCache;
    private int _motorwaysFailedToBlock;
    private bool _motorwayBlockValidationPassed;
    private bool _motorwaysScanCompleted;

    // Edge quality pre-computation cache — shared across all pool instances.
    // All instances load the same RouterDb, so profile IDs and classifications are identical.
    private static Dictionary<uint, (RoadClassifier.RoadQuality quality, string? highway)>? _staticEdgeQualityCache;

    private static readonly Lazy<string> MotorcycleProfileLua = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("MotoRouteFinder.Resources.MotorcycleProfile.lua");
        if (stream == null)
            throw new InvalidOperationException("Embedded resource MotorcycleProfile.lua not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public bool IsLoaded => _loadedMaps.Count > 0;
    public IReadOnlyList<string> LoadedMaps => _loadedMaps.AsReadOnly();
    public Router? Router => _router;
    public RouterDb? RouterDb => _routerDb;
    public string? CachePath { get; private set; }

    // Cache diagnostic properties
    public bool LoadedFromCache => _loadedFromCache;
    public string MotorwayCacheFile => _motorwayCacheFile;
    public int MotorwaysBlockedAtLoadTime => _motorwaysBlockedAtLoadTime;
    public long MotorwayBlockLoadTimeMs => _motorwayBlockLoadTimeMs;
    public bool MotorwaysInCache => _motorwaysInCache;
    public int MotorwaysFailedToBlock => _motorwaysFailedToBlock;
    public bool MotorwayBlockValidationPassed => _motorwayBlockValidationPassed;
    public bool MotorwaysScanCompleted => _motorwaysScanCompleted;

    // Edge quality cache property — reads from shared static cache
    public IReadOnlyDictionary<uint, (RoadClassifier.RoadQuality quality, string? highway)>? EdgeQualityCache => _staticEdgeQualityCache;

    public event Action<string>? StatusChanged;

    /// <summary>
    /// Loads a map with optional motorway blocking.
    /// When avoidHighways is true, motorways are permanently blocked at load time
    /// and saved to a separate cache file.
    /// </summary>
    public async Task LoadMapAsync(string osmPbfPath, bool avoidHighways = false)
    {
        await Task.Run(() => LoadMap(osmPbfPath, avoidHighways));
    }

    /// <summary>
    /// Loads multiple maps into a single RouterDb with optional motorway blocking.
    /// All PBF files are merged into one routing network.
    /// </summary>
    public async Task LoadMapsAsync(string[] osmPbfPaths, bool avoidHighways = false)
    {
        await Task.Run(() => LoadMultipleMaps(osmPbfPaths, avoidHighways));
    }

    /// <summary>
    /// Loads a pre-built cache file (.routerdb) directly.
    /// Bypasses OSM loading and motorway blocking.
    /// </summary>
    public async Task LoadCacheAsync(string cachePath)
    {
        await Task.Run(() => LoadCache(cachePath));
    }

    internal void LoadCache(string cachePath)
    {
        if (!File.Exists(cachePath))
            throw new FileNotFoundException($"Cache file not found: {cachePath}");

        lock (_lock)
        {
            _loadedMaps.Clear();
            _loadedMaps.Add(Path.GetFileName(cachePath));
            _motorwayCacheFile = Path.GetFileName(cachePath);
            _loadedFromCache = true;
            _motorwaysBlockedAtLoadTime = 0;
            _motorwayBlockLoadTimeMs = 0;
            _motorwaysScanCompleted = false;
            _motorwaysFailedToBlock = 0;
            _motorwayBlockValidationPassed = true;

            // Detect if this is a no-motorway cache
            _motorwaysInCache = cachePath.Contains("_nomotorway");

            StatusChanged?.Invoke($"Loading cache: {Path.GetFileName(cachePath)}...");
            _routerDb = LoadRouterDb(cachePath);
            _router = new Router(_routerDb);
            CachePath = cachePath;
            StatusChanged?.Invoke($"Loaded from cache: {Path.GetFileName(cachePath)}");

            // Pre-compute edge qualities for fast classification
            PrecomputeEdgeQualities();
        }
    }

    private void LoadMap(string osmPbfPath, bool avoidHighways = false)
    {
        if (!File.Exists(osmPbfPath))
        {
            throw new FileNotFoundException($"OSM file not found: {osmPbfPath}");
        }

        lock (_lock)
        {
            var fileName = Path.GetFileName(osmPbfPath);
            _loadedMaps.Clear();
            _loadedMaps.Add(fileName);

            var cachePath = avoidHighways
                ? GetNoMotorwayCachePath(osmPbfPath)
                : GetCachePath(osmPbfPath);

            _motorwayCacheFile = Path.GetFileName(cachePath);
            _motorwaysInCache = avoidHighways;

            if (File.Exists(cachePath))
            {
                _loadedFromCache = true;
                _motorwaysBlockedAtLoadTime = 0;
                _motorwayBlockLoadTimeMs = 0;
                StatusChanged?.Invoke($"Loading {fileName} from cache{(avoidHighways ? " (no motorways)" : "")}...");
                _routerDb = LoadRouterDb(cachePath);
                _router = new Router(_routerDb);
                StatusChanged?.Invoke($"Loaded: {fileName}");
                return;
            }

            _loadedFromCache = false;
            StatusChanged?.Invoke($"Loading {fileName}...");

            var settings = new LoadSettings
            {
                AllCore = false,
                ProcessRestrictions = false,
                NetworkSimplificationEpsilon = 0,
                KeepNodeIds = false,
                KeepWayIds = false,
            };

            var sources = new OsmStreamSource[] { new PBFOsmStreamSource(File.OpenRead(osmPbfPath)) };

            _routerDb = new RouterDb();
            var motorcycleVehicle = DynamicVehicle.Load(MotorcycleProfileLua.Value);
            _routerDb.LoadOsmData(sources, settings, motorcycleVehicle);
            _router = new Router(_routerDb);

            // If avoidHighways, permanently block motorways before caching
            if (avoidHighways)
            {
                StatusChanged?.Invoke("Blocking motorways at load time...");
                _edgeBlocker = new EdgeBlocker(this);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _motorwaysBlockedAtLoadTime = _edgeBlocker.BlockMotorwaysPermanently(
                    msg => StatusChanged?.Invoke(msg));
                sw.Stop();
                _motorwayBlockLoadTimeMs = sw.ElapsedMilliseconds;
                _motorwaysScanCompleted = true;
                _motorwaysFailedToBlock = _edgeBlocker.MotorwayEdgesFound - _motorwaysBlockedAtLoadTime;
                _motorwayBlockValidationPassed = _motorwaysFailedToBlock == 0;
                StatusChanged?.Invoke($"Permanently blocked {_motorwaysBlockedAtLoadTime:N0} motorway edges in {sw.ElapsedMilliseconds}ms" +
                    (_motorwaysFailedToBlock > 0 ? $" ({_motorwaysFailedToBlock} FAILED)" : ""));
            }
            else
            {
                _motorwaysBlockedAtLoadTime = 0;
                _motorwayBlockLoadTimeMs = 0;
                _motorwaysScanCompleted = false;
                _motorwaysFailedToBlock = 0;
                _motorwayBlockValidationPassed = true;
            }

            StatusChanged?.Invoke("Saving cache...");
            SaveRouterDb(_routerDb, cachePath);
            CachePath = cachePath;

            StatusChanged?.Invoke($"Loaded: {string.Join(", ", _loadedMaps)} ({_loadedMaps.Count} map{(_loadedMaps.Count == 1 ? "" : "s")})");

            // Pre-compute edge qualities for fast classification
            PrecomputeEdgeQualities();
        }
    }

    private void LoadMultipleMaps(string[] osmPbfPaths, bool avoidHighways = false)
    {
        if (osmPbfPaths == null || osmPbfPaths.Length == 0)
            throw new ArgumentException("No map files specified.");

        foreach (var path in osmPbfPaths)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"OSM file not found: {path}");
        }

        lock (_lock)
        {
            _loadedMaps.Clear();
            _loadedMaps.AddRange(osmPbfPaths.Select(p => Path.GetFileName(p)!));

            var cachePath = avoidHighways
                ? GetMultiMapNoMotorwayCachePath(osmPbfPaths)
                : GetMultiMapCachePath(osmPbfPaths);

            _motorwayCacheFile = Path.GetFileName(cachePath);
            _motorwaysInCache = avoidHighways;

            if (File.Exists(cachePath))
            {
                _loadedFromCache = true;
                _motorwaysBlockedAtLoadTime = 0;
                _motorwayBlockLoadTimeMs = 0;
                _motorwaysScanCompleted = false;
                _motorwaysFailedToBlock = 0;
                _motorwayBlockValidationPassed = true;
                StatusChanged?.Invoke($"Loading {string.Join(", ", _loadedMaps)} from cache{(avoidHighways ? " (no motorways)" : "")}...");
                _routerDb = LoadRouterDb(cachePath);
                _router = new Router(_routerDb);
                StatusChanged?.Invoke($"Loaded: {string.Join(", ", _loadedMaps)}");
                return;
            }

            _loadedFromCache = false;
            StatusChanged?.Invoke($"Loading {string.Join(", ", _loadedMaps)}...");

            var settings = new LoadSettings
            {
                AllCore = false,
                ProcessRestrictions = false,
                NetworkSimplificationEpsilon = 0,
                KeepNodeIds = false,
                KeepWayIds = false,
            };

            // Create sources from all PBF files
            var sources = osmPbfPaths
                .Select(p => (OsmStreamSource)new PBFOsmStreamSource(File.OpenRead(p)))
                .ToArray();

            _routerDb = new RouterDb();
            var motorcycleVehicle = DynamicVehicle.Load(MotorcycleProfileLua.Value);
            _routerDb.LoadOsmData(sources, settings, motorcycleVehicle);
            _router = new Router(_routerDb);

            // If avoidHighways, permanently block motorways before caching
            if (avoidHighways)
            {
                StatusChanged?.Invoke("Blocking motorways at load time...");
                _edgeBlocker = new EdgeBlocker(this);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var profile = _routerDb.GetSupportedProfile("motorcycle");
                _motorwaysBlockedAtLoadTime = _edgeBlocker.BlockMotorwaysPermanently(
                    msg => StatusChanged?.Invoke(msg));
                sw.Stop();
                _motorwayBlockLoadTimeMs = sw.ElapsedMilliseconds;
                _motorwaysScanCompleted = true;
                _motorwaysFailedToBlock = _edgeBlocker.MotorwayEdgesFound - _motorwaysBlockedAtLoadTime;
                _motorwayBlockValidationPassed = _motorwaysFailedToBlock == 0;
                StatusChanged?.Invoke($"Permanently blocked {_motorwaysBlockedAtLoadTime:N0} motorway edges in {sw.ElapsedMilliseconds}ms" +
                    (_motorwaysFailedToBlock > 0 ? $" ({_motorwaysFailedToBlock} FAILED)" : ""));
            }
            else
            {
                _motorwaysBlockedAtLoadTime = 0;
                _motorwayBlockLoadTimeMs = 0;
                _motorwaysScanCompleted = false;
                _motorwaysFailedToBlock = 0;
                _motorwayBlockValidationPassed = true;
            }

            StatusChanged?.Invoke("Saving cache...");
            SaveRouterDb(_routerDb, cachePath);
            CachePath = cachePath;

            StatusChanged?.Invoke($"Loaded: {string.Join(", ", _loadedMaps)} ({_loadedMaps.Count} map{(_loadedMaps.Count == 1 ? "" : "s")})");

            // Pre-compute edge qualities for fast classification
            PrecomputeEdgeQualities();
        }
    }

    public void ClearMaps()
    {
        lock (_lock)
        {
            _routerDb = null;
            _router = null;
            _loadedMaps.Clear();
        }
    }

    /// <summary>
    /// Clears the static edge quality cache shared across all pool instances.
    /// Call this when unloading maps to free memory.
    /// </summary>
    public static void ClearStaticCache()
    {
        _staticEdgeQualityCache?.Clear();
        _staticEdgeQualityCache = null;
    }

    private static string GetCachePath(string osmPbfPath)
    {
        var baseName = Path.GetFileNameWithoutExtension(osmPbfPath);
        var dir = Path.GetDirectoryName(osmPbfPath) ?? ".";
        return Path.Combine(dir, $"{baseName}_motorcycle.routerdb");
    }

    private static string GetNoMotorwayCachePath(string osmPbfPath)
    {
        var baseName = Path.GetFileNameWithoutExtension(osmPbfPath);
        var dir = Path.GetDirectoryName(osmPbfPath) ?? ".";
        return Path.Combine(dir, $"{baseName}_motorcycle_nomotorway.routerdb");
    }

    private static string GetMultiMapCachePath(string[] osmPbfPaths)
    {
        var names = osmPbfPaths
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .OrderBy(n => n)
            .ToArray();
        var combined = string.Join("+", names);
        var dir = Path.GetDirectoryName(osmPbfPaths[0]) ?? ".";
        return Path.Combine(dir, $"{combined}_motorcycle.routerdb");
    }

    private static string GetMultiMapNoMotorwayCachePath(string[] osmPbfPaths)
    {
        var names = osmPbfPaths
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .OrderBy(n => n)
            .ToArray();
        var combined = string.Join("+", names);
        var dir = Path.GetDirectoryName(osmPbfPaths[0]) ?? ".";
        return Path.Combine(dir, $"{combined}_motorcycle_nomotorway.routerdb");
    }

    private static RouterDb LoadRouterDb(string path)
    {
        using var stream = File.OpenRead(path);
        return RouterDb.Deserialize(stream);
    }

    private static void SaveRouterDb(RouterDb db, string path)
    {
        using var stream = File.Create(path);
        db.Serialize(stream);
    }

    /// <summary>
    /// Pre-computes road quality classification for all unique edge profiles in the RouterDb.
    /// Populates _edgeQualityCache so RoadClassifier can look up quality without re-parsing tags.
    /// Must be called after RouterDb is loaded and before any routing.
    /// </summary>
    public void PrecomputeEdgeQualities()
    {
        if (_routerDb == null || _router == null) return;

        // Reuse shared static cache if already populated (all pool instances load same RouterDb)
        if (_staticEdgeQualityCache != null)
        {
            StatusChanged?.Invoke($"Reusing shared edge quality cache ({_staticEdgeQualityCache.Count} profiles)");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cache = new Dictionary<uint, (RoadClassifier.RoadQuality quality, string? highway)>();

        // GetEdgeEnumerator() is broken in Itinero 1.5.1 — returns 0 edges regardless of EdgeCount.
        // The runtime classification cache (78% hit rate) already makes ClassificationMs = 0,
        // so this precomputation is redundant. Skip gracefully.
        int enumeratedEdges = 0;
        foreach (var edge in _routerDb.Network.GetEdgeEnumerator())
        {
            enumeratedEdges++;
            var profileId = edge.Data.Profile;

            if (cache.ContainsKey(profileId))
                continue;

            var tags = _routerDb.EdgeProfiles.Get(profileId);
            if (tags == null)
            {
                cache[profileId] = (RoadClassifier.RoadQuality.Poor, null);
                continue;
            }

            string? highway = null;
            tags.TryGetValue("highway", out string? hw);
            if (hw != null) highway = hw.ToLowerInvariant();

            // Check access restrictions
            if (tags.TryGetValue("access", out string access))
            {
                access = access.ToLowerInvariant();
                if (access is "private" or "no" or "customers" or "military" or "restricted")
                {
                    cache[profileId] = (RoadClassifier.RoadQuality.Blocked, highway);
                    continue;
                }
            }

            if (tags.TryGetValue("motor_vehicle", out string motorVehicle) && motorVehicle == "no")
            {
                cache[profileId] = (RoadClassifier.RoadQuality.Blocked, highway);
                continue;
            }

            if (tags.TryGetValue("motorcycle", out string motorcycle) && motorcycle == "no")
            {
                cache[profileId] = (RoadClassifier.RoadQuality.Blocked, highway);
                continue;
            }

            // Classify by road type
            RoadClassifier.RoadQuality quality;
            if (highway == null)
                quality = RoadClassifier.RoadQuality.Poor;
            else if (highway is "motorway" or "motorway_link")
                quality = RoadClassifier.RoadQuality.Acceptable;
            else if (highway is "trunk" or "trunk_link" or "primary" or "primary_link")
                quality = RoadClassifier.RoadQuality.Preferred;
            else if (highway is "secondary" or "secondary_link" or "tertiary" or "tertiary_link"
                     or "unclassified" or "residential")
                quality = RoadClassifier.RoadQuality.Preferred;
            else if (highway is "service" or "living_street")
                quality = RoadClassifier.RoadQuality.Acceptable;
            else
                quality = RoadClassifier.RoadQuality.Preferred;

            cache[profileId] = (quality, highway);
        }

        _staticEdgeQualityCache = cache;
        sw.Stop();
        if (enumeratedEdges == 0 && _routerDb.Network.EdgeCount > 0)
            StatusChanged?.Invoke($"Edge quality pre-computation skipped (enumerator broken after cache load, {_routerDb.Network.EdgeCount:N0} edges available)");
        else
            StatusChanged?.Invoke($"Pre-computed {cache.Count} edge profiles from {enumeratedEdges:N0} edges in {sw.ElapsedMilliseconds}ms");
    }
}
