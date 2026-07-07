using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Itinero;
using Itinero.Profiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MotoRouteFinder.Helpers;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Services;

public class RoutingService
{
    [DllImport("libc", SetLastError = true)]
    private static extern int malloc_trim(int pad);

    [DllImport("libc", SetLastError = true)]
    private static extern int mallopt(int param, int value);

    private const int M_PURGE = -6;

    private readonly MapRepository _mapRepository = new();
    private RoadClassifier _roadClassifier = null!;
    private EdgeBlocker _edgeBlocker = null!;
    private RouteAssembler _routeAssembler = null!;
    private WaypointGenerator _waypointGenerator = null!;
    private RouteStatistics _routeStatistics = null!;
    private readonly DiagnosticsCollector _diagnostics = new();
    private RouteBuilder _routeBuilder = null!;
    private readonly Action<string> _statusForwarder;
    private readonly RouteGeometryUtils.EdgeSpatialIndex _edgeSpatialIndex = new();
    private readonly RouteGenerationOptions _options = new();
    private readonly ILogger<RoutingService>? _logger;

    // H3: Serialize shared-repo route generation to prevent concurrent RouterDb mutation
    private readonly SemaphoreSlim _generationLock = new(1, 1);
    private int _inFlightRequests;

    private Timer? _idleTimer;
    private Timer? _heartbeatTimer;
    private RouterDbPool? _pool;
    private string? _cachedCachePath;
    private bool _avoidHighwaysCached;
    private bool _unloading;
    private DateTime _lastHeartbeat = DateTime.MinValue;

    /// <summary>
    /// Creates a RoutingService using a pre-loaded MapRepository (for parallel candidate generation).
    /// No cache loading needed — the MapRepository is already initialized.
    /// </summary>
    public RoutingService(MapRepository mapRepository, IOptions<RouteGenerationOptions>? options = null)
    {
        _statusForwarder = msg => StatusChanged?.Invoke(msg);
        _mapRepository = mapRepository;
        if (options != null) _options = options.Value;
        InitSharedDependencies(options);
    }

    public RoutingService(IOptions<RouteGenerationOptions> options, ILogger<RoutingService>? logger = null)
    {
        _statusForwarder = msg => StatusChanged?.Invoke(msg);
        _options = options.Value;
        _logger = logger;
        InitSharedDependencies(options);
        _idleTimer = new Timer(UnloadMapIdle, null, Timeout.Infinite, Timeout.Infinite);
        _heartbeatTimer = new Timer(CheckHeartbeat, null, Timeout.Infinite, Timeout.Infinite);
    }

    private void InitSharedDependencies(IOptions<RouteGenerationOptions>? options = null)
    {
        _edgeBlocker = new EdgeBlocker(_mapRepository);
        _roadClassifier = new RoadClassifier(_mapRepository, _edgeBlocker);
        _routeAssembler = new RouteAssembler(_mapRepository, _roadClassifier);
        _waypointGenerator = new WaypointGenerator(_roadClassifier, _edgeSpatialIndex, _options);
        _routeStatistics = new RouteStatistics(_roadClassifier);
        _routeBuilder = new RouteBuilder(_mapRepository, _roadClassifier, _routeAssembler, _waypointGenerator, _routeStatistics, _diagnostics, _edgeBlocker, options: options);
    }

    public bool IsLoaded => _mapRepository.IsLoaded;
    public IReadOnlyList<string> LoadedMaps => _mapRepository.LoadedMaps;
    public string? CachePath => _mapRepository.CachePath;

    public event Action<string>? StatusChanged;
    public event Action<double>? ProgressChanged;

