using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MotoRouteFinder.Services;

/// <summary>
/// Pre-loads multiple MapRepository instances from cache for parallel route generation.
/// Each instance has its own RouterDb (~500MB), RoadClassifier, EdgeBlocker, etc.
/// Thread-safe checkout/checkin via ConcurrentQueue.
/// </summary>
public class RouterDbPool : IDisposable
{
    private readonly ConcurrentQueue<MapRepository> _pool = new();
    private readonly SemaphoreSlim _semaphore;
    private readonly string _cachePath;
    private readonly int _size;
    private int _checkedOut;
    private bool _disposed;

    // Cache observability — captured at warm-up
    public string CachePath => _cachePath;
    public long CacheFileSizeBytes { get; private set; }
    public long WarmUpMs { get; private set; }
    public bool MotorwayBlockingSuspect { get; private set; }
    public int RoutableMotorwayEdgesOnLoad { get; private set; }

    public int Size => _size;
    public int AvailableCount => _size - _checkedOut;
    public int CheckedOutCount => _checkedOut;

    public RouterDbPool(string cachePath, int size)
    {
        _cachePath = cachePath;
        _size = size;
        _semaphore = new SemaphoreSlim(size, size);
    }

    /// <summary>
    /// Pre-loads all RouterDb instances in parallel. Call once at startup.
    /// </summary>
    public async Task WarmUpAsync(Action<string>? statusCallback = null)
    {
        statusCallback?.Invoke($"Loading {_size} routing instances from cache...");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int loaded = 0;

        await Task.Run(() =>
        {
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = _size,
            };

            Parallel.For(0, _size, parallelOptions, i =>
            {
                var repo = new MapRepository();
                repo.LoadCache(_cachePath);
                _pool.Enqueue(repo);
                int count = Interlocked.Increment(ref loaded);
                statusCallback?.Invoke($"Loaded routing instance {count}/{_size}");
            });
        });

        sw.Stop();
        WarmUpMs = sw.ElapsedMilliseconds;

        // Capture cache file size from disk
        try
        {
            if (File.Exists(_cachePath))
                CacheFileSizeBytes = new FileInfo(_cachePath).Length;
        }
        catch { /* non-critical */ }

        // Capture integrity flags from first pool instance
        var firstRepo = _pool.ToArray().FirstOrDefault();
        if (firstRepo != null)
        {
            MotorwayBlockingSuspect = firstRepo.MotorwayBlockingSuspect;
            RoutableMotorwayEdgesOnLoad = firstRepo.RoutableMotorwayEdgesOnLoad;
        }

        statusCallback?.Invoke($"Ready: {_size} routing instances loaded in {WarmUpMs}ms (cache: {CacheFileSizeBytes / 1048576.0:F1} MB)");
    }

    /// <summary>
    /// Checks out a MapRepository from the pool. Blocks if none available.
    /// </summary>
    public MapRepository Checkout()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RouterDbPool));

        _semaphore.Wait();

        if (!_pool.TryDequeue(out var repo))
            throw new InvalidOperationException("Pool semaphore released but no instance available");

        Interlocked.Increment(ref _checkedOut);
        return repo;
    }

    /// <summary>
    /// Returns a MapRepository to the pool for reuse.
    /// </summary>
    public void Checkin(MapRepository repo)
    {
        if (repo == null) return;
        if (_disposed)
        {
            Interlocked.Decrement(ref _checkedOut);
            repo.ClearMaps();
        }
        else
        {
            _pool.Enqueue(repo);
            _semaphore.Release();
            Interlocked.Decrement(ref _checkedOut);
        }
    }

    /// <summary>
    /// Checks out multiple instances at once.
    /// </summary>
    public MapRepository[] CheckoutMultiple(int count)
    {
        var repos = new MapRepository[count];
        for (int i = 0; i < count; i++)
            repos[i] = Checkout();
        return repos;
    }

    /// <summary>
    /// Returns multiple instances to the pool.
    /// </summary>
    public void CheckinMultiple(MapRepository[] repos)
    {
        foreach (var repo in repos)
            Checkin(repo);
    }

    public void Dispose()
    {
        _disposed = true;
        int count = 0;
        while (_pool.TryDequeue(out var repo))
        {
            repo?.ClearMaps();
            count++;
        }
        _semaphore.Dispose();
        System.Diagnostics.Debug.WriteLine($"[MEM] RouterDbPool.Dispose: cleared {count} MapRepository instances (pool size was {_size}, checked out was {_checkedOut})");
    }
}
