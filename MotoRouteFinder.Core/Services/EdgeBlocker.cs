using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Itinero;
using Itinero.Data.Network.Edges;
using Itinero.Profiles;
using MotoRouteFinder.Helpers;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Services;

/// <summary>
/// Manages edge blocking/restoration for route avoidance and motorway detection.
/// </summary>
public class EdgeBlocker
{
    private readonly MapRepository _mapRepository;
    private int _blockEdgesCount;
    private long _findMotorwayMs;
    private int _motorwayEdgesFound;
    private int _gridPointsSampled;

    public int BlockEdgesCount => _blockEdgesCount;
    public long FindMotorwayMs => _findMotorwayMs;
    public int MotorwayEdgesFound => _motorwayEdgesFound;
    public int GridPointsSampled => _gridPointsSampled;

    public EdgeBlocker(MapRepository mapRepository)
    {
        _mapRepository = mapRepository;
    }

    public void ResetCounters()
    {
        _blockEdgesCount = 0;
        _findMotorwayMs = 0;
        _motorwayEdgesFound = 0;
        _gridPointsSampled = 0;
    }

    public BlockedEdgesScope? BlockMotorways(IProfileInstance profile, Coordinate center, double radiusKm, Action<string>? statusCallback = null)
    {
        if (_mapRepository.Router == null || _mapRepository.RouterDb == null) return null;

        double latDeg = radiusKm / GeoConstants.KmPerDegreeLat;
        double lonDeg = radiusKm / (GeoConstants.KmPerDegreeLat * Math.Max(Math.Cos(center.Lat * Math.PI / 180), GeoConstants.MinCosLat));

        double latMin = center.Lat - latDeg;
        double latMax = center.Lat + latDeg;
        double lonMin = center.Lon - lonDeg;
        double lonMax = center.Lon + lonDeg;

        var fmSw = System.Diagnostics.Stopwatch.StartNew();
        var motorwayEdges = FindMotorwayEdges(profile, latMin, latMax, lonMin, lonMax);
        fmSw.Stop();
        _findMotorwayMs += fmSw.ElapsedMilliseconds;
        if (motorwayEdges.Count == 0) return null;

        statusCallback?.Invoke($"Blocking {motorwayEdges.Count} motorway edges in surrounding area...");
        return new BlockedEdgesScope(this, motorwayEdges);
    }