    /// <summary>
    /// Resets the idle timer. Called on every route request to keep the map loaded.
    /// </summary>
    public void TouchIdleTimer()
    {
        _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _idleTimer?.Change(TimeSpan.FromSeconds(_options.IdleTimeoutSeconds), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Ensures a RouterDbPool exists with at least the requested size.
    /// Reuses existing pool if large enough, otherwise disposes and recreates.
    /// Only disposes the old pool when no instances are checked out.
    /// </summary>
    public RouterDbPool EnsurePool(int requestedSize)
    {
        if (_pool != null && _pool.Size >= requestedSize)
            return _pool;

        if (_pool != null && _pool.CheckedOutCount > 0)
        {
            // Cannot dispose pool while instances are in use — reuse existing
            _logger?.LogWarning("[POOL] Cannot resize pool: {CheckedOut}/{Size} instances in use",
                _pool.CheckedOutCount, _pool.Size);
            return _pool;
        }

        _pool?.Dispose();

        if (string.IsNullOrEmpty(_cachedCachePath))
            throw new InvalidOperationException("No cache path available. Load a map first.");

        if (!File.Exists(_cachedCachePath))
            throw new InvalidOperationException($"Cache file missing: {_cachedCachePath} — reload the map");

        _pool = new RouterDbPool(_cachedCachePath, requestedSize);
        _pool.WarmUpAsync(msg => StatusChanged?.Invoke(msg)).GetAwaiter().GetResult();
        return _pool;
    }

    /// <summary>
    /// Gets the current pool size, or 0 if no pool exists.
    /// </summary>
    public int CurrentPoolSize => _pool?.Size ?? 0;

    /// <summary>
    /// Releases the RouterDb from memory. Called by the idle timer after inactivity.
    /// </summary>
    private void UnloadMapIdle(object? state)
    {
        if (_unloading) return;
        _unloading = true;
        try
        {
            _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            // Preserve cache path so EnsureMapLoadedAsync can reload after idle unload
            var savedCachePath = _cachedCachePath;

            StatusChanged?.Invoke("Idle timer fired — unloading map to free memory...");
            ClearMaps();

            // Restore cache path for future reload
            _cachedCachePath = savedCachePath;

            var memMB = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1);
            StatusChanged?.Invoke($"Map unloaded. Memory: {memMB}MB. Will reload on next request.");
        }
        finally
        {
            _unloading = false;
        }
    }

    /// <summary>
    /// Reloads the map from cache if it was unloaded due to idle timeout.
    /// </summary>
    public async Task EnsureMapLoadedAsync()
    {
        if (_mapRepository.IsLoaded) return;
        if (string.IsNullOrEmpty(_cachedCachePath)) return;

        StatusChanged?.Invoke("Reloading map from cache...");
        await _mapRepository.LoadCacheAsync(_cachedCachePath);
        TouchIdleTimer();
        StartHeartbeatMonitor();
    }

    /// <summary>
    /// Called by the client heartbeat. Resets the idle timer and records the heartbeat time.
    /// </summary>
    public void Heartbeat()
    {
        _lastHeartbeat = DateTime.UtcNow;
        TouchIdleTimer();
    }

    /// <summary>
    /// Starts the heartbeat monitoring. Called when a map is first loaded.
    /// </summary>
    private void StartHeartbeatMonitor()
    {
        _lastHeartbeat = DateTime.UtcNow;
        _heartbeatTimer?.Change(TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds), TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds));
    }

    /// <summary>
    /// Checks if the heartbeat has timed out. If no heartbeat for HeartbeatTimeoutSeconds, unloads the map.
    /// </summary>
    private void CheckHeartbeat(object? state)
    {
        if (_unloading || !_mapRepository.IsLoaded) return;
        if (_lastHeartbeat == DateTime.MinValue) return;

        var elapsed = (DateTime.UtcNow - _lastHeartbeat).TotalSeconds;
        if (elapsed > _options.HeartbeatTimeoutSeconds)
        {
            UnloadMapIdle(null);
        }
    }

    public async Task LoadMapAsync(string osmPbfPath, bool avoidHighways = false)
    {
        _mapRepository.StatusChanged += _statusForwarder;
        await _mapRepository.LoadMapAsync(osmPbfPath, avoidHighways);
        _roadClassifier.ClearCache();
        _cachedCachePath = _mapRepository.CachePath;
        _avoidHighwaysCached = avoidHighways;
        TouchIdleTimer();
        StartHeartbeatMonitor();
    }

    public async Task LoadMapsAsync(string[] osmPbfPaths, bool avoidHighways = false)
    {
        _mapRepository.StatusChanged += _statusForwarder;
        await _mapRepository.LoadMapsAsync(osmPbfPaths, avoidHighways);
        _roadClassifier.ClearCache();
        _cachedCachePath = _mapRepository.CachePath;
        _avoidHighwaysCached = avoidHighways;
        TouchIdleTimer();
        StartHeartbeatMonitor();
    }

    public async Task LoadCacheAsync(string cachePath)
    {
        _mapRepository.StatusChanged += _statusForwarder;
        await _mapRepository.LoadCacheAsync(cachePath);
        _roadClassifier.ClearCache();
        _cachedCachePath = cachePath;
        _avoidHighwaysCached = false;
        TouchIdleTimer();
        StartHeartbeatMonitor();
    }

    public void DetachStatusHandler()
    {
        _mapRepository.StatusChanged -= _statusForwarder;
    }

    public void ClearMaps()
    {
        // H3: Wait for in-flight route generation to complete before clearing
        if (Interlocked.CompareExchange(ref _inFlightRequests, 0, 0) > 0)
        {
            _logger?.LogWarning("[MEM] ClearMaps deferred — {Count} in-flight request(s)", _inFlightRequests);
            // Signal idle timer to retry later
            _idleTimer?.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
            return;
        }

        _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _lastHeartbeat = DateTime.MinValue;
        _cachedCachePath = null;

        _logger?.LogInformation("[MEM] ClearMaps START: {Diag}", MemoryDiagnostics.Describe());

        // Step 1: Dispose pool
        if (_pool != null)
        {
            var poolSize = _pool.Size;
            _pool.Dispose();
            _pool = null;
            _logger?.LogInformation("[MEM] Pool disposed ({PoolSize} instances): {Diag}", poolSize, MemoryDiagnostics.Describe());
        }
        else
        {
            _logger?.LogInformation("[MEM] No pool to dispose: {Diag}", MemoryDiagnostics.Describe());
        }

        // Step 2: Clear caches
        _roadClassifier.ClearCache();
        _logger?.LogInformation("[MEM] RoadClassifier cache cleared: {Diag}", MemoryDiagnostics.Describe());

        // Step 3: Clear MapRepository (nulls RouterDb, Router, EdgeBlocker)
        _mapRepository.ClearMaps();
        _logger?.LogInformation("[MEM] MapRepository.ClearMaps done: {Diag}", MemoryDiagnostics.Describe());

        // Step 4: GC pass 1 — aggressive collect + finalizers
        var gcBefore1 = GC.GetTotalMemory(false);
        var gcInfo1 = MemoryDiagnostics.GetGCInfo();
        var gen0_1 = GC.CollectionCount(0);
        var gen1_1 = GC.CollectionCount(1);
        var gen2_1 = GC.CollectionCount(2);
        var memBefore = MemoryDiagnostics.Describe();

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        var gcAfter1 = GC.GetTotalMemory(false);
        _logger?.LogInformation("[MEM] GC pass 1 done: freed {Freed:N0} bytes managed. Before={Before} (Gen0={Gen0},Gen1={Gen1},Gen2={Gen2}) After={After} ({GcInfo})",
            gcBefore1 - gcAfter1, memBefore, gen0_1, gen1_1, gen2_1, MemoryDiagnostics.Describe(), gcInfo1);

        // Step 5: GC pass 2 — compact LOH + decommit
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        var gcBefore2 = GC.GetTotalMemory(false);
        var lohBefore = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        var gcAfter2 = GC.GetTotalMemory(false);
        var lohAfter = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;
        _logger?.LogInformation("[MEM] GC pass 2 (LOH compact) done: freed {Freed:N0} bytes managed, LOH {LohBefore:N0} -> {LohAfter:N0}. {Diag}",
            gcBefore2 - gcAfter2, lohBefore, lohAfter, MemoryDiagnostics.Describe());

        // Step 6: mallopt(M_PURGE) on Linux — flush ALL glibc cached memory
        if (OperatingSystem.IsLinux())
        {
            var rssBefore = MemoryDiagnostics.GetRssKB();
            mallopt(M_PURGE, 0);
            malloc_trim(0);
            var rssAfter = MemoryDiagnostics.GetRssKB();
            _logger?.LogInformation("[MEM] mallopt(M_PURGE)+malloc_trim: RSS {RssBefore}KB -> {RssAfter}KB (freed {Freed}KB). {Diag}",
                rssBefore, rssAfter, rssBefore - rssAfter, MemoryDiagnostics.Describe());
        }

        _logger?.LogInformation("[MEM] ClearMaps DONE: {Diag}", MemoryDiagnostics.Describe());
    }

    public async Task<RouteResponse> GenerateLoopRouteAsync(RouteRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureMapLoadedAsync();
        // H3: Serialize shared-repo generation — EdgeBlocker mutates RouterDb in-place (not thread-safe)
        Interlocked.Increment(ref _inFlightRequests);
        await _generationLock.WaitAsync(cancellationToken);
        try
        {
            return GenerateLoopRoute(request, cancellationToken);
        }
        finally
        {
            _generationLock.Release();
            Interlocked.Decrement(ref _inFlightRequests);
        }
    }

    private RouteResponse GenerateLoopRoute(RouteRequest request, CancellationToken cancellationToken = default)
    {
        if (_mapRepository.Router == null || _mapRepository.RouterDb == null)
        {
            throw new InvalidOperationException("Map not loaded. Call LoadMapAsync first.");
        }

        TouchIdleTimer();
        var profile = _mapRepository.RouterDb.GetSupportedProfile("motorcycle");

        if (request.Waypoints.Count == 0)
        {
            return GenerateAutoLoop(request, profile, cancellationToken);
        }
        else
        {
            return GenerateWaypointRoute(request, profile, cancellationToken);
        }
    }

    /// <summary>
    /// Instance wrapper around the static GenerateLoopRouteCandidates that tracks in-flight requests
    /// to prevent the idle timer from disposing the pool mid-generation.
    /// </summary>
    public async Task<(RouteResponse best, RouteResponse[] allCandidates)> GenerateCandidatesAsync(
        RouteRequest request,
        int candidateCount,
        IOptions<RouteGenerationOptions>? options = null,
        CancellationToken cancellationToken = default,
        IProgress<(int candidateIndex, int candidateCount, double fraction, string? message)>? progress = null)
    {
        Interlocked.Increment(ref _inFlightRequests);
        TouchIdleTimer();
        try
        {
            var pool = EnsurePool(candidateCount);
            return await Task.Run(() =>
                GenerateLoopRouteCandidates(request, pool, candidateCount, options, cancellationToken, progress),
                cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightRequests);
        }
    }

    /// <summary>
    /// Generates multiple route candidates in parallel using separate RouterDb instances from the pool.
    /// Returns the best candidate (by QualityScore) and all candidates for comparison.
    /// </summary>
    public static (RouteResponse best, RouteResponse[] allCandidates) GenerateLoopRouteCandidates(
        RouteRequest request,
        RouterDbPool pool,
        int candidateCount,
        IOptions<RouteGenerationOptions>? options = null,
        CancellationToken cancellationToken = default,
        IProgress<(int candidateIndex, int candidateCount, double fraction, string? message)>? progress = null)
    {
        var repos = pool.CheckoutMultiple(candidateCount);
        var candidates = new RouteResponse[candidateCount];
        Exception? firstError = null;

        try
        {
            Parallel.For(0, candidateCount, new ParallelOptions { MaxDegreeOfParallelism = candidateCount }, i =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var service = new RoutingService(repos[i], options);

                    if (progress != null)
                    {
                        service.ProgressChanged += p => progress.Report((i, candidateCount, p, null));
                        service.StatusChanged += msg => progress.Report((i, candidateCount, -1, msg));
                    }

                    candidates[i] = service.GenerateLoopRoute(request, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref firstError, ex, null);
                    candidates[i] = null;
                }
            });
        }
        finally
        {
            pool.CheckinMultiple(repos);
        }

        // Pick best by QualityScore, break ties by RepetitionRatio
        var valid = candidates
            .Where(c => c?.Stats != null)
            .OrderByDescending(c => c!.Stats.QualityScore)
            .ThenBy(c => c!.Stats.RepetitionRatio)
            .ToList();
        if (valid.Count == 0)
        {
            var detail = firstError?.Message ?? "unknown error";
            throw new InvalidOperationException(
                $"All {candidateCount} candidates failed to generate a route (first error: {detail})", firstError);
        }
        var best = valid[0];

        return (best, candidates);
    }

    private void ComputeAttemptMetrics(
        RouteStats stats, RepetitionBreakdown repetition, List<Coordinate> geometry,
        double targetDist)
    {
        // Calculate circularity score on forward path only (return path mirrors forward, inflating score)
        int fwdEnd = Math.Min(_routeStatistics.ReturnToStartIndex + 1, geometry.Count);
        var forwardPath = geometry.GetRange(0, fwdEnd);
        var (circTotal, circSpread, circSector, circCompactness) = RouteGeometryUtils.CalculateCircularityScoreWithSubScores(forwardPath, _options?.MaxBearingSpreadDegrees ?? 270);
        stats.CircularityScoreComponent = Math.Round(circTotal, 1);
        stats.CircularitySpreadSubScore = Math.Round(circSpread, 1);
        stats.CircularitySectorSubScore = Math.Round(circSector, 1);
        stats.CircularityCompactnessSubScore = Math.Round(circCompactness, 1);
        stats.QualityScore = RouteStats.CalculateQualityScore(stats, repetition, targetDist, _options);

        // Quality component breakdown (v2 formula)
        RouteStats.CalculateScoreComponents(stats, targetDist, _options!);
    }

    private void SelectBestRoute(
        RouteStats stats, RepetitionBreakdown repetition, List<Coordinate> geometry,
        List<Coordinate> allPoints, double targetDist,
        ref double bestRepetition, ref double bestQuality, ref int bestAttempt,
        ref RouteResponse? bestRoute, ref double bestQualityWithinCap,
        ref RouteResponse? bestRouteWithinCap, int attempt)
    {
        // §20: Unify selection on QualityScore (higher = better)
        if (stats.QualityScore > bestQuality)
        {
            bestQuality = stats.QualityScore;
            bestRepetition = stats.RepetitionRatio;
            bestAttempt = attempt + 1;
            bestRoute = BuildRouteResponse(geometry, stats, allPoints, repetition, _routeStatistics.ReturnToStartIndex);
        }

        // Within-cap: hard constraints + highest QualityScore
        if (stats.TotalDistanceKm <= targetDist * _options.OvershootThresholdMultiplier && repetition.OutAndBackM <= _options.OutAndBackOverlapThresholdM && stats.QualityScore > bestQualityWithinCap)
        {
            bestQualityWithinCap = stats.QualityScore;
            bestRouteWithinCap = BuildRouteResponse(geometry, stats, allPoints, repetition, _routeStatistics.ReturnToStartIndex);
        }
    }

    private RouteResponse GenerateAutoLoop(RouteRequest request, IProfileInstance profile, CancellationToken cancellationToken)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        ProgressChanged?.Invoke(0.0);

        // Reset per-route counters at start of each route generation
        _routeAssembler.ResetCounters();

        double targetDist = request.TargetDistanceKm
            ?? (request.TargetDurationMin.HasValue ? request.TargetDurationMin.Value * GeoConstants.KmPerHour / 60.0 : 50.0);

        // Motorways are now blocked at load time when avoidHighways is enabled
        // No runtime blocking needed — the RouterDb already has motorways blocked in the cache
        StatusChanged?.Invoke($"Generating route (attempt 1/{_options.MaxRouteAttempts})...");

        _diagnostics.Clear();
        _diagnostics.Enabled = request.CollectDiagnostics;

        double bestRepetition = double.MaxValue;
        double bestQuality = -1;
        RouteResponse? bestRoute = null;
        RouteResponse? bestRouteWithinCap = null;
        double bestQualityWithinCap = -1;
        int bestAttempt = 0;
        double previousRepetition = double.MaxValue;
        double previousQuality = -1;
        var previousGeometries = new List<(List<Coordinate> geom, HashSet<RouteGeometryUtils.EdgeKey> edges)>();
        var attemptElapsedMs = new List<double>();
        var attemptRepetitionRatio = new List<double>();
        var attemptQualityScore = new List<double>();
        var attemptOverlapWithPrevious = new List<double>();
        var attemptRejectionReasons = new List<string>();
        HashSet<int>? previousBlockedSectors = null;
        double? previousFailedBearing = null;

        for (int attempt = 0; attempt < _options.MaxRouteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptSw = System.Diagnostics.Stopwatch.StartNew();
            long memStart = GC.GetTotalMemory(false);
            int gcStart = GC.CollectionCount(0);
            int gc1Start = GC.CollectionCount(1);
            int gc2Start = GC.CollectionCount(2);
            StatusChanged?.Invoke($"Generating route (attempt {attempt + 1}/{_options.MaxRouteAttempts})...");
            ProgressChanged?.Invoke((double)attempt / _options.MaxRouteAttempts);

            // Start with alternative path (GraphHopper-inspired) for best repetition avoidance
            // Fall back to progressive loop only if alternative fails
            // Turnaround distance varies by attempt: 45%, 55%, 50% for geometric diversity
            double turnaroundRatio = attempt switch
            {
                0 => 0.45,
                1 => 0.55,
                _ => 0.50
            };
            var result = _routeBuilder.BuildAlternativeLoop(profile, request.Start, targetDist, request.AvoidHighways, cancellationToken, attemptNumber: attempt + 1, turnaroundRatio: turnaroundRatio, direction: request.Direction, avoidBearing: previousFailedBearing);

            // If alternative failed, fall back to progressive
            if (result == null)
            {
                result = _routeBuilder.BuildProgressiveLoop(profile, request.Start, targetDist, request.WaypointCount + attempt, request.AvoidHighways, cancellationToken, attemptNumber: attempt + 1, avoidSectors: previousBlockedSectors, direction: request.Direction);
            }

            if (result == null) continue;

            var (geometry, allPoints, context) = result.Value;

            // Collect blocked sectors from this attempt for cross-attempt memory
            previousBlockedSectors = new HashSet<int>();
            for (int i = 0; i < context.BlockedSectorCounts.Length; i++)
            {
                if (context.BlockedSectorCounts[i] > 0)
                    previousBlockedSectors.Add(i);
            }

            // §13b: Extract failed turnaround bearing for cross-attempt avoidance
            if (context.FailedTurnaroundBearing.HasValue)
                previousFailedBearing = context.FailedTurnaroundBearing;

            // Compute attempt diversity (overlap with previous attempts) — reuse cached edge sets
            double overlapWithPrevious = 0;
            bool isDuplicate = false;
            var currentEdges = new HashSet<RouteGeometryUtils.EdgeKey>();
            for (int i = 0; i < geometry.Count - 1; i++)
                currentEdges.Add(RouteGeometryUtils.MakeEdgeKey(geometry[i], geometry[i + 1]));

            if (previousGeometries.Count > 0)
            {
                double maxOverlap = 0;
                foreach (var (_, prevEdges) in previousGeometries)
                {
                    int shared = 0;
                    foreach (var key in currentEdges)
                        if (prevEdges.Contains(key)) shared++;
                    int total = currentEdges.Count + prevEdges.Count - shared;
                    double overlapRatio = total > 0 ? (double)shared / total : 0;
                    if (overlapRatio > maxOverlap) maxOverlap = overlapRatio;
                }
                overlapWithPrevious = maxOverlap;
                isDuplicate = maxOverlap > 0.8;
            }
            previousGeometries.Add((geometry, currentEdges));
            var repSw = System.Diagnostics.Stopwatch.StartNew();
            var repetition = _routeStatistics.CalculateRepetition(geometry);
            repSw.Stop();
            var statsSw = System.Diagnostics.Stopwatch.StartNew();
            var stats = _routeStatistics.CalculateStats(geometry, profile, precomputedRepetition: repetition);
            statsSw.Stop();

            var overlapTriggered = _diagnostics.Enabled ? _diagnostics.CountOverlapTriggered() : 0;

            ComputeAttemptMetrics(stats, repetition, geometry, targetDist);

            // Diagnostic-only: edge reuse, bottleneck analysis, shape analysis, stem events
            int maxEdgeReuse = 0;
            double avgEdgeReuse = 0;
            double avgSegmentLength = context.SegmentLengths.Count > 0 ? context.SegmentLengths.Average() : 0;
            string parallelization = "";
            RouteGeometryUtils.RouteShapeAnalysis shapeAnalysis = default;
            double returnOverlapM = 0;
            double returnSegmentLength = 0;
            Dictionary<string, double> overlapByPosition = new();

            if (_diagnostics.Enabled)
            {
                var edgeReuseCounts = new Dictionary<RouteGeometryUtils.EdgeKey, int>();
                for (int i = 0; i < geometry.Count - 1; i++)
                {
                    var key = RouteGeometryUtils.MakeEdgeKey(geometry[i], geometry[i + 1]);
                    if (edgeReuseCounts.ContainsKey(key))
                        edgeReuseCounts[key]++;
                    else
                        edgeReuseCounts[key] = 1;
                }
                maxEdgeReuse = edgeReuseCounts.Count > 0 ? edgeReuseCounts.Values.Max() : 0;
                avgEdgeReuse = edgeReuseCounts.Count > 0 ? edgeReuseCounts.Values.Average() : 0;

                var timingFields = new Dictionary<string, long>
                {
                    ["RoutingCalls"] = _routeAssembler.PerRouteRoutingCallsMs,
                    ["RoadClassification"] = _roadClassifier.ClassificationMs,
                    ["CoordinateResolution"] = _roadClassifier.ResolutionMs,
                    ["OverlapCalc"] = context.OverlapCalcMs,
                    ["MotorwayBlocking"] = _edgeBlocker.FindMotorwayMs,
                    ["TurnaroundDensity"] = context.TurnaroundDensityCheckMs,
                };
                var topBottleneck = timingFields.OrderByDescending(kvp => kvp.Value).First();
                long totalTimingMs = timingFields.Values.Sum();
                double bottleneckPct = totalTimingMs > 0
                    ? Math.Round((double)topBottleneck.Value / totalTimingMs * 100, 1)
                    : 0;

                if (_roadClassifier.ClassificationMs > attemptSw.ElapsedMilliseconds * 0.2)
                    parallelization = "Road classification is bottleneck. Could cache results or parallelize per-sample-point.";
                else if (_roadClassifier.ResolutionMs > attemptSw.ElapsedMilliseconds * 0.2)
                    parallelization = "Coordinate resolution is bottleneck. Could batch-resolve points.";
                else if (_edgeBlocker.FindMotorwayMs > attemptSw.ElapsedMilliseconds * 0.15)
                    parallelization = "Motorway blocking is bottleneck. Edge enumeration could run on background thread.";

                shapeAnalysis = RouteGeometryUtils.AnalyzeRouteShape(geometry, request.Start);
                (returnOverlapM, returnSegmentLength, overlapByPosition) = ComputeAllDiagnosticsInSinglePass();

                _diagnostics.Add(CreateAttemptDiagnosticsRecord(new AttemptData(
                    Attempt: attempt,
                    TargetDist: targetDist,
                    Request: request,
                    Stats: stats,
                    Repetition: repetition,
                    Context: context,
                    AllPoints: allPoints,
                    ShapeAnalysis: shapeAnalysis,
                    AttemptElapsedMs: attemptSw.ElapsedMilliseconds,
                    RepElapsedMs: repSw.ElapsedMilliseconds,
                    StatsElapsedMs: statsSw.ElapsedMilliseconds,
                    MemStart: memStart,
                    GcStart: gcStart, Gc1Start: gc1Start, Gc2Start: gc2Start,
                    MaxEdgeReuse: maxEdgeReuse,
                    AvgEdgeReuse: avgEdgeReuse,
                    AvgSegmentLength: avgSegmentLength,
                    EdgeReuseCounts: edgeReuseCounts,
                    OverlapWithPrevious: overlapWithPrevious,
                    IsDuplicate: isDuplicate,
                    TopBottleneck: topBottleneck,
                    BottleneckPct: bottleneckPct,
                    Parallelization: parallelization,
                    ReturnOverlapM: returnOverlapM,
                    ReturnSegmentLength: returnSegmentLength,
                    OverlapByPosition: overlapByPosition,
                    PreviousRepetition: previousRepetition,
                    PreviousQuality: previousQuality,
                    OverlapTriggered: overlapTriggered
                )));
            }

            attemptElapsedMs.Add(attemptSw.ElapsedMilliseconds);
            attemptRepetitionRatio.Add(stats.RepetitionRatio);
            attemptQualityScore.Add(stats.QualityScore);
            attemptOverlapWithPrevious.Add(overlapWithPrevious);

            previousRepetition = stats.RepetitionRatio;
            previousQuality = stats.QualityScore;

            SelectBestRoute(stats, repetition, geometry, allPoints, targetDist,
                ref bestRepetition, ref bestQuality, ref bestAttempt, ref bestRoute,
                ref bestQualityWithinCap, ref bestRouteWithinCap, attempt);

            if (stats.RepetitionRatio <= _options.MaxRepetitionRatio && stats.TotalDistanceKm <= targetDist * _options.OvershootThresholdMultiplier && stats.QualityScore >= _options.EarlyAcceptQualityScore)
            {
                bestRoute!.StemDiagnosticsJson = _diagnostics.ToJson();
                StatusChanged?.Invoke($"Route generated (attempt {attempt + 1}, QS={stats.QualityScore:F0}, RR={stats.RepetitionRatio:P0})");
                ProgressChanged?.Invoke(1.0);
                attemptRejectionReasons.Add("accepted_winner");
                return bestRoute!;
            }

            // Track rejection reason for this attempt
            var rejectionReasons = new List<string>();
            if (stats.RepetitionRatio > _options.MaxRepetitionRatio)
                rejectionReasons.Add("high_repetition");
            if (stats.TotalDistanceKm > targetDist * _options.OvershootThresholdMultiplier)
                rejectionReasons.Add("overshoot");
            if (rejectionReasons.Count == 0)
                rejectionReasons.Add("continued");
            attemptRejectionReasons.Add(string.Join(",", rejectionReasons));

            attemptSw.Stop();
            System.Diagnostics.Debug.WriteLine($"[PERF] Attempt {attempt + 1} took {attemptSw.ElapsedMilliseconds}ms");
        }

        totalSw.Stop();
        System.Diagnostics.Debug.WriteLine($"[PERF] GenerateAutoLoop total: {totalSw.ElapsedMilliseconds}ms");

        if (bestRoute != null)
        {
            var returnRoute = bestRouteWithinCap ?? bestRoute;
            var finalShapeAnalysis = RouteGeometryUtils.AnalyzeRouteShape(returnRoute.RouteGeometry, request.Start);
            _diagnostics.Add(new DebugFinalSummary
            {
                WinnerAttempt = bestAttempt,
                TotalAttempts = _options.MaxRouteAttempts,
                TargetDistanceKm = targetDist,
                BestRepetitionRatio = bestRepetition,
                BestTotalDistanceKm = returnRoute.Stats.TotalDistanceKm,
                BestWaypointCount = returnRoute.Waypoints.Count,
                BestRequestedWaypointCount = _diagnostics.GetAllRouteSummaries()
                    .OfType<DebugRouteSummary>()
                    .FirstOrDefault(s => s.AttemptNumber == bestAttempt)?.RequestedWaypointCount ?? request.WaypointCount,
                BestBuilderMethod = _diagnostics.GetAllRouteSummaries()
                    .OfType<DebugRouteSummary>()
                    .FirstOrDefault(s => s.AttemptNumber == bestAttempt)?.BuilderMethod ?? "",
                TotalElapsedMs = totalSw.ElapsedMilliseconds,
                StartPoint = new[] { request.Start.Lat, request.Start.Lon },
                FinalRepetitionBreakdown = returnRoute.RepetitionBreakdown,
                QualityScore = returnRoute.Stats.QualityScore,
                UsedCapRoute = bestRouteWithinCap != null,
                TotalResolveCount = _diagnostics.SumResolveCount(),
                TotalRoutingCount = _diagnostics.SumRoutingCount(),
                TotalBlockEdgesCount = _diagnostics.SumBlockEdgesCount(),
                AttemptElapsedMs = attemptElapsedMs,
                AttemptRepetitionRatio = attemptRepetitionRatio,
                AttemptQualityScore = attemptQualityScore,
                AttemptOverlapWithPrevious = attemptOverlapWithPrevious,
                FinalRoadQualityKm = returnRoute.Stats.RoadQualityKm,
                AttemptRejectionReasons = attemptRejectionReasons,
                // Session 9: New diagnostic fields
                AttemptNullDropCounts = new List<int>(),
                BlockSectorEffectiveness = ComputeBlockSectorEffectiveness(),
                PerAttemptReturnOverlapM = ComputePerAttemptReturnOverlapM(),
                // Direction diagnostics
                RequestedDirectionBias = request.Direction.ToString(),
                RequestedBearing = GeoConstants.DirectionToBearing(request.Direction),
                DominantRouteDirection = GeoConstants.BearingToDirection(finalShapeAnalysis.TurnaroundBearing),
                TurnaroundCoordinates = new[] { finalShapeAnalysis.TurnaroundLat, finalShapeAnalysis.TurnaroundLon },
                ForwardWaypointCoordinates = ExtractForwardWaypointCoordinates(
                    returnRoute.Waypoints,
                    _diagnostics.GetAllRouteSummaries()
                        .OfType<DebugRouteSummary>()
                        .FirstOrDefault(s => s.AttemptNumber == bestAttempt)?.ForwardWaypointCount ?? 4),
                // Cache and motorway load-time diagnostics
                MapLoadedFromCache = _mapRepository.LoadedFromCache,
                MotorwayCacheFile = _mapRepository.MotorwayCacheFile,
                MotorwaysBlockedAtLoadTime = _mapRepository.MotorwaysBlockedAtLoadTime,
                MotorwayBlockLoadTimeMs = _mapRepository.MotorwayBlockLoadTimeMs,
                MotorwaysInCache = _mapRepository.MotorwaysInCache,
                MotorwaysFailedToBlock = _mapRepository.MotorwaysFailedToBlock,
                MotorwayBlockValidationPassed = _mapRepository.MotorwayBlockValidationPassed,
                MotorwaysScanCompleted = _mapRepository.MotorwaysScanCompleted,
                MotorwayBlockingSuspect = _pool?.MotorwayBlockingSuspect ?? _mapRepository.MotorwayBlockingSuspect,
                RoutableMotorwayEdgesOnLoad = _pool?.RoutableMotorwayEdgesOnLoad ?? _mapRepository.RoutableMotorwayEdgesOnLoad,
                CacheFileMissingAtGen = !string.IsNullOrEmpty(_cachedCachePath) && !File.Exists(_cachedCachePath),
            });
            returnRoute.StemDiagnosticsJson = _diagnostics.ToJson();
            StatusChanged?.Invoke($"Route generated (best of {_options.MaxRouteAttempts} attempts, {bestRepetition:P0} repetition)");
            ProgressChanged?.Invoke(1.0);
            return returnRoute!;
        }

        // Graceful fallback: try a simple route with minimal waypoints
        StatusChanged?.Invoke("All attempts failed, trying fallback route...");
        var fallbackResult = _routeBuilder.BuildProgressiveLoop(profile, request.Start, targetDist, 3, request.AvoidHighways, cancellationToken, attemptNumber: 0);
        if (fallbackResult != null)
        {
            var (fallbackGeometry, fallbackPoints, fallbackContext) = fallbackResult.Value;
            var fallbackRepetition = _routeStatistics.CalculateRepetition(fallbackGeometry);
            var fallbackStats = _routeStatistics.CalculateStats(fallbackGeometry, profile, precomputedRepetition: fallbackRepetition);
            RouteStats.CalculateScoreComponents(fallbackStats, targetDist, _options!);
            var fallbackRoute = new RouteResponse
            {
                RouteGeometry = fallbackGeometry,
                Stats = fallbackStats,
                Waypoints = fallbackPoints,
                RepetitionBreakdown = fallbackRepetition,
            };
            StatusChanged?.Invoke($"Fallback route generated ({fallbackStats.RepetitionRatio:P0} repetition)");
            ProgressChanged?.Invoke(1.0);
            return fallbackRoute;
        }

        throw new InvalidOperationException(
            "Could not find a connected route through the generated waypoints. " +
            "The roads near the start point may be disconnected. " +
            "Try moving the start point or reducing the waypoint count.");
    }

    // Session 9: Helper methods for new diagnostic fields — single pass over all stem events
    private (double returnOverlapM, double returnSegmentLength,
        Dictionary<string, double> overlapByPosition) ComputeAllDiagnosticsInSinglePass()
    {
        double returnOverlapM = 0;
        double returnSegmentLength = 0;
        var overlapByPosition = new Dictionary<string, double> { ["0-25%"] = 0, ["25-50%"] = 0, ["50-75%"] = 0, ["75-100%"] = 0 };
        int totalSegments = _diagnostics.CountSegmentsTotal();

        foreach (var e in _diagnostics.GetAllStemEvents())
        {
            if (e is not DebugStemEvent evt) continue;

            // returnOverlapM & returnSegmentLength
            if (evt.SegmentRole == "return")
            {
                if (evt.IsReturnPathOverlap)
                    returnOverlapM += evt.OverlapWithPriorSegments * evt.SegmentLengthM;
                if (returnSegmentLength == 0)
                    returnSegmentLength = evt.SegmentLengthM;
            }

            // overlapByPosition (forward only)
            if (evt.SegmentRole == "forward" && totalSegments > 0)
            {
                double positionRatio = (double)evt.SegmentIndex / totalSegments;
                string band = positionRatio < 0.25 ? "0-25%" : positionRatio < 0.50 ? "25-50%" : positionRatio < 0.75 ? "50-75%" : "75-100%";
                overlapByPosition[band] += evt.OverlapWithPriorSegments * evt.SegmentLengthM;
            }
        }

        // Finalize overlapByPosition (round)
        foreach (var key in overlapByPosition.Keys.ToList())
            overlapByPosition[key] = Math.Round(overlapByPosition[key], 0);

        return (Math.Round(returnOverlapM, 0), returnSegmentLength,
            overlapByPosition);
    }

    // Session 9: Helper methods for DebugFinalSummary
    private double ComputeBlockSectorEffectiveness()
    {
        var summaries = _diagnostics.GetAllRouteSummaries().OfType<DebugRouteSummary>().ToList();
        if (summaries.Count < 2) return 0;

        double totalImprovement = 0;
        int comparisonCount = 0;

        for (int i = 1; i < summaries.Count; i++)
        {
            if (summaries[i].RepetitionDeltaFromPrior < 0)
            {
                totalImprovement += Math.Abs(summaries[i].RepetitionDeltaFromPrior);
                comparisonCount++;
            }
        }

        return comparisonCount > 0 ? Math.Round(totalImprovement / comparisonCount, 3) : 0;
    }

    private List<double> ComputePerAttemptReturnOverlapM()
    {
        var overlaps = new List<double>();
        foreach (var summary in _diagnostics.GetAllRouteSummaries())
        {
            if (summary is DebugRouteSummary rs)
            {
                overlaps.Add(rs.ReturnOverlapM);
            }
        }
        return overlaps;
    }


    private RouteResponse GenerateWaypointRoute(RouteRequest request, IProfileInstance profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProgressChanged?.Invoke(0.0);

        // Reset per-route counters at start of each route generation
        _routeAssembler.ResetCounters();

        double routeRadius = request.TargetDistanceKm ?? 50;
        if (request.Waypoints.Count > 0)
        {
            double maxWpDist = request.Waypoints.Max(wp => RouteGeometryUtils.HaversineDistance(request.Start, wp));
            routeRadius = Math.Max(routeRadius, maxWpDist / 1000) * 1.5;
        }

        // Motorways are now blocked at load time when avoidHighways is enabled
        // No runtime blocking needed

        StatusChanged?.Invoke("Calculating route...");

        for (int regenAttempt = 0; regenAttempt < 3; regenAttempt++)
        {
            var waypoints = new List<Coordinate>();
            foreach (var wp in request.Waypoints)
            {
                var resolved = _roadClassifier.TryResolveToRoadCounted(profile, wp, regenAttempt == 0 ? 2000 : 4000);
                if (resolved != null)
                {
                    var quality = _roadClassifier.ClassifyRoad(resolved, profile, request.AvoidHighways);
                    if (quality != RoadClassifier.RoadQuality.Preferred && quality != RoadClassifier.RoadQuality.Acceptable)
                        resolved = null;
                }
                waypoints.Add(resolved ?? wp);
            }

            var result = _routeBuilder.IterativeRouteLoop(profile, request.Start, waypoints, request.AvoidHighways, cancellationToken);
            if (result == null) continue;

            var routeResult = result.Value;
            var geometry = routeResult.geometry;
            var allPoints = routeResult.allPoints;
            var needsRegen = routeResult.needsRegen;
            if (!needsRegen)
            {
                var repetition = _routeStatistics.CalculateRepetition(geometry);
                var stats = _routeStatistics.CalculateStats(geometry, profile, precomputedRepetition: repetition);
                stats.QualityScore = RouteStats.CalculateQualityScore(stats, repetition, routeRadius, _options);
                RouteStats.CalculateScoreComponents(stats, routeRadius, _options!);

                ProgressChanged?.Invoke(1.0);
                StatusChanged?.Invoke("Route calculated");

                return BuildRouteResponse(geometry, stats, allPoints, repetition, _routeStatistics.ReturnToStartIndex);
            }
        }

        throw new InvalidOperationException(
            "Could not calculate a route between the specified points. " +
            "Make sure the start point and waypoints are on roads within the loaded map area.");
    }

    private static RouteResponse BuildRouteResponse(
        List<Coordinate> geometry, RouteStats stats, List<Coordinate> allPoints,
        RepetitionBreakdown? repetition, int returnToStartIndex, string? stemDiagnosticsJson = null)
    {
        return new RouteResponse
        {
            RouteGeometry = geometry,
            Stats = stats,
            Waypoints = allPoints,
            RepetitionSegments = RouteGeometryUtils.ExtractRepetitionSegments(geometry, returnToStartIndex),
            RepetitionBreakdown = repetition,
            StemDiagnosticsJson = stemDiagnosticsJson,
        };
    }

    private static List<double[]> ExtractForwardWaypointCoordinates(List<Coordinate> allPoints, int forwardWaypointCount)
    {
        var result = new List<double[]>();
        // allPoints: [start, wp1, wp2, ..., wpN, returnWps..., start]
        // First entry is start, then forwardWaypointCount entries are forward waypoints
        int count = Math.Min(forwardWaypointCount + 1, allPoints.Count); // +1 for start
        for (int i = 0; i < count; i++)
            result.Add(new[] { allPoints[i].Lat, allPoints[i].Lon });
        return result;
    }

    private record AttemptData(
        int Attempt,
        double TargetDist,
        RouteRequest Request,
        RouteStats Stats,
        RepetitionBreakdown Repetition,
        BuildContext Context,
        List<Coordinate> AllPoints,
        RouteGeometryUtils.RouteShapeAnalysis ShapeAnalysis,
        long AttemptElapsedMs,
        long RepElapsedMs,
        long StatsElapsedMs,
        long MemStart,
        int GcStart, int Gc1Start, int Gc2Start,
        int MaxEdgeReuse,
        double AvgEdgeReuse,
        double AvgSegmentLength,
        Dictionary<RouteGeometryUtils.EdgeKey, int> EdgeReuseCounts,
        double OverlapWithPrevious,
        bool IsDuplicate,
        KeyValuePair<string, long> TopBottleneck,
        double BottleneckPct,
        string Parallelization,
        double ReturnOverlapM,
        double ReturnSegmentLength,
        Dictionary<string, double> OverlapByPosition,
        double PreviousRepetition,
        double PreviousQuality,
        int OverlapTriggered
    );

    private DebugRouteSummary CreateAttemptDiagnosticsRecord(AttemptData d)
    {
        var r = _diagnostics;
        var ctx = d.Context;
        var sa = d.ShapeAnalysis;
        var stats = d.Stats;
        var rep = d.Repetition;
        var s = _routeAssembler;
        var rc = _roadClassifier;
        var eb = _edgeBlocker;
        var erc = d.EdgeReuseCounts;

        return new DebugRouteSummary
        {
            AttemptNumber = d.Attempt + 1,
            TargetDistanceKm = d.TargetDist,
            TotalDistanceKm = stats.TotalDistanceKm,
            WaypointCount = d.AllPoints.Count,
            RepetitionRatio = stats.RepetitionRatio,
            EdgeOverlapM = Math.Round(rep.EdgeOverlapM, 0),
            OutAndBackOverlapM = Math.Round(rep.OutAndBackM, 0),
            TotalOverlapM = Math.Round(rep.EdgeOverlapM + rep.ParallelOverlapM, 0),
            SegmentsTotal = r.CountSegmentsTotal(),
            RequestedWaypointCount = d.Request.WaypointCount,
            BuilderMethod = ctx.BuilderMethod,
            SegmentsOverlapTriggered = d.OverlapTriggered,
            PushReroutesSucceeded = ctx.TotalPushReroutesSucceeded,
            MaxEdgeReuse = d.MaxEdgeReuse,
            AvgEdgeReuse = Math.Round(d.AvgEdgeReuse, 2),
            ElapsedMs = d.AttemptElapsedMs,
            Resolution = stats.RepetitionRatio <= _options.MaxRepetitionRatio && stats.TotalDistanceKm <= d.TargetDist * _options.OvershootThresholdMultiplier && rep.OutAndBackM <= _options.OutAndBackOverlapThresholdM ? "winner" : "attempt_complete",
            StartPoint = new[] { d.Request.Start.Lat, d.Request.Start.Lon },
            BlockedSectorCounts = ctx.BlockedSectorCounts.ToArray(),
            PushAttemptsUsed = ctx.TotalPushAttemptsUsed,
            RepetitionDeltaFromPrior = d.PreviousRepetition < double.MaxValue ? d.PreviousRepetition - stats.RepetitionRatio : 0,
            UniqueEdgeCount = erc.Count,
            TotalEdgeCount = erc.Values.Sum(),
            AverageSegmentLengthM = Math.Round(d.AvgSegmentLength, 0),
            MinLat = ctx.MinLat,
            MaxLat = ctx.MaxLat,
            MinLon = ctx.MinLon,
            MaxLon = ctx.MaxLon,
            WaypointRejectionReasons = ctx.WaypointRejectionReasons,
            QualityScore = stats.QualityScore,
            NearMissCount = r.CountNearMisses(),
            PrivateRoadDetectedCount = r.CountPrivateRoads(),
            OvershootRatio = Math.Round(stats.TotalDistanceKm / Math.Max(d.TargetDist, 0.1), 2),
            FindMotorwayMs = eb.FindMotorwayMs,
            CalculateStatsMs = d.StatsElapsedMs,
            CalculateRepetitionMs = d.RepElapsedMs,
            WaypointGenMs = ctx.WaypointGenMs,
            ConnectivityCheckMs = ctx.ConnectivityCheckMs,
            OverlapCalcMs = ctx.OverlapCalcMs,
            RoutingCallsMs = s.PerRouteRoutingCallsMs,
            IntermediateTotalMs = ctx.IntermediateTotalMs,
            ResolveCount = rc.ResolveCount,
            RoutingCount = s.PerRouteRoutingCount,
            BlockEdgesCount = eb.BlockEdgesCount,
            OverlapWithPreviousAttempt = Math.Round(d.OverlapWithPrevious, 3),
            IsDuplicateGeometry = d.IsDuplicate,
            ResolveCacheHitCount = rc.ResolveCacheHitCount,
            ConnectivityCacheHitCount = rc.ConnectivityCacheHitCount,
            HomingWaypointsUsed = ctx.HomingWaypointsUsed,
            FinalEdgeSetSize = ctx.FinalEdgeSetSize,
            MotorwayEdgesFound = eb.MotorwayEdgesFound,
            GridPointsSampled = eb.GridPointsSampled,
            TotalWaypointAttempts = ctx.TotalWaypointAttempts,
            TotalSegmentBuildMs = ctx.TotalSegmentBuildMs,
            ReturnOverlapM = d.ReturnOverlapM,
            ReturnSegmentLengthM = d.ReturnSegmentLength,
            OverlapBySegmentPosition = d.OverlapByPosition,
            EdgeSaturationRatio = erc.Count > 0 ? Math.Round((double)erc.Count / erc.Values.Sum(), 3) : 0,
            AttemptFailedEarlyAbort = ctx.AttemptFailedEarlyAbort,
            WastedRoutingCalls = s.PerRouteWastedCalls,
            WaypointDistributionUniformity = ctx.WaypointDistributionUniformity,
            HomingSuccessRate = ctx.HomingWaypointsUsed > 0 ? 1.0 : 0,
            MotorwayKm = stats.MotorwayKm,
            MotorwayPct = stats.MotorwayPct,
            CentroidLat = stats.CentroidLat,
            CentroidLon = stats.CentroidLon,
            BoundingBoxAreaKm2 = stats.BoundingBoxAreaKm2,
            CompactnessRatio = stats.CompactnessRatio,
            MaxDistanceFromStartKm = stats.MaxDistanceFromStartKm,
            SectorsVisited = stats.SectorsVisited,
            DominantRoadType = stats.DominantRoadType,
            DominantRoadTypePct = stats.DominantRoadTypePct,
            ResidentialKm = stats.ResidentialKm,
            LivingStreetKm = stats.LivingStreetKm,
            TurnCount = stats.TurnCount,
            SharpTurnCount = stats.SharpTurnCount,
            HairpinCount = stats.HairpinCount,
            AverageTurnAngle = stats.AverageTurnAngle,
            StraightLineRatio = stats.StraightLineRatio,
            RoadTypeTransitions = stats.RoadTypeTransitions,
            RepetitionScoreComponent = stats.RepetitionScoreComponent,
            CurvatureScoreComponent = stats.CurvatureScoreComponent,
            AverageCurvature = stats.AverageCurvature,
            DistAccuracyComponent = stats.DistAccuracyComponent,
            CircularityScoreComponent = stats.CircularityScoreComponent,
            CircularitySpreadSubScore = stats.CircularitySpreadSubScore,
            CircularitySectorSubScore = stats.CircularitySectorSubScore,
            CircularityCompactnessSubScore = stats.CircularityCompactnessSubScore,
            ForwardDistanceKm = ctx.ForwardDistanceKm,
            ReturnDistanceKm = ctx.ReturnDistanceKm,
            ForwardPctOfTotal = stats.TotalDistanceKm > 0 ? Math.Round(ctx.ForwardDistanceKm / stats.TotalDistanceKm * 100, 1) : 0,
            ReturnRatio = ctx.ReturnRatio,
            ForwardLoopExitReason = ctx.ForwardLoopExitReason,
            ReturnSegmentCurvature = ctx.ReturnSegmentCurvature,
            ReturnSegmentRoadType = ctx.ReturnSegmentRoadType,
            ReturnSegmentOverlapPct = ctx.ReturnSegmentOverlapPct,
            ReturnPushAttempts = ctx.ReturnPushAttempts,
            ReturnOverlapBeforePush = ctx.ReturnOverlapBeforePush,
            ReturnOverlapAfterPush = ctx.ReturnOverlapAfterPush,
            EstReturnAtLoopExit = ctx.EstReturnAtLoopExit,
            ActualReturnVsEstimate = ctx.ActualReturnVsEstimate,
            ForwardHaversineAtExit = ctx.ForwardHaversineAtExit,
            SectorsBlockedAtReturn = ctx.SectorsBlockedAtReturn,
            ReturnSegmentRerouteCount = ctx.ReturnSegmentRerouteCount,
            ForwardBearingSpread = sa.ForwardBearingSpread,
            ReturnBearingSpread = sa.ReturnBearingSpread,
            TurnaroundBearing = sa.TurnaroundBearing,
            ForwardPathCurvature = sa.ForwardPathCurvature,
            ReturnPathCurvature = sa.ReturnPathCurvature,
            ForwardMaxDeviationFromLine = sa.ForwardMaxDeviationFromLine,
            ReturnMaxDeviationFromLine = sa.ReturnMaxDeviationFromLine,
            ForwardDistinctBearingCount = sa.ForwardDistinctBearingCount,
            ReturnDistinctBearingCount = sa.ReturnDistinctBearingCount,
            ForwardReturnSectorDifference = sa.ForwardReturnSectorDifference,
            ForwardPathWindingNumber = sa.ForwardPathWindingNumber,
            ForwardBearingSpreadExHome = sa.ForwardBearingSpreadExHome,
            ReturnBearingSpreadExHome = sa.ReturnBearingSpreadExHome,
            TurnaroundAngle = sa.TurnaroundAngle,
            TurnaroundOffsetFromLine = sa.TurnaroundOffsetFromLine,
            RouteEfficiency = sa.RouteEfficiency,
            ForwardPathCompactness = sa.ForwardPathCompactness,
            ReturnPathCompactness = sa.ReturnPathCompactness,
            AvgSegmentBearing = sa.AvgSegmentBearing,
            BearingVariance = sa.BearingVariance,
            TotalRouteBearingChanges = sa.TotalRouteBearingChanges,
            ForwardSegmentCount = ctx.ForwardSegmentCount,
            ReturnPathSectorCoverage = ctx.ReturnPathSectorCoverage,
            ForwardWaypointCount = ctx.ForwardWaypointCount,
            ForwardSegmentBearings = ctx.ForwardSegmentBearings,
            RoutingCacheHits = s.CacheHits,
            RoutingCacheSize = s.CacheSize,
            QualityDeltaFromPrior = d.PreviousQuality >= 0 ? stats.QualityScore - d.PreviousQuality : 0,
            Attempt1FailedHighRepetition = d.Attempt == 1 && d.PreviousRepetition > 0.03,
            Attempt1FailedOvershoot = d.Attempt == 1 && stats.TotalDistanceKm > d.TargetDist * 1.5,
            MemoryBytesStart = d.MemStart,
            MemoryBytesEnd = GC.GetTotalMemory(false),
            MemoryBytesDelta = GC.GetTotalMemory(false) - d.MemStart,
            GcCollections = GC.CollectionCount(0) - d.GcStart,
            ProcessorCount = Environment.ProcessorCount,
            WorkingSetBytes = Environment.WorkingSet,
            PrivateMemoryBytes = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64,
            Gen0Collections = GC.CollectionCount(0) - d.GcStart,
            Gen1Collections = GC.CollectionCount(1) - d.Gc1Start,
            Gen2Collections = GC.CollectionCount(2) - d.Gc2Start,
            // Prefer pool's captured cache size when pool is active; fall back to singleton
            RouterDbFileSize = (_pool?.CacheFileSizeBytes ?? 0) > 0
                ? _pool!.CacheFileSizeBytes
                : (_mapRepository.CachePath != null && File.Exists(_mapRepository.CachePath)
                    ? new FileInfo(_mapRepository.CachePath).Length : 0),
            PeakMemoryBytes = GC.GetTotalMemory(false),
            RouteAssemblyMs = ctx.TotalSegmentBuildMs,
            RoadClassificationMs = rc.ClassificationMs,
            CoordinateResolutionMs = rc.ResolutionMs,
            TotalWallClockMs = d.AttemptElapsedMs,
            TopBottleneck = d.TopBottleneck.Key,
            BottleneckPct = d.BottleneckPct,
            ParallelizationOpportunity = d.Parallelization,
            RequestedDirectionBias = d.Request.Direction.ToString(),
            RequestedBearing = GeoConstants.DirectionToBearing(d.Request.Direction),
            DominantRouteDirection = GeoConstants.BearingToDirection(sa.TurnaroundBearing),
            TurnaroundCoordinates = new[] { sa.TurnaroundLat, sa.TurnaroundLon },
            ForwardWaypointCoordinates = ExtractForwardWaypointCoordinates(d.AllPoints, ctx.ForwardWaypointCount),
            ReturnPathNormalOverlap = ctx.ReturnPathDiagnostics?.NormalOverlap ?? 0,
            ReturnPathVeryHighPenaltyApplied = ctx.ReturnPathDiagnostics?.VeryHighPenaltyApplied ?? false,
            ReturnPathVeryHighPenaltyEdgeCount = ctx.ReturnPathDiagnostics?.VeryHighPenaltyEdgeCount ?? 0,
            ReturnPathVeryHighPenaltyOverlap = ctx.ReturnPathDiagnostics?.VeryHighPenaltyOverlap ?? 0,
            ReturnPathVeryHighPenaltyAccepted = ctx.ReturnPathDiagnostics?.VeryHighPenaltyAccepted ?? false,
            ReturnPathHighPenaltyApplied = ctx.ReturnPathDiagnostics?.HighPenaltyApplied ?? false,
            ReturnPathHighPenaltyEdgeCount = ctx.ReturnPathDiagnostics?.HighPenaltyEdgeCount ?? 0,
            ReturnPathHighPenaltyOverlap = ctx.ReturnPathDiagnostics?.HighPenaltyOverlap ?? 0,
            ReturnPathHighPenaltyAccepted = ctx.ReturnPathDiagnostics?.HighPenaltyAccepted ?? false,
            ReturnPathPushFallbackApplied = ctx.ReturnPathDiagnostics?.PushFallbackApplied ?? false,
            ReturnPathPushFallbackBestOverlap = ctx.ReturnPathDiagnostics?.PushFallbackBestOverlap ?? 0,
            ReturnPathPenaltyLevelUsed = ctx.ReturnPathDiagnostics?.PenaltyLevelUsed ?? "",
            RepetitionRootCause = ctx.ReturnPathDiagnostics?.RepetitionRootCause ?? "",
            ForwardPathTurnaroundAngle = ctx.ReturnPathDiagnostics?.ForwardPathTurnaroundAngle ?? 0,
            ForwardPathDetourRatio = ctx.ReturnPathDiagnostics?.ForwardPathDetourRatio ?? 0,
            ForwardPathEdgeDensity = ctx.ReturnPathDiagnostics?.ForwardPathEdgeDensity ?? 0,
            ForwardPathRoutingMs = ctx.ForwardPathRoutingMs,
            ForwardPathRoutingCalls = ctx.ForwardPathRoutingCalls,
            ReturnPathNormalRoutingMs = ctx.ReturnPathDiagnostics?.NormalRoutingMs ?? 0,
            ReturnPathVeryHighRoutingMs = ctx.ReturnPathDiagnostics?.VeryHighPenaltyRoutingMs ?? 0,
            ReturnPathHighRoutingMs = ctx.ReturnPathDiagnostics?.HighPenaltyRoutingMs ?? 0,
            ReturnPathPushFallbackRoutingMs = ctx.ReturnPathDiagnostics?.PushFallbackRoutingMs ?? 0,
            ReturnPathRoutingCalls = ctx.ReturnPathDiagnostics?.TotalRoutingCalls ?? 0,
            FindEdgesAlongLineMs = ctx.FindEdgesAlongLineMs,
            FindEdgesAlongLineCalls = ctx.FindEdgesAlongLineCalls,
            ResolveForwardEdgeIdsMs = ctx.ResolveForwardEdgeIdsMs,
            ResolveForwardEdgeIdsCalls = ctx.ResolveForwardEdgeIdsCalls,
            CountEdgesNearPointMs = ctx.CountEdgesNearPointMs,
            CountEdgesNearPointCalls = ctx.CountEdgesNearPointCalls,
            PenaltyEdgesMs = ctx.PenaltyEdgesMs,
            PenaltyEdgesEdgeCount = ctx.PenaltyEdgesEdgeCount,
            RestoreEdgesMs = ctx.RestoreEdgesMs,
            RestoreEdgesEdgeCount = ctx.RestoreEdgesEdgeCount,
            TurnaroundDensityCheckMs = ctx.TurnaroundDensityCheckMs,
            TurnaroundDensityCheckCalls = ctx.TurnaroundDensityCheckCalls,
        };
    }

    /// <summary>
    /// Temporary diagnostic: verify edge walk IDs actually block motorways.
    /// </summary>
    public Dictionary<string, object> ProbeMotorwayEdgeWalk()
    {
        return _edgeBlocker.ProbeEdgeWalkSafety(msg => StatusChanged?.Invoke(msg));
    }
}
