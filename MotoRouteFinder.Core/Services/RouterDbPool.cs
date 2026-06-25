using System;
using System.Collections.Concurrent;
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
    private readonly string _cachePath;
    private readonly int _size;
    private int _checkedOut;
    private bool _disposed;

    public int Size => _size;
    public int AvailableCount => _size - _checkedOut;
    public int CheckedOutCount => _checkedOut;

    public RouterDbPool(string cachePath, int size)
    {
        _cachePath = cachePath;
        _size = size;
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
        statusCallback?.Invoke($"Ready: {_size} routing instances loaded in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Checks out a MapRepository from the pool. Blocks if none available.
    /// </summary>
    public MapRepository Checkout()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RouterDbPool));

        MapRepository? repo;
        while (!_pool.TryDequeue(out repo))
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RouterDbPool));
            Thread.SpinWait(100);
        }

        Interlocked.Increment(ref _checkedOut);
        return repo;
    }

    /// <summary>
    /// Returns a MapRepository to the pool for reuse.
    /// </summary>
    public void Checkin(MapRepository repo)
    {
        if (repo == null) return;
        Interlocked.Decrement(ref _checkedOut);
        if (!_disposed)
            _pool.Enqueue(repo);
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
        while (_pool.TryDequeue(out var repo))
        {
            repo?.ClearMaps();
        }
    }
}