    public List<uint> FindMotorwayEdges(
        IProfileInstance profile,
        double latMin, double latMax, double lonMin, double lonMax)
    {
        if (_mapRepository.Router == null || _mapRepository.RouterDb == null) return new List<uint>();

        try
        {
            long edgeCount = _mapRepository.RouterDb.Network.EdgeCount;
            _gridPointsSampled = (int)edgeCount;

            System.Diagnostics.Debug.WriteLine($"[MOTORWAY BLOCK] Scanning {edgeCount} edges for motorways in bbox [{latMin:F2}-{latMax:F2}, {lonMin:F2}-{lonMax:F2}]...");

            var motorwayEdges = new HashSet<uint>();

            foreach (var edge in _mapRepository.RouterDb.Network.GetEdgeEnumerator())
            {
                try
                {
                    var tags = _mapRepository.RouterDb.EdgeProfiles.Get(edge.Data.Profile);
                    if (tags == null) continue;

                    if (!tags.TryGetValue("highway", out string highway)) continue;
                    highway = highway.ToLowerInvariant();
                    if (highway != "motorway" && highway != "motorway_link") continue;

                    if (edge.Shape != null)
                    {
                        bool inBbox = false;
                        foreach (var vertex in edge.Shape)
                        {
                            if (vertex.Latitude >= latMin && vertex.Latitude <= latMax &&
                                vertex.Longitude >= lonMin && vertex.Longitude <= lonMax)
                            {
                                inBbox = true;
                                break;
                            }
                        }
                        if (!inBbox) continue;
                    }

                    motorwayEdges.Add(edge.Id);
                }
                catch { }
            }

            _motorwayEdgesFound = motorwayEdges.Count;
            System.Diagnostics.Debug.WriteLine($"[MOTORWAY BLOCK] Found {motorwayEdges.Count} motorway edges via edge enumeration");
            return motorwayEdges.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MOTORWAY BLOCK] Failed to enumerate edges: {ex.Message}");
            return new List<uint>();
        }
    }

    public List<(uint EdgeId, EdgeData Original)> BlockEdges(List<uint> edgeIds)
    {
        var blocked = new List<(uint, EdgeData)>();
        _blockEdgesCount += edgeIds.Count;

        foreach (uint edgeId in edgeIds)
        {
            try
            {
                var edge = _mapRepository.RouterDb!.Network.GetEdge(edgeId);
                var original = edge.Data;
                _mapRepository.RouterDb.Network.UpdateEdgeData(edgeId, new EdgeData
                {
                    Distance = original.Distance,
                    Profile = 0,
                    MetaId = original.MetaId
                });
                blocked.Add((edgeId, original));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to block edge {edgeId}: {ex.Message}");
            }
        }

        return blocked;
    }

    /// <summary>
    /// Scans a route segment for motorway edges and blocks them.
    /// Samples points along the segment, resolves each to the nearest edge,
    /// and blocks any that are motorway/motorway_link.
    /// </summary>
    public List<uint> BlockMotorwaysInSegment(List<Coordinate> segment, IProfileInstance profile, HashSet<uint> alreadyBlocked)
    {
        if (_mapRepository.Router == null || _mapRepository.RouterDb == null || segment.Count < 2)
            return new List<uint>();

        var newMotorwayEdges = new List<uint>();
        var sampledPoints = RouteGeometryUtils.SampleAlongGeometry(segment, 50);

        foreach (var point in sampledPoints)
        {
            var result = _mapRepository.Router.TryResolve(profile, (float)point.Lat, (float)point.Lon, 500);
            if (result.IsError) continue;

            uint edgeId = result.Value.EdgeId;
            if (alreadyBlocked.Contains(edgeId)) continue;

            try
            {
                var edge = _mapRepository.RouterDb.Network.GetEdge(edgeId);
                var tags = _mapRepository.RouterDb.EdgeProfiles.Get(edge.Data.Profile);
                if (tags == null) continue;

                if (tags.TryGetValue("highway", out string highway))
                {
                    highway = highway.ToLowerInvariant();
                    if (highway == "motorway" || highway == "motorway_link")
                    {
                        newMotorwayEdges.Add(edgeId);
                        alreadyBlocked.Add(edgeId);
                    }
                }
            }
            catch { }
        }

        if (newMotorwayEdges.Count > 0)
        {
            BlockEdges(newMotorwayEdges);
            _motorwayEdgesFound += newMotorwayEdges.Count;
            System.Diagnostics.Debug.WriteLine($"[MOTORWAY BLOCK] Blocked {newMotorwayEdges.Count} motorway edges in segment ({sampledPoints.Count} points sampled)");
        }

        return newMotorwayEdges;
    }

    public void RestoreEdges(List<(uint EdgeId, EdgeData Original)> blocked)
    {
        foreach (var (edgeId, original) in blocked)
        {
            try
            {
                _mapRepository.RouterDb!.Network.UpdateEdgeData(edgeId, original);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to restore edge {edgeId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Applies a penalty factor to edge distances (GraphHopper-inspired).
    /// Multiplies edge distances by the penalty factor, making routes through these edges more expensive.
    /// Returns a scope that automatically restores original distances when disposed.
    /// </summary>
    public PenaltyEdgesScope? PenaltyEdges(List<uint> edgeIds, double penaltyFactor)
    {
        if (_mapRepository.RouterDb == null || edgeIds.Count == 0) return null;

        var originalData = new List<(uint EdgeId, EdgeData Original)>();

        foreach (uint edgeId in edgeIds)
        {
            try
            {
                var edge = _mapRepository.RouterDb.Network.GetEdge(edgeId);
                var original = edge.Data;

                // Apply penalty by multiplying distance (clamped to ushort max to avoid overflow)
                uint penalizedDistance = (uint)(original.Distance * penaltyFactor);
                if (penalizedDistance > ushort.MaxValue)
                    penalizedDistance = ushort.MaxValue;

                var penalizedData = new EdgeData
                {
                    Distance = penalizedDistance,
                    Profile = original.Profile,
                    MetaId = original.MetaId
                };

                _mapRepository.RouterDb.Network.UpdateEdgeData(edgeId, penalizedData);
                originalData.Add((edgeId, original));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to penalize edge {edgeId}: {ex.Message}");
            }
        }

        return new PenaltyEdgesScope(this, originalData);
    }

    public void RestorePenalizedEdges(List<(uint EdgeId, EdgeData Original)> penalized)
    {
        foreach (var (edgeId, original) in penalized)
        {
            try
            {
                _mapRepository.RouterDb!.Network.UpdateEdgeData(edgeId, original);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to restore penalized edge {edgeId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Finds all motorway edges in the RouterDb.
    /// Scans all edges and collects those with highway=motorway or motorway_link.
    /// Returns all motorway edge IDs.
    /// </summary>
    /// <summary>
    /// Finds all motorway edges using parallel grid sampling with TryResolve.
    /// Samples points across the map area, resolves each to the nearest edge,
    /// and checks if that edge is a motorway. Uses TryResolve to force Itinero initialization.
    /// </summary>
    public List<uint> FindMotorwaysParallel(IProfileInstance? profile = null, Action<string>? statusCallback = null, IProgress<double>? progress = null)
    {
        if (_mapRepository.RouterDb == null || _mapRepository.Router == null) return new List<uint>();
        if (profile == null) return new List<uint>();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Compute bounding box
        double centerLat = 51.5, centerLon = 14.0;
        double searchRadius = 500; // km

        double latDeg = searchRadius / GeoConstants.KmPerDegreeLat;
        double cosLat = Math.Max(Math.Cos(centerLat * Math.PI / 180), GeoConstants.MinCosLat);
        double lonDeg = searchRadius / (GeoConstants.KmPerDegreeLat * cosLat);

        double latMin = centerLat - latDeg;
        double latMax = centerLat + latDeg;
        double lonMin = centerLon - lonDeg;
        double lonMax = centerLon + lonDeg;

        // Grid sampling: ~500m steps (0.005 degrees ≈ 500m)
        double step = 0.005;
        int latSteps = (int)((latMax - latMin) / step) + 1;
        int lonSteps = (int)((lonMax - lonMin) / step) + 1;
        int totalPoints = latSteps * lonSteps;

        System.Diagnostics.Debug.WriteLine($"[MOTORWAY] Grid sampling: {latSteps}x{lonSteps} = {totalPoints} points in bbox [{latMin:F2}-{latMax:F2}, {lonMin:F2}-{lonMax:F2}]");
        statusCallback?.Invoke($"Scanning {totalPoints:N0} grid points for motorways...");

        // Build grid points list
        var gridPoints = new List<(double lat, double lon)>(totalPoints);
        for (double lat = latMin; lat <= latMax; lat += step)
            for (double lon = lonMin; lon <= lonMax; lon += step)
                gridPoints.Add((lat, lon));

        // Thread-safe collections
        var motorwayEdges = new ConcurrentBag<uint>();
        var visitedEdges = new ConcurrentDictionary<uint, byte>();
        int processed = 0;
        int resolved = 0;

        // Process grid points in parallel
        int maxThreads = Environment.ProcessorCount;
        System.Diagnostics.Debug.WriteLine($"[MOTORWAY] Processing {totalPoints} points with {maxThreads} threads...");

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = maxThreads };

        Parallel.ForEach(gridPoints, parallelOptions, point =>
        {
            int localProcessed = Interlocked.Increment(ref processed);
            if (localProcessed % 50000 == 0)
                progress?.Report((double)localProcessed / totalPoints);

            try
            {
                var result = _mapRepository.Router.TryResolve(profile, (float)point.lat, (float)point.lon, 500);
                if (result.IsError) return;

                Interlocked.Increment(ref resolved);
                uint edgeId = result.Value.EdgeId;

                // Skip already processed edges
                if (!visitedEdges.TryAdd(edgeId, 0)) return;

                var edge = _mapRepository.RouterDb.Network.GetEdge(edgeId);
                var tags = _mapRepository.RouterDb.EdgeProfiles.Get(edge.Data.Profile);
                if (tags == null) return;

                if (tags.TryGetValue("highway", out string highway))
                {
                    highway = highway.ToLowerInvariant();
                    if (highway == "motorway" || highway == "motorway_link")
                    {
                        motorwayEdges.Add(edgeId);
                    }
                }
            }
            catch { }
        });

        progress?.Report(1.0);
        sw.Stop();
        _findMotorwayMs += sw.ElapsedMilliseconds;
        _motorwayEdgesFound = motorwayEdges.Count;
        _gridPointsSampled = totalPoints;

        var resultList = motorwayEdges.Distinct().ToList();
        System.Diagnostics.Debug.WriteLine($"[MOTORWAY] Grid scan complete: {totalPoints} points sampled, {resolved} resolved, {resultList.Count} motorway edges found in {sw.ElapsedMilliseconds}ms");
        statusCallback?.Invoke($"Found {resultList.Count:N0} motorway edges in {sw.ElapsedMilliseconds}ms");

        return resultList;
    }

    /// <summary>
    /// Permanently blocks motorway edges in the RouterDb (for load-time use).
    /// Sets Profile=0 on all motorway edges. This modification persists in the cache.
    /// Returns the number of edges successfully blocked.
    /// </summary>
    public int BlockMotorwaysPermanently(Action<string>? statusCallback = null, IProgress<double>? progress = null)
    {
        if (_mapRepository.RouterDb == null || _mapRepository.Router == null) return 0;

        // Get motorcycle profile for initialization
        var profile = _mapRepository.RouterDb.GetSupportedProfile("motorcycle");

        var motorwayEdges = FindMotorwaysParallel(profile, statusCallback, progress);
        if (motorwayEdges.Count == 0) return 0;

        statusCallback?.Invoke($"Permanently blocking {motorwayEdges.Count:N0} motorway edges...");

        // Block edges (single-threaded — UpdateEdgeData is not thread-safe)
        int blocked = 0;
        int failed = 0;
        foreach (uint edgeId in motorwayEdges)
        {
            try
            {
                var edge = _mapRepository.RouterDb.Network.GetEdge(edgeId);
                var original = edge.Data;
                _mapRepository.RouterDb.Network.UpdateEdgeData(edgeId, new EdgeData
                {
                    Distance = original.Distance,
                    Profile = 0,
                    MetaId = original.MetaId
                });
                blocked++;
            }
            catch (Exception ex)
            {
                failed++;
                System.Diagnostics.Debug.WriteLine($"[MOTORWAY BLOCK] FAILED to block edge {edgeId}: {ex.Message}");
            }
        }

        // Validate blocking
        bool validationPassed = blocked == motorwayEdges.Count;
        if (!validationPassed)
        {
            System.Diagnostics.Debug.WriteLine($"[MOTORWAY BLOCK] WARNING: Only blocked {blocked}/{motorwayEdges.Count} edges ({failed} failed)");
            statusCallback?.Invoke($"WARNING: Only blocked {blocked}/{motorwayEdges.Count} motorway edges ({failed} failed)");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[MOTORWAY BLOCK] Successfully blocked all {blocked} motorway edges");
        }

        return blocked;
    }

    public sealed class PenaltyEdgesScope : IDisposable
    {
        private readonly EdgeBlocker _blocker;
        private readonly List<(uint EdgeId, EdgeData Original)> _penalized;
        private bool _disposed;

        public PenaltyEdgesScope(EdgeBlocker blocker, List<(uint EdgeId, EdgeData Original)> penalized)
        {
            _blocker = blocker;
            _penalized = penalized;
        }

        public bool HasEdges => _penalized.Count > 0;

        public void Dispose()
        {
            if (!_disposed)
            {
                _blocker.RestorePenalizedEdges(_penalized);
                _disposed = true;
            }
        }
    }

    public sealed class BlockedEdgesScope : IDisposable
    {
        private readonly EdgeBlocker _blocker;
        private readonly List<(uint EdgeId, EdgeData Original)> _blocked;
        private bool _disposed;

        public BlockedEdgesScope(EdgeBlocker blocker, List<uint> edgeIds)
        {
            _blocker = blocker;
            _blocked = blocker.BlockEdges(edgeIds);
        }

        public List<(uint EdgeId, EdgeData Original)> BlockedEdges => _blocked;
        public bool HasEdges => _blocked.Count > 0;

        public void Dispose()
        {
            if (!_disposed)
            {
                _blocker.RestoreEdges(_blocked);
                _disposed = true;
            }
        }
    }
}
