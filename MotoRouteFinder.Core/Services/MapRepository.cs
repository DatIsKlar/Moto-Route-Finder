using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Itinero;
using Itinero.Attributes;
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
    public EdgeBlocker? EdgeBlocker => _edgeBlocker;

    // Cache diagnostics
    private bool _loadedFromCache;
    private string _motorwayCacheFile = "";
    private int _motorwaysBlockedAtLoadTime;
    private long _motorwayBlockLoadTimeMs;
    private bool _motorwaysInCache;
    private int _motorwaysFailedToBlock;
    private bool _motorwayBlockValidationPassed;
    private bool _motorwaysScanCompleted;
    private bool _motorwayBlockingSuspect;
    private int _routableMotorwayEdgesOnLoad;

    // Motorway edge scan cache — avoids re-scanning all edges for the same cache file.
    private static readonly ConcurrentDictionary<string, List<uint>> _motorwayEdgeCache = new();
    public static ConcurrentDictionary<string, List<uint>> MotorwayEdgeCache => _motorwayEdgeCache;

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
    public bool MotorwayBlockingSuspect => _motorwayBlockingSuspect;
    public int RoutableMotorwayEdgesOnLoad => _routableMotorwayEdgesOnLoad;

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

            // Integrity check: if filename says _nomotorway, verify motorways are actually blocked
            if (_motorwaysInCache)
            {
                var checkBlocker = new EdgeBlocker(this);
                int routableMotorways = checkBlocker.CountRoutableMotorways();
                if (routableMotorways > 0)
                {
                    _motorwayBlockingSuspect = true;
                    _routableMotorwayEdgesOnLoad = routableMotorways;
                    var warnMsg = $"[WARN] Cache claims _nomotorway but {routableMotorways:N0} motorway edges are still routable — cache may be stale or misbuilt";
                    Console.WriteLine(warnMsg);
                    StatusChanged?.Invoke(warnMsg);
                }
            }

            _router = new Router(_routerDb);
            CachePath = cachePath;
            StatusChanged?.Invoke($"Loaded from cache: {Path.GetFileName(cachePath)}");
        }
    }

    private void LoadMap(string osmPbfPath, bool avoidHighways = false)
    {
        if (!File.Exists(osmPbfPath))
            throw new FileNotFoundException($"OSM file not found: {osmPbfPath}");

        lock (_lock)
        {
            _loadedMaps.Clear();
            _loadedMaps.Add(Path.GetFileName(osmPbfPath));

            var cachePath = avoidHighways
                ? GetNoMotorwayCachePath(osmPbfPath)
                : GetCachePath(osmPbfPath);

            if (TryLoadFromCache(cachePath, avoidHighways))
                return;

            // M2: Dispose PBF streams after loading to release file handles
            using var stream = File.OpenRead(osmPbfPath);
            var sources = new OsmStreamSource[] { new PBFOsmStreamSource(stream) };
            LoadRouterDbFromSources(sources, cachePath, avoidHighways);
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

            if (TryLoadFromCache(cachePath, avoidHighways))
                return;

            // M2: Dispose PBF streams after loading to release file handles
            var streams = osmPbfPaths.Select(p => File.OpenRead(p)).ToArray();
            try
            {
                var sources = streams
                    .Select(s => (OsmStreamSource)new PBFOsmStreamSource(s))
                    .ToArray();
                LoadRouterDbFromSources(sources, cachePath, avoidHighways);
            }
            finally
            {
                foreach (var s in streams) s.Dispose();
            }
        }
    }

    /// <summary>
    /// Tries to load from an existing cache file. Returns true if loaded from cache.
    /// Must be called within a lock.
    /// </summary>
    private bool TryLoadFromCache(string cachePath, bool avoidHighways)
    {
        _motorwayCacheFile = Path.GetFileName(cachePath);
        _motorwaysInCache = avoidHighways;

        if (!File.Exists(cachePath))
            return false;

        _loadedFromCache = true;
        _motorwaysBlockedAtLoadTime = 0;
        _motorwayBlockLoadTimeMs = 0;
        _motorwaysScanCompleted = false;
        _motorwaysFailedToBlock = 0;
        _motorwayBlockValidationPassed = true;

        var displayName = string.Join(", ", _loadedMaps);
        StatusChanged?.Invoke($"Loading {displayName} from cache{(avoidHighways ? " (no motorways)" : "")}...");
        _routerDb = LoadRouterDb(cachePath);

        // Integrity check: if avoidHighways, verify motorways are actually blocked
        if (_motorwaysInCache)
        {
            var checkBlocker = new EdgeBlocker(this);
            int routableMotorways = checkBlocker.CountRoutableMotorways();
            if (routableMotorways > 0)
            {
                _motorwayBlockingSuspect = true;
                _routableMotorwayEdgesOnLoad = routableMotorways;
                var warnMsg = $"[WARN] Cache claims _nomotorway but {routableMotorways:N0} motorway edges are still routable — cache may be stale or misbuilt";
                Console.WriteLine(warnMsg);
                StatusChanged?.Invoke(warnMsg);
            }
        }

        _router = new Router(_routerDb);
        CachePath = cachePath;
        StatusChanged?.Invoke($"Loaded: {displayName}");
        return true;
    }

    /// <summary>
    /// Shared logic: load OSM data from sources, optionally block motorways, save cache, precompute.
    /// Must be called within a lock.
    /// </summary>
    private void LoadRouterDbFromSources(OsmStreamSource[] sources, string cachePath, bool avoidHighways)
    {
        _loadedFromCache = false;
        _motorwayCacheFile = Path.GetFileName(cachePath);
        _motorwaysInCache = avoidHighways;

        var displayName = string.Join(", ", _loadedMaps);
        StatusChanged?.Invoke($"Loading {displayName}...");

        var settings = new LoadSettings
        {
            AllCore = false,
            ProcessRestrictions = false,
            NetworkSimplificationEpsilon = 0,
            KeepNodeIds = false,
            KeepWayIds = false,
        };

        _routerDb = new RouterDb();
        var motorcycleVehicle = DynamicVehicle.Load(MotorcycleProfileLua.Value);
        _routerDb.LoadOsmData(sources, settings, motorcycleVehicle);
        _router = new Router(_routerDb);

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

        StatusChanged?.Invoke($"Loaded: {displayName} ({_loadedMaps.Count} map{(_loadedMaps.Count == 1 ? "" : "s")})");
    }

    public void ClearMaps()
    {
        lock (_lock)
        {
            var hadRouterDb = _routerDb != null;
            var hadRouter = _router != null;
            var hadEdgeBlocker = _edgeBlocker != null;
            var loadedCount = _loadedMaps.Count;
            var routerDbEdges = _routerDb?.Network?.EdgeCount ?? 0;

            _routerDb = null;
            _router = null;
            _edgeBlocker = null;
            _loadedMaps.Clear();
            _loadedFromCache = false;
            _motorwayCacheFile = "";
            _motorwaysBlockedAtLoadTime = 0;
            _motorwayBlockLoadTimeMs = 0;
            _motorwaysInCache = false;
            _motorwaysFailedToBlock = 0;
            _motorwayBlockValidationPassed = false;
            _motorwaysScanCompleted = false;
            _motorwayEdgeCache.Clear();

            Console.WriteLine($"[MEM] MapRepository.ClearMaps: RouterDb={hadRouterDb} (edges={routerDbEdges}), Router={hadRouter}, EdgeBlocker={hadEdgeBlocker}, LoadedMaps={loadedCount}");
        }
    }


    private static string GetCachePath(string osmPbfPath, bool nomotorway = false)
    {
        var baseName = Path.GetFileNameWithoutExtension(osmPbfPath);
        var dir = Path.GetDirectoryName(osmPbfPath) ?? ".";
        return Path.Combine(dir, $"{baseName}_motorcycle{(nomotorway ? "_nomotorway" : "")}.routerdb");
    }

    private static string GetNoMotorwayCachePath(string osmPbfPath)
        => GetCachePath(osmPbfPath, nomotorway: true);

    private static string GetMultiMapCachePath(string[] osmPbfPaths, bool nomotorway = false)
    {
        var names = osmPbfPaths
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .OrderBy(n => n)
            .ToArray();
        var combined = string.Join("+", names);
        var dir = Path.GetDirectoryName(osmPbfPaths[0]) ?? ".";
        return Path.Combine(dir, $"{combined}_motorcycle{(nomotorway ? "_nomotorway" : "")}.routerdb");
    }

    private static string GetMultiMapNoMotorwayCachePath(string[] osmPbfPaths)
        => GetMultiMapCachePath(osmPbfPaths, nomotorway: true);

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

}
