using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Itinero;
using Itinero.Profiles;
using MotoRouteFinder.Helpers;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Services;

public class RoutingService
{
    [DllImport("libc", SetLastError = true)]
    private static extern int malloc_trim(int pad);

    private readonly MapRepository _mapRepository = new();
    private readonly RoadClassifier _roadClassifier;
    private readonly EdgeBlocker _edgeBlocker;
    private readonly RouteAssembler _routeAssembler;
    private readonly WaypointGenerator _waypointGenerator;
    private readonly StemFixer _stemFixer;
    private readonly RouteStatistics _routeStatistics;
    private readonly DiagnosticsCollector _diagnostics = new();
    private readonly RouteBuilder _routeBuilder;
    private readonly Action<string> _statusForwarder;

    private const int MaxRouteAttempts = 3;
    private const double MaxRepetitionRatio = 0.05;
    private const int IdleTimeoutSeconds = 120; // 2 minutes
    private const int HeartbeatTimeoutSeconds = 60; // unload 60s after last heartbeat

    private Timer? _idleTimer;
    private Timer? _heartbeatTimer;
    private string? _cachedCachePath;
    private bool _avoidHighwaysCached;
    private bool _unloading;
    private DateTime _lastHeartbeat = DateTime.MinValue;

    public RoutingService()
    {
        _statusForwarder = msg => StatusChanged?.Invoke(msg);
        _roadClassifier = new RoadClassifier(_mapRepository);
        _edgeBlocker = new EdgeBlocker(_mapRepository);
        _routeAssembler = new RouteAssembler(_mapRepository, _roadClassifier);
        _waypointGenerator = new WaypointGenerator(_roadClassifier);
        _stemFixer = new StemFixer(_mapRepository, _roadClassifier, _routeAssembler, _edgeBlocker);
        _routeStatistics = new RouteStatistics(_roadClassifier);
        _routeBuilder = new RouteBuilder(_mapRepository, _roadClassifier, _routeAssembler, _waypointGenerator, _stemFixer, _routeStatistics, _diagnostics, _edgeBlocker);
        _idleTimer = new Timer(UnloadMapIdle, null, Timeout.Infinite, Timeout.Infinite);
        _heartbeatTimer = new Timer(CheckHeartbeat, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Creates a RoutingService using a pre-loaded MapRepository (for parallel candidate generation).
    /// No cache loading needed — the MapRepository is already initialized.
    /// </summary>
    public RoutingService(MapRepository mapRepository)
    {
        _statusForwarder = msg => StatusChanged?.Invoke(msg);
        _mapRepository = mapRepository;
        _roadClassifier = new RoadClassifier(_mapRepository);
        _edgeBlocker = new EdgeBlocker(_mapRepository);
        _routeAssembler = new RouteAssembler(_mapRepository, _roadClassifier);
        _waypointGenerator = new WaypointGenerator(_roadClassifier);
        _stemFixer = new StemFixer(_mapRepository, _roadClassifier, _routeAssembler, _edgeBlocker);
        _routeStatistics = new RouteStatistics(_roadClassifier);
        _routeBuilder = new RouteBuilder(_mapRepository, _roadClassifier, _routeAssembler, _waypointGenerator, _stemFixer, _routeStatistics, _diagnostics, _edgeBlocker);
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
        _idleTimer?.Change(TimeSpan.FromSeconds(IdleTimeoutSeconds), Timeout.InfiniteTimeSpan);
    }

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

            StatusChanged?.Invoke($"Idle timer fired — unloading map to free memory...");

            // Clear all caches before releasing the RouterDb
            _roadClassifier.ClearCache();
            MapRepository.ClearStaticCache();

            // Release the RouterDb
            _mapRepository.ClearMaps();

            // Force full GC collection with LOH compaction
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();

            // On Linux, tell glibc to return free pages to the OS
            if (OperatingSystem.IsLinux())
            {
                malloc_trim(0);
            }

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
    private async Task EnsureMapLoadedAsync()
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
        _heartbeatTimer?.Change(TimeSpan.FromSeconds(HeartbeatTimeoutSeconds), TimeSpan.FromSeconds(HeartbeatTimeoutSeconds));
    }

    /// <summary>
    /// Checks if the heartbeat has timed out. If no heartbeat for HeartbeatTimeoutSeconds, unloads the map.
    /// </summary>
    private void CheckHeartbeat(object? state)
    {
        if (_unloading || !_mapRepository.IsLoaded) return;
        if (_lastHeartbeat == DateTime.MinValue) return;

        var elapsed = (DateTime.UtcNow - _lastHeartbeat).TotalSeconds;
        if (elapsed > HeartbeatTimeoutSeconds)
        {
            UnloadMapIdle(null);
        }
    }

    public async Task LoadMapAsync(string osmPbfPath, bool avoidHighways = false)
    {
        _mapRepository.StatusChanged += _statusForwarder;
        await _mapRepository.LoadMapAsync(osmPbfPath, avoidHighways);
        _cachedCachePath = _mapRepository.CachePath;
        _avoidHighwaysCached = avoidHighways;
        TouchIdleTimer();
        StartHeartbeatMonitor();
    }

    public async Task LoadMapsAsync(string[] osmPbfPaths, bool avoidHighways = false)
    {
        _mapRepository.StatusChanged += _statusForwarder;
        await _mapRepository.LoadMapsAsync(osmPbfPaths, avoidHighways);
        _cachedCachePath = _mapRepository.CachePath;
        _avoidHighwaysCached = avoidHighways;
        TouchIdleTimer();
        StartHeartbeatMonitor();
    }

    public async Task LoadCacheAsync(string cachePath)
    {
        _mapRepository.StatusChanged += _statusForwarder;
        await _mapRepository.LoadCacheAsync(cachePath);
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
        _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _lastHeartbeat = DateTime.MinValue;
        _cachedCachePath = null;

        _roadClassifier.ClearCache();
        MapRepository.ClearStaticCache();
        _mapRepository.ClearMaps();

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        if (OperatingSystem.IsLinux())
        {
            malloc_trim(0);
        }
    }

    public async Task<RouteResponse> GenerateLoopRouteAsync(RouteRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureMapLoadedAsync();

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

    public RouteResponse GenerateLoopRoute(RouteRequest request, CancellationToken cancellationToken = default)
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
    /// Generates multiple route candidates in parallel using separate RouterDb instances from the pool.
    /// Returns the best candidate (by QualityScore) and all candidates for comparison.
    /// </summary>
    public static (RouteResponse best, RouteResponse[] allCandidates) GenerateLoopRouteCandidates(
        RouteRequest request,
        RouterDbPool pool,
        int candidateCount,
        CancellationToken cancellationToken = default)
    {
        var repos = pool.CheckoutMultiple(candidateCount);
        var candidates = new RouteResponse[candidateCount];

        try
        {
            Parallel.For(0, candidateCount, new ParallelOptions { MaxDegreeOfParallelism = candidateCount }, i =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var service = new RoutingService(repos[i]);
                candidates[i] = service.GenerateLoopRoute(request, cancellationToken);
            });
        }
        finally
        {
            pool.CheckinMultiple(repos);
        }

        // Pick best by QualityScore
        var best = candidates
            .Where(c => c?.Stats != null)
            .OrderByDescending(c => c!.Stats.QualityScore)
            .First()!;

        return (best, candidates);
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
        StatusChanged?.Invoke($"Generating route (attempt 1/{MaxRouteAttempts})...");

        _diagnostics.Clear();

        double bestRepetition = double.MaxValue;
        double bestQuality = -1;
        RouteResponse? bestRoute = null;
        RouteResponse? bestRouteWithinCap = null;
        double bestRepetitionWithinCap = double.MaxValue;
        int bestAttempt = 0;
        double previousRepetition = double.MaxValue;
        double previousQuality = -1;
        var previousGeometries = new List<List<Coordinate>>();
        var attemptElapsedMs = new List<double>();
        var attemptRepetitionRatio = new List<double>();
        var attemptQualityScore = new List<double>();
        var attemptOverlapWithPrevious = new List<double>();
        HashSet<int>? previousBlockedSectors = null;

        for (int attempt = 0; attempt < MaxRouteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptSw = System.Diagnostics.Stopwatch.StartNew();
            long memStart = GC.GetTotalMemory(false);
            int gcStart = GC.CollectionCount(0);
            int gc1Start = GC.CollectionCount(1);
            int gc2Start = GC.CollectionCount(2);
            StatusChanged?.Invoke($"Generating route (attempt {attempt + 1}/{MaxRouteAttempts})...");
            ProgressChanged?.Invoke((double)attempt / MaxRouteAttempts);

            // Start with alternative path (GraphHopper-inspired) for best repetition avoidance
            // Fall back to progressive loop only if alternative fails
            // Turnaround distance varies by attempt: 45%, 55%, 50% for geometric diversity
            double turnaroundRatio = attempt switch
            {
                0 => 0.45,
                1 => 0.55,
                _ => 0.50
            };
            var result = _routeBuilder.BuildAlternativeLoop(profile, request.Start, targetDist, request.AvoidHighways, cancellationToken, attemptNumber: attempt + 1, turnaroundRatio: turnaroundRatio, direction: request.Direction);

            // If alternative failed or produced too many stems, fall back to progressive
            if (result == null || (result.Value.context.MaxConsecutiveStemSegments > 2))
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

            // Compute attempt diversity (overlap with previous attempts)
            double overlapWithPrevious = 0;
            bool isDuplicate = false;
            if (previousGeometries.Count > 0)
            {
                var currentEdges = new HashSet<RouteGeometryUtils.EdgeKey>();
                for (int i = 0; i < geometry.Count - 1; i++)
                    currentEdges.Add(RouteGeometryUtils.MakeEdgeKey(geometry[i], geometry[i + 1]));

                double maxOverlap = 0;
                foreach (var prevGeom in previousGeometries)
                {
                    var prevEdges = new HashSet<RouteGeometryUtils.EdgeKey>();
                    for (int i = 0; i < prevGeom.Count - 1; i++)
                        prevEdges.Add(RouteGeometryUtils.MakeEdgeKey(prevGeom[i], prevGeom[i + 1]));

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
            previousGeometries.Add(geometry);
            var statsSw = System.Diagnostics.Stopwatch.StartNew();
            var stats = _routeStatistics.CalculateStats(geometry, profile);
            statsSw.Stop();
            var repSw = System.Diagnostics.Stopwatch.StartNew();
            var repetition = _routeStatistics.CalculateRepetition(geometry);
            repSw.Stop();

            var (stemsDetected, stemsFixed, stemsDropped, overlapTriggered, pushReroutes) = _diagnostics.CountAll();

            // Calculate circularity score (bearing spread + sector coverage + compactness)
            stats.CircularityScoreComponent = Math.Round(RouteGeometryUtils.CalculateCircularityScore(geometry), 1);
            stats.QualityScore = RouteStats.CalculateQualityScore(stats, repetition, stemsDetected, stemsDropped, stemsFixed, targetDist);

            // Quality component breakdown (v2 formula)
            stats.RepetitionScoreComponent = Math.Round(Math.Max(0, 100 - stats.RepetitionRatio * 1000), 1);
            stats.CurvatureScoreComponent = Math.Round(Math.Exp(-Math.Pow((stats.AverageCurvature - 0.001) / 0.002, 2)) * 100, 1);
            double preferredKm = stats.RoadQualityKm.TryGetValue("Preferred", out double pk) ? pk : 0;
            double totalKmForScore = stats.TotalDistanceKm > 0 ? stats.TotalDistanceKm : 1;
            stats.StemPenaltyComponent = Math.Round((double)Math.Max(0, 100 - stemsDropped * 20 - stemsFixed * 5), 1);
            double deviation = targetDist > 0 ? Math.Abs(stats.TotalDistanceKm - targetDist) / targetDist : 0;
            stats.DistAccuracyComponent = Math.Round(Math.Max(0, 100 - deviation * 100), 1);

            var edgeReuseCounts = new Dictionary<RouteGeometryUtils.EdgeKey, int>();
            for (int i = 0; i < geometry.Count - 1; i++)
            {
                var key = RouteGeometryUtils.MakeEdgeKey(geometry[i], geometry[i + 1]);
                var revKey = key.Reversed();
                if (edgeReuseCounts.ContainsKey(key))
                    edgeReuseCounts[key]++;
                else if (edgeReuseCounts.ContainsKey(revKey))
                    edgeReuseCounts[revKey]++;
                else
                    edgeReuseCounts[key] = 1;
            }
            int maxEdgeReuse = edgeReuseCounts.Count > 0 ? edgeReuseCounts.Values.Max() : 0;
            double avgEdgeReuse = edgeReuseCounts.Count > 0 ? edgeReuseCounts.Values.Average() : 0;

            var stemsByRoadType = new Dictionary<string, int>();
            var stemsByDistanceBand = new Dictionary<string, int> { ["0-25%"] = 0, ["25-50%"] = 0, ["50-75%"] = 0, ["75-100%"] = 0 };
            int forwardStemCount = 0, returnStemCount = 0;
            foreach (var diag in _diagnostics.GetAllStemEvents())
            {
                if (diag is DebugStemEvent evt && evt.IsStem)
                {
                    string roadType = evt.RoadType ?? "unknown";
                    stemsByRoadType[roadType] = stemsByRoadType.GetValueOrDefault(roadType) + 1;

                    double distRatio = targetDist > 0 ? evt.DistanceFromStartM / (targetDist * 1000) : 0;
                    if (distRatio < 0.25) stemsByDistanceBand["0-25%"]++;
                    else if (distRatio < 0.50) stemsByDistanceBand["25-50%"]++;
                    else if (distRatio < 0.75) stemsByDistanceBand["50-75%"]++;
                    else stemsByDistanceBand["75-100%"]++;

                    if (evt.SegmentRole == "forward") forwardStemCount++;
                    else if (evt.SegmentRole == "return") returnStemCount++;
                }
            }

            double avgSegmentLength = context.SegmentLengths.Count > 0 ? context.SegmentLengths.Average() : 0;

            attemptElapsedMs.Add(attemptSw.ElapsedMilliseconds);
            attemptRepetitionRatio.Add(stats.RepetitionRatio);
            attemptQualityScore.Add(stats.QualityScore);
            attemptOverlapWithPrevious.Add(overlapWithPrevious);

            // Phase 4: Bottleneck analysis (compute BEFORE record creation)
            var timingFields = new Dictionary<string, long>
            {
                ["RoutingCalls"] = _routeAssembler.PerRouteRoutingCallsMs,
                ["RoadClassification"] = _roadClassifier.ClassificationMs,
                ["CoordinateResolution"] = _roadClassifier.ResolutionMs,
                ["StemDetection"] = context.StemDetectionMs,
                ["FixPipeline"] = _stemFixer.FixStemTotalMs,
                ["OverlapCalc"] = context.OverlapCalcMs,
                ["MotorwayBlocking"] = _edgeBlocker.FindMotorwayMs,
            };
            var topBottleneck = timingFields.OrderByDescending(kvp => kvp.Value).First();
            long totalTimingMs = timingFields.Values.Sum();
            double bottleneckPct = totalTimingMs > 0
                ? Math.Round((double)topBottleneck.Value / totalTimingMs * 100, 1)
                : 0;

            string parallelization = "";
            if (_roadClassifier.ClassificationMs > attemptSw.ElapsedMilliseconds * 0.2)
                parallelization = "Road classification is bottleneck. Could cache results or parallelize per-sample-point.";
            else if (_roadClassifier.ResolutionMs > attemptSw.ElapsedMilliseconds * 0.2)
                parallelization = "Coordinate resolution is bottleneck. Could batch-resolve points.";
            else if (_edgeBlocker.FindMotorwayMs > attemptSw.ElapsedMilliseconds * 0.15)
                parallelization = "Motorway blocking is bottleneck. Edge enumeration could run on background thread.";
            else if (context.StemDetectionMs > attemptSw.ElapsedMilliseconds * 0.15)
                parallelization = "Stem detection is bottleneck. Could run in parallel with segment building.";

            // Analyze route shape for circularity diagnostics
            var shapeAnalysis = RouteGeometryUtils.AnalyzeRouteShape(geometry, request.Start);

            // Single pass over all stem events for diagnostics (replaces 8 separate passes)
            var singlePassDiagnostics = ComputeAllDiagnosticsInSinglePass();

            _diagnostics.Add(new DebugRouteSummary
            {
                AttemptNumber = attempt + 1,
                TargetDistanceKm = targetDist,
                TotalDistanceKm = stats.TotalDistanceKm,
                WaypointCount = allPoints.Count,
                RepetitionRatio = stats.RepetitionRatio,
                EdgeOverlapM = Math.Round(repetition.EdgeOverlapM, 0),
                OutAndBackOverlapM = Math.Round(repetition.OutAndBackM, 0),
                StemOverlapM = Math.Round(repetition.StemOverlapM, 0),
                TotalOverlapM = Math.Round(repetition.EdgeOverlapM + repetition.OutAndBackM + repetition.StemOverlapM, 0),
                SegmentsTotal = _diagnostics.CountSegmentsTotal(),
                RequestedWaypointCount = request.WaypointCount,
                BuilderMethod = context.BuilderMethod,
                StemsDetected = stemsDetected,
                StemsFixed = stemsFixed,
                StemsDropped = stemsDropped,
                SegmentsOverlapTriggered = overlapTriggered,
                PushReroutesSucceeded = pushReroutes,
                MaxEdgeReuse = maxEdgeReuse,
                AvgEdgeReuse = Math.Round(avgEdgeReuse, 2),
                ElapsedMs = attemptSw.ElapsedMilliseconds,
                Resolution = stats.RepetitionRatio <= MaxRepetitionRatio ? "winner" : "attempt_complete",
                StartPoint = new[] { request.Start.Lat, request.Start.Lon },
                StemsByRoadType = stemsByRoadType,
                StemsByDistanceBand = stemsByDistanceBand,
                BlockedSectorCounts = context.BlockedSectorCounts.ToArray(),
                FixStemTotalMs = _stemFixer.FixStemTotalMs,
                ReplacementTotalMs = _stemFixer.ReplacementTotalMs,
                MultiHopTotalMs = _stemFixer.MultiHopTotalMs,
                PushAttemptsUsed = context.TotalPushAttemptsUsed,
                RepetitionDeltaFromPrior = previousRepetition < double.MaxValue ? previousRepetition - stats.RepetitionRatio : 0,
                UniqueEdgeCount = edgeReuseCounts.Count,
                TotalEdgeCount = edgeReuseCounts.Values.Sum(),
                AverageSegmentLengthM = Math.Round(avgSegmentLength, 0),
                MinLat = context.MinLat,
                MaxLat = context.MaxLat,
                MinLon = context.MinLon,
                MaxLon = context.MaxLon,
                ForwardStemCount = forwardStemCount,
                ReturnStemCount = returnStemCount,
                WaypointRejectionReasons = context.WaypointRejectionReasons,
                QualityScore = stats.QualityScore,
                StemsTimedOut = _diagnostics.CountStemsTimedOut(),
                NearMissCount = _diagnostics.CountNearMisses(),
                PrivateRoadDetectedCount = _diagnostics.CountPrivateRoads(),
                FixFailureByReasonCode = _diagnostics.GetFixFailureByReasonCode(),
                OvershootRatio = Math.Round(stats.TotalDistanceKm / Math.Max(targetDist, 0.1), 2),
                FindMotorwayMs = _edgeBlocker.FindMotorwayMs,
                CalculateStatsMs = statsSw.ElapsedMilliseconds,
                CalculateRepetitionMs = repSw.ElapsedMilliseconds,
                WaypointGenMs = context.WaypointGenMs,
                ConnectivityCheckMs = context.ConnectivityCheckMs,
                StemDetectionMs = context.StemDetectionMs,
                OverlapCalcMs = context.OverlapCalcMs,
                RoutingCallsMs = _routeAssembler.PerRouteRoutingCallsMs,
                IntermediateTotalMs = context.IntermediateTotalMs,
                ResolveCount = _roadClassifier.ResolveCount,
                RoutingCount = _routeAssembler.PerRouteRoutingCount,
                BlockEdgesCount = _edgeBlocker.BlockEdgesCount,
                OverlapWithPreviousAttempt = Math.Round(overlapWithPrevious, 3),
                IsDuplicateGeometry = isDuplicate,
                FixStemCallCount = _stemFixer.FixStemCallCount,
                FixStemSuccessCount = _stemFixer.FixStemSuccessCount,
                ReplacementCallCount = _stemFixer.ReplacementCallCount,
                ReplacementSuccessCount = _stemFixer.ReplacementSuccessCount,
                MultiHopCallCount = _stemFixer.MultiHopCallCount,
                MultiHopSuccessCount = _stemFixer.MultiHopSuccessCount,
                StemsByRootCause = singlePassDiagnostics.stemsByRootCause,
                ResolveCacheHitCount = _roadClassifier.ResolveCacheHitCount,
                ConnectivityCacheHitCount = _roadClassifier.ConnectivityCacheHitCount,
                HomingWaypointsUsed = context.HomingWaypointsUsed,
                FinalEdgeSetSize = context.FinalEdgeSetSize,
                MotorwayEdgesFound = _edgeBlocker.MotorwayEdgesFound,
                GridPointsSampled = _edgeBlocker.GridPointsSampled,
                TotalWaypointAttempts = context.TotalWaypointAttempts,
                TotalSegmentBuildMs = context.TotalSegmentBuildMs,
                StemClusters = ComputeStemClusters(attempt + 1),
                // Session 9: New diagnostic fields (single pass over all stem events)
                ReturnOverlapM = singlePassDiagnostics.returnOverlapM,
                ReturnSegmentLengthM = singlePassDiagnostics.returnSegmentLength,
                OverlapBySegmentPosition = singlePassDiagnostics.overlapByPosition,
                StemsAcceptedWithHighOverlap = singlePassDiagnostics.stemsAcceptedHighOverlap,
                EdgeSaturationRatio = edgeReuseCounts.Count > 0 ? Math.Round((double)edgeReuseCounts.Count / edgeReuseCounts.Values.Sum(), 3) : 0,
                AttemptFailedEarlyAbort = context.AttemptFailedEarlyAbort,
                MaxConsecutiveStemSegments = context.MaxConsecutiveStemSegments,
                NullSegmentDrops = context.NullSegmentDrops,
                FixStrategyDistribution = singlePassDiagnostics.fixStrategyDist,
                FixStrategySuccessRates = singlePassDiagnostics.fixStrategyRates,
                AvgFixPipelineMsPerStem = stemsDetected > 0 ? Math.Round((double)_stemFixer.FixStemTotalMs / stemsDetected, 1) : 0,
                WastedRoutingCalls = _routeAssembler.PerRouteRoutingCount - _diagnostics.CountSegmentsTotal(),
                WaypointDistributionUniformity = singlePassDiagnostics.waypointUniformity,
                HomingSuccessRate = context.HomingWaypointsUsed > 0 ? 1.0 : 0,
                MotorwayKm = stats.MotorwayKm,
                MotorwayPct = stats.MotorwayPct,
                // Phase 1: Geographic spread
                CentroidLat = stats.CentroidLat,
                CentroidLon = stats.CentroidLon,
                BoundingBoxAreaKm2 = stats.BoundingBoxAreaKm2,
                CompactnessRatio = stats.CompactnessRatio,
                MaxDistanceFromStartKm = stats.MaxDistanceFromStartKm,
                SectorsVisited = stats.SectorsVisited,
                // Phase 1: Road type diversity
                DominantRoadType = stats.DominantRoadType,
                DominantRoadTypePct = stats.DominantRoadTypePct,
                ResidentialKm = stats.ResidentialKm,
                LivingStreetKm = stats.LivingStreetKm,
                // Phase 2: Turning analysis
                TurnCount = stats.TurnCount,
                SharpTurnCount = stats.SharpTurnCount,
                HairpinCount = stats.HairpinCount,
                AverageTurnAngle = stats.AverageTurnAngle,
                StraightLineRatio = stats.StraightLineRatio,
                // Phase 3: Road variety
                RoadTypeTransitions = stats.RoadTypeTransitions,
                // Quality component breakdown
                RepetitionScoreComponent = stats.RepetitionScoreComponent,
                CurvatureScoreComponent = stats.CurvatureScoreComponent,
                StemPenaltyComponent = stats.StemPenaltyComponent,
                DistAccuracyComponent = stats.DistAccuracyComponent,
                CircularityScoreComponent = stats.CircularityScoreComponent,
                // Overshoot analysis
                ForwardDistanceKm = context.ForwardDistanceKm,
                ReturnDistanceKm = context.ReturnDistanceKm,
                ForwardPctOfTotal = stats.TotalDistanceKm > 0 ? Math.Round(context.ForwardDistanceKm / stats.TotalDistanceKm * 100, 1) : 0,
                ReturnRatio = context.ReturnRatio,
                ForwardLoopExitReason = context.ForwardLoopExitReason,
                // Return segment quality
                ReturnSegmentCurvature = context.ReturnSegmentCurvature,
                ReturnSegmentRoadType = context.ReturnSegmentRoadType,
                ReturnSegmentOverlapPct = context.ReturnSegmentOverlapPct,
                // Return path diagnostics
                ReturnPushAttempts = context.ReturnPushAttempts,
                ReturnOverlapBeforePush = context.ReturnOverlapBeforePush,
                ReturnOverlapAfterPush = context.ReturnOverlapAfterPush,
                EstReturnAtLoopExit = context.EstReturnAtLoopExit,
                ActualReturnVsEstimate = context.ActualReturnVsEstimate,
                ForwardHaversineAtExit = context.ForwardHaversineAtExit,
                SectorsBlockedAtReturn = context.SectorsBlockedAtReturn,
                ReturnSegmentRerouteCount = context.ReturnSegmentRerouteCount,
                // Route shape diagnostics
                ForwardBearingSpread = shapeAnalysis.ForwardBearingSpread,
                ReturnBearingSpread = shapeAnalysis.ReturnBearingSpread,
                TurnaroundBearing = shapeAnalysis.TurnaroundBearing,
                ForwardPathCurvature = shapeAnalysis.ForwardPathCurvature,
                ReturnPathCurvature = shapeAnalysis.ReturnPathCurvature,
                ForwardMaxDeviationFromLine = shapeAnalysis.ForwardMaxDeviationFromLine,
                ReturnMaxDeviationFromLine = shapeAnalysis.ReturnMaxDeviationFromLine,
                ForwardDistinctBearingCount = shapeAnalysis.ForwardDistinctBearingCount,
                ReturnDistinctBearingCount = shapeAnalysis.ReturnDistinctBearingCount,
                ForwardReturnSectorDifference = shapeAnalysis.ForwardReturnSectorDifference,
                ForwardPathWindingNumber = shapeAnalysis.ForwardPathWindingNumber,
                ForwardBearingSpreadExHome = shapeAnalysis.ForwardBearingSpreadExHome,
                ReturnBearingSpreadExHome = shapeAnalysis.ReturnBearingSpreadExHome,
                TurnaroundAngle = shapeAnalysis.TurnaroundAngle,
                TurnaroundOffsetFromLine = shapeAnalysis.TurnaroundOffsetFromLine,
                RouteEfficiency = shapeAnalysis.RouteEfficiency,
                ForwardPathCompactness = shapeAnalysis.ForwardPathCompactness,
                ReturnPathCompactness = shapeAnalysis.ReturnPathCompactness,
                AvgSegmentBearing = shapeAnalysis.AvgSegmentBearing,
                BearingVariance = shapeAnalysis.BearingVariance,
                TotalRouteBearingChanges = shapeAnalysis.TotalRouteBearingChanges,
                // Circularity diagnostics
                ForwardSegmentCount = context.ForwardSegmentCount,
                ReturnPathSectorCoverage = context.ReturnPathSectorCoverage,
                ForwardWaypointCount = context.ForwardWaypointCount,
                ForwardWaypointAngles = context.ForwardWaypointAngles,
                ForwardSegmentBearings = context.ForwardSegmentBearings,
                // Cache effectiveness
                RoutingCacheHits = _routeAssembler.CacheHits,
                RoutingCacheSize = _routeAssembler.CacheSize,
                // Attempt comparison
                QualityDeltaFromPrior = previousQuality >= 0 ? stats.QualityScore - previousQuality : 0,
                Attempt1FailedHighRepetition = attempt == 1 && previousRepetition > 0.03,
                Attempt1FailedOvershoot = attempt == 1 && stats.TotalDistanceKm > targetDist * 1.5,
                // Phase 4: Performance - CPU profiling
                MemoryBytesStart = memStart,
                MemoryBytesEnd = GC.GetTotalMemory(false),
                MemoryBytesDelta = GC.GetTotalMemory(false) - memStart,
                GcCollections = GC.CollectionCount(0) - gcStart,
                ProcessorCount = Environment.ProcessorCount,
                // Phase 4: Performance - Memory diagnostics
                WorkingSetBytes = Environment.WorkingSet,
                PrivateMemoryBytes = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64,
                Gen0Collections = GC.CollectionCount(0) - gcStart,
                Gen1Collections = GC.CollectionCount(1) - gc1Start,
                Gen2Collections = GC.CollectionCount(2) - gc2Start,
                RouterDbFileSize = _mapRepository.CachePath != null && File.Exists(_mapRepository.CachePath)
                    ? new FileInfo(_mapRepository.CachePath).Length : 0,
                PeakMemoryBytes = GC.GetTotalMemory(false),
                // Phase 4: Performance - Per-method timing
                RouteAssemblyMs = context.TotalSegmentBuildMs,
                RoadClassificationMs = _roadClassifier.ClassificationMs,
                CoordinateResolutionMs = _roadClassifier.ResolutionMs,
                TotalWallClockMs = attemptSw.ElapsedMilliseconds,
                // Phase 4: Bottleneck analysis
                TopBottleneck = topBottleneck.Key,
                BottleneckPct = bottleneckPct,
                ParallelizationOpportunity = parallelization,
                // Direction diagnostics
                RequestedDirectionBias = request.Direction.ToString(),
                RequestedBearing = DirectionToBearing(request.Direction),
                DominantRouteDirection = BearingToDirection(shapeAnalysis.TurnaroundBearing),
                TurnaroundCoordinates = new[] { shapeAnalysis.TurnaroundLat, shapeAnalysis.TurnaroundLon },
                ForwardWaypointCoordinates = ExtractForwardWaypointCoordinates(allPoints, context.ForwardWaypointCount),
            });

            previousRepetition = stats.RepetitionRatio;
            previousQuality = stats.QualityScore;

            if (stats.RepetitionRatio < bestRepetition)
            {
                bestRepetition = stats.RepetitionRatio;
                bestQuality = stats.QualityScore;
                bestAttempt = attempt + 1;
                bestRoute = new RouteResponse
                {
                    RouteGeometry = geometry,
                    Stats = stats,
                    Waypoints = allPoints,
                    RepetitionSegments = RouteGeometryUtils.ExtractRepetitionSegments(geometry, _routeStatistics.ReturnToStartIndex),
                    StemDiagnosticsJson = _diagnostics.ToJson(),
                    RepetitionBreakdown = repetition,
                };
            }
            else if (stats.RepetitionRatio <= bestRepetition * 1.02 && stats.QualityScore > bestQuality)
            {
                // Tiebreaker: use QS when RR values are within 2%
                bestRepetition = stats.RepetitionRatio;
                bestQuality = stats.QualityScore;
                bestAttempt = attempt + 1;
                bestRoute = new RouteResponse
                {
                    RouteGeometry = geometry,
                    Stats = stats,
                    Waypoints = allPoints,
                    RepetitionSegments = RouteGeometryUtils.ExtractRepetitionSegments(geometry, _routeStatistics.ReturnToStartIndex),
                    StemDiagnosticsJson = _diagnostics.ToJson(),
                    RepetitionBreakdown = repetition,
                };
            }

            if (stats.TotalDistanceKm <= targetDist * 1.5 && stats.RepetitionRatio < bestRepetitionWithinCap)
            {
                bestRepetitionWithinCap = stats.RepetitionRatio;
                bestRouteWithinCap = new RouteResponse
                {
                    RouteGeometry = geometry,
                    Stats = stats,
                    Waypoints = allPoints,
                    RepetitionSegments = RouteGeometryUtils.ExtractRepetitionSegments(geometry, _routeStatistics.ReturnToStartIndex),
                    StemDiagnosticsJson = _diagnostics.ToJson(),
                    RepetitionBreakdown = repetition,
                };
            }

            if (stats.RepetitionRatio <= MaxRepetitionRatio)
            {
                StatusChanged?.Invoke($"Route generated (attempt {attempt + 1}, {stats.RepetitionRatio:P0} repetition)");
                ProgressChanged?.Invoke(1.0);
                return bestRoute!;
            }

            if (stats.QualityScore >= 45 && stats.RepetitionRatio <= 0.05)
            {
                StatusChanged?.Invoke($"Route generated (attempt {attempt + 1}, QS={stats.QualityScore:F0}, RR={stats.RepetitionRatio:P0})");
                ProgressChanged?.Invoke(1.0);
                return bestRoute!;
            }

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
                TotalAttempts = MaxRouteAttempts,
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
                StemsTimedOut = _diagnostics.CountStemsTimedOut(),
                TotalResolveCount = _diagnostics.SumResolveCount(),
                TotalRoutingCount = _diagnostics.SumRoutingCount(),
                TotalBlockEdgesCount = _diagnostics.SumBlockEdgesCount(),
                AttemptElapsedMs = attemptElapsedMs,
                AttemptRepetitionRatio = attemptRepetitionRatio,
                AttemptQualityScore = attemptQualityScore,
                AttemptOverlapWithPrevious = attemptOverlapWithPrevious,
                FinalRoadQualityKm = returnRoute.Stats.RoadQualityKm,
                // Session 9: New diagnostic fields
                AttemptStemCounts = ComputeAttemptStemCounts(),
                AttemptNullDropCounts = ComputeAttemptNullDropCounts(),
                BlockSectorEffectiveness = ComputeBlockSectorEffectiveness(),
                PerAttemptReturnOverlapM = ComputePerAttemptReturnOverlapM(),
                // Direction diagnostics
                RequestedDirectionBias = request.Direction.ToString(),
                RequestedBearing = DirectionToBearing(request.Direction),
                DominantRouteDirection = BearingToDirection(finalShapeAnalysis.TurnaroundBearing),
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
            });
            returnRoute.StemDiagnosticsJson = _diagnostics.ToJson();
            StatusChanged?.Invoke($"Route generated (best of {MaxRouteAttempts} attempts, {bestRepetition:P0} repetition)");
            ProgressChanged?.Invoke(1.0);
            return returnRoute!;
        }

        // Graceful fallback: try a simple route with minimal waypoints
        StatusChanged?.Invoke("All attempts failed, trying fallback route...");
        var fallbackResult = _routeBuilder.BuildProgressiveLoop(profile, request.Start, targetDist, 3, request.AvoidHighways, cancellationToken, attemptNumber: 0);
        if (fallbackResult != null)
        {
            var (fallbackGeometry, fallbackPoints, fallbackContext) = fallbackResult.Value;
            var fallbackStats = _routeStatistics.CalculateStats(fallbackGeometry, profile);
            var fallbackRepetition = _routeStatistics.CalculateRepetition(fallbackGeometry);
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

    private List<StemCluster> ComputeStemClusters(int attemptNumber)
    {
        const double gridSizeDegrees = 0.01; // ~1km grid
        var clusters = new Dictionary<(int, int), StemCluster>();

        // Optimization: Accumulate overlap during first pass (O(N) instead of O(N²))
        foreach (var e in _diagnostics.GetAllStemEvents())
        {
            if (e is not DebugStemEvent evt || evt.AttemptNumber != attemptNumber) continue;
            if (evt.SegmentRole != "forward") continue;

            int gridLat = (int)(evt.MidpointLat / gridSizeDegrees);
            int gridLon = (int)(evt.MidpointLon / gridSizeDegrees);
            var key = (gridLat, gridLon);

            if (!clusters.TryGetValue(key, out var cluster))
            {
                cluster = new StemCluster { GridLat = gridLat, GridLon = gridLon };
                clusters[key] = cluster;
            }

            cluster.StemCount++;
            cluster.SegmentIndices.Add(evt.SegmentIndex);
            // Accumulate overlap during first pass
            cluster.AvgOverlapRatio += evt.OverlapWithPriorSegments;
        }

        // Finalize average overlap (divide by count)
        foreach (var cluster in clusters.Values)
        {
            cluster.AvgOverlapRatio = cluster.StemCount > 0 
                ? Math.Round(cluster.AvgOverlapRatio / cluster.StemCount, 3) 
                : 0;
        }

        return clusters.Values.OrderByDescending(c => c.StemCount).ToList();
    }

    // Session 9: Helper methods for new diagnostic fields — single pass over all stem events
    private (Dictionary<string, int> stemsByRootCause, double returnOverlapM, double returnSegmentLength,
        Dictionary<string, double> overlapByPosition, int stemsAcceptedHighOverlap,
        Dictionary<string, int> fixStrategyDist, Dictionary<string, double> fixStrategyRates,
        double waypointUniformity) ComputeAllDiagnosticsInSinglePass()
    {
        var stemsByRootCause = new Dictionary<string, int>();
        double returnOverlapM = 0;
        double returnSegmentLength = 0;
        var overlapByPosition = new Dictionary<string, double> { ["0-25%"] = 0, ["25-50%"] = 0, ["50-75%"] = 0, ["75-100%"] = 0 };
        int stemsAcceptedHighOverlap = 0;
        var fixStrategyDist = new Dictionary<string, int>();
        var fixAttempts = new Dictionary<string, int>();
        var fixSuccesses = new Dictionary<string, int>();
        var sectors = new int[18];
        int totalSegments = _diagnostics.CountSegmentsTotal();

        foreach (var e in _diagnostics.GetAllStemEvents())
        {
            if (e is not DebugStemEvent evt) continue;

            // stemsByRootCause
            if (evt.IsStem)
            {
                string rootCauseKey = evt.RootCause.ToString();
                stemsByRootCause[rootCauseKey] = stemsByRootCause.GetValueOrDefault(rootCauseKey) + 1;
            }

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

            // stemsAcceptedHighOverlap
            if (evt.IsStem && evt.OverlapWithPriorSegments > 0.15)
            {
                if (evt.Resolution == "timeout_accepted" || evt.Resolution == "fixFailed_butAccepted")
                    stemsAcceptedHighOverlap++;
            }

            // fixStrategyDist & fixStrategyRates
            if (evt.FixStrategy != null)
                fixStrategyDist[evt.FixStrategy] = fixStrategyDist.GetValueOrDefault(evt.FixStrategy) + 1;

            if (evt.TryFixStem?.Attempted == true)
            {
                fixAttempts["tryFix"] = fixAttempts.GetValueOrDefault("tryFix") + 1;
                if (evt.TryFixStem.Succeeded) fixSuccesses["tryFix"] = fixSuccesses.GetValueOrDefault("tryFix") + 1;
            }
            if (evt.GenerateReplacement?.Attempted == true)
            {
                fixAttempts["replacement"] = fixAttempts.GetValueOrDefault("replacement") + 1;
                if (evt.GenerateReplacement.Succeeded) fixSuccesses["replacement"] = fixSuccesses.GetValueOrDefault("replacement") + 1;
            }
            if (evt.Intermediate?.Attempted == true)
            {
                fixAttempts["intermediate"] = fixAttempts.GetValueOrDefault("intermediate") + 1;
                if (evt.Intermediate.Succeeded) fixSuccesses["intermediate"] = fixSuccesses.GetValueOrDefault("intermediate") + 1;
            }
            if (evt.HopCount > 0)
            {
                fixAttempts["multiHop"] = fixAttempts.GetValueOrDefault("multiHop") + 1;
                if (evt.Resolution == "multiHop") fixSuccesses["multiHop"] = fixSuccesses.GetValueOrDefault("multiHop") + 1;
            }

            // waypointUniformity (forward only)
            if (evt.SegmentRole == "forward")
            {
                int sector = (int)(evt.SectorFromStart / 20) % 18;
                sectors[sector]++;
            }
        }

        // Finalize overlapByPosition (round)
        foreach (var key in overlapByPosition.Keys.ToList())
            overlapByPosition[key] = Math.Round(overlapByPosition[key], 0);

        // Finalize fixStrategyRates
        var fixStrategyRates = new Dictionary<string, double>();
        foreach (var key in fixAttempts.Keys)
            fixStrategyRates[key] = fixAttempts[key] > 0 ? Math.Round((double)fixSuccesses.GetValueOrDefault(key) / fixAttempts[key], 3) : 0;

        // Finalize waypointUniformity
        int sectorTotal = sectors.Sum();
        double waypointUniformity = 0;
        if (sectorTotal > 0)
        {
            double expected = (double)sectorTotal / 18;
            double chiSquared = 0;
            foreach (var count in sectors)
                chiSquared += Math.Pow(count - expected, 2) / expected;
            double maxChiSquared = 17.0 * expected;
            waypointUniformity = Math.Round(1.0 - (chiSquared / maxChiSquared), 3);
        }

        return (stemsByRootCause, Math.Round(returnOverlapM, 0), returnSegmentLength,
            overlapByPosition, stemsAcceptedHighOverlap, fixStrategyDist, fixStrategyRates, waypointUniformity);
    }

    // Session 9: Helper methods for DebugFinalSummary
    private List<int> ComputeAttemptStemCounts()
    {
        var counts = new List<int>();
        foreach (var summary in _diagnostics.GetAllRouteSummaries())
        {
            if (summary is DebugRouteSummary rs)
            {
                counts.Add(rs.StemsDetected);
            }
        }
        return counts;
    }

    private List<int> ComputeAttemptNullDropCounts()
    {
        var counts = new List<int>();
        foreach (var summary in _diagnostics.GetAllRouteSummaries())
        {
            if (summary is DebugRouteSummary rs)
            {
                counts.Add(rs.NullSegmentDrops);
            }
        }
        return counts;
    }

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
                var stats = _routeStatistics.CalculateStats(geometry, profile);

                ProgressChanged?.Invoke(1.0);
                StatusChanged?.Invoke("Route calculated");

                return new RouteResponse
                {
                    RouteGeometry = geometry,
                    Stats = stats,
                    Waypoints = allPoints,
                    RepetitionSegments = RouteGeometryUtils.ExtractRepetitionSegments(geometry, _routeStatistics.ReturnToStartIndex),
                };
            }
        }

        throw new InvalidOperationException(
            "Could not calculate a route between the specified points. " +
            "Make sure the start point and waypoints are on roads within the loaded map area.");
    }

    private static double DirectionToBearing(DirectionBias direction) => direction switch
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

    private static string BearingToDirection(double bearing)
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
}
