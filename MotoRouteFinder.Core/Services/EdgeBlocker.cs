using System;
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
    private int _activeBlockCount;

    // §11e: PenaltyEdges/RestoreEdges timing
    private long _penaltyEdgesMs;
    private int _penaltyEdgesEdgeCount;
    private long _restoreEdgesMs;
    private int _restoreEdgesEdgeCount;

    public int BlockEdgesCount => _blockEdgesCount;
    public long FindMotorwayMs => _findMotorwayMs;
    public int MotorwayEdgesFound => _motorwayEdgesFound;
    public int GridPointsSampled => _gridPointsSampled;
    public bool HasActiveBlocks => _activeBlockCount > 0;
    public long PenaltyEdgesMs => _penaltyEdgesMs;
    public int PenaltyEdgesEdgeCount => _penaltyEdgesEdgeCount;
    public long RestoreEdgesMs => _restoreEdgesMs;
    public int RestoreEdgesEdgeCount => _restoreEdgesEdgeCount;

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
        _penaltyEdgesMs = 0;
        _penaltyEdgesEdgeCount = 0;
        _restoreEdgesMs = 0;
        _restoreEdgesEdgeCount = 0;
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

        // Motorways already stripped at load time — nothing to find
        if (_mapRepository.MotorwaysInCache)
        {
            _motorwayEdgesFound = 0;
            _gridPointsSampled = 0;
            return new List<uint>();
        }

        string cacheKey = _mapRepository.CachePath ?? "";
        if (cacheKey.Length > 0 && MapRepository.MotorwayEdgeCache.TryGetValue(cacheKey, out var cached))
        {
            _motorwayEdgesFound = cached.Count;
            _gridPointsSampled = 0;
            return cached;
        }

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

                    if (!tags.TryGetValue("highway", out string? highway)) continue;
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
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MOTORWAY BLOCK] Edge enumeration error: {ex.Message}");
                }
            }

            _motorwayEdgesFound = motorwayEdges.Count;
            var result = motorwayEdges.ToList();
            if (cacheKey.Length > 0)
                MapRepository.MotorwayEdgeCache[cacheKey] = result;
            System.Diagnostics.Debug.WriteLine($"[MOTORWAY BLOCK] Found {motorwayEdges.Count} motorway edges via edge enumeration");
            return result;
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

                if (tags.TryGetValue("highway", out string? highway))
                {
                    highway = highway.ToLowerInvariant();
                    if (highway == "motorway" || highway == "motorway_link")
                    {
                        newMotorwayEdges.Add(edgeId);
                        alreadyBlocked.Add(edgeId);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MOTORWAY BLOCK] Segment scan error: {ex.Message}");
            }
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

        var sw = System.Diagnostics.Stopwatch.StartNew();
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

        sw.Stop();
        _penaltyEdgesMs += sw.ElapsedMilliseconds;
        _penaltyEdgesEdgeCount += edgeIds.Count;

        return new PenaltyEdgesScope(this, originalData);
    }

    public void RestorePenalizedEdges(List<(uint EdgeId, EdgeData Original)> penalized)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
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
        sw.Stop();
        _restoreEdgesMs += sw.ElapsedMilliseconds;
        _restoreEdgesEdgeCount += penalized.Count;
    }

    /// <summary>
    /// Finds ALL motorway/motorway_link edges by walking every vertex in the network.
    /// Replaces the grid-scan approach which missed ~92% of motorway edges.
    /// O(2·EdgeCount) — single-threaded, no Router/profile needed.
    /// </summary>
    public List<uint> FindAllMotorwayEdges(Action<string>? statusCallback = null)
    {
        var db = _mapRepository.RouterDb;
        if (db == null) return new List<uint>();

        var network = db.Network;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        uint vertexCount = network.VertexCount;
        var motorways = new HashSet<uint>();

        var en = network.GetEdgeEnumerator();
        for (uint v = 0; v < vertexCount; v++)
        {
            if (!en.MoveTo(v)) continue;
            while (en.MoveNext())
            {
                uint edgeId = en.Id;
                if (!motorways.Add(edgeId)) continue;

                var tags = db.EdgeProfiles.Get(en.Data.Profile);
                if (tags != null && tags.TryGetValue("highway", out string? hw))
                {
                    hw = hw.ToLowerInvariant();
                    if (hw == "motorway" || hw == "motorway_link") continue;
                }
                motorways.Remove(edgeId);
            }
        }

        sw.Stop();
        _findMotorwayMs += sw.ElapsedMilliseconds;
        _motorwayEdgesFound = motorways.Count;
        _gridPointsSampled = 0;

        var result = motorways.ToList();
        Console.WriteLine($"[MOTORWAY] Edge walk: {vertexCount:N0} vertices, {result.Count:N0} motorway edges found in {sw.ElapsedMilliseconds}ms");
        statusCallback?.Invoke($"Found {result.Count:N0} motorway edges via complete edge walk in {sw.ElapsedMilliseconds}ms");
        return result;
    }

    /// <summary>
    /// Counts motorway/motorway_link edges that still have a non-zero Profile (i.e., are routable).
    /// Used by LoadCache integrity check — in a properly blocked cache this should be 0.
    /// </summary>
    public int CountRoutableMotorways()
    {
        var db = _mapRepository.RouterDb;
        if (db == null) return 0;

        var network = db.Network;
        uint vertexCount = network.VertexCount;
        int count = 0;

        var en = network.GetEdgeEnumerator();
        for (uint v = 0; v < vertexCount; v++)
        {
            if (!en.MoveTo(v)) continue;
            while (en.MoveNext())
            {
                if (en.Data.Profile == 0) continue; // already blocked

                var tags = db.EdgeProfiles.Get(en.Data.Profile);
                if (tags != null && tags.TryGetValue("highway", out string? hw))
                {
                    hw = hw.ToLowerInvariant();
                    if (hw == "motorway" || hw == "motorway_link")
                    {
                        count++;
                    }
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Step 3 probe: verify the edge walk's IDs match GetEdge(id) by checking tags directly.
    /// For each sample motorway edge found via en.Id, calls GetEdge(en.Id) and verifies
    /// the returned edge's profile tags still say motorway/motorway_link.
    /// </summary>
    public Dictionary<string, object> ProbeEdgeWalkSafety(Action<string>? statusCallback = null)
    {
        var result = new Dictionary<string, object>();
        var db = _mapRepository.RouterDb;
        if (db == null)
        {
            result["error"] = "RouterDb not loaded";
            return result;
        }

        var network = db.Network;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Walk all vertices to find motorway edges via en.Id
        uint vertexCount = network.VertexCount;
        var motorwayEdges = new List<(uint edgeId, uint fromVertex, uint toVertex)>();
        var allEdgesWalked = 0;

        var en = network.GetEdgeEnumerator();
        for (uint v = 0; v < vertexCount; v++)
        {
            if (!en.MoveTo(v)) continue;
            while (en.MoveNext())
            {
                allEdgesWalked++;
                uint edgeId = en.Id;

                var tags = db.EdgeProfiles.Get(en.Data.Profile);
                if (tags != null && tags.TryGetValue("highway", out string? hw))
                {
                    hw = hw.ToLowerInvariant();
                    if (hw == "motorway" || hw == "motorway_link")
                    {
                        bool alreadyFound = false;
                        foreach (var existing in motorwayEdges)
                        {
                            if (existing.edgeId == edgeId) { alreadyFound = true; break; }
                        }
                        if (!alreadyFound)
                        {
                            motorwayEdges.Add((edgeId, en.From, en.To));
                        }
                    }
                }
            }
        }

        sw.Stop();
        result["vertexCount"] = vertexCount;
        result["allEdgesWalked"] = allEdgesWalked;
        result["motorwayEdgesFound"] = motorwayEdges.Count;
        result["walkMs"] = sw.ElapsedMilliseconds;

        var msg = $"[PROBE] Walk: {vertexCount} vertices, {allEdgesWalked} edges, {motorwayEdges.Count} motorways in {sw.ElapsedMilliseconds}ms";
        Console.WriteLine(msg);
        statusCallback?.Invoke(msg);

        if (motorwayEdges.Count == 0)
        {
            result["error"] = "No motorway edges found — enumeration may be broken";
            return result;
        }

        // 2. Id-mapping proof: for each sample, verify GetEdge(en.Id) returns the same motorway edge
        int sampleCount = Math.Min(10, motorwayEdges.Count);
        var samples = motorwayEdges.Take(sampleCount).ToList();
        var idProofResults = new List<Dictionary<string, object>>();

        foreach (var sample in samples)
        {
            var proof = new Dictionary<string, object>();
            proof["edgeId"] = sample.edgeId;

            try
            {
                // Direct tag assertion: GetEdge(en.Id) → EdgeProfiles.Get → verify motorway
                var edge = network.GetEdge(sample.edgeId);
                var edgeTags = db.EdgeProfiles.Get(edge.Data.Profile);

                if (edgeTags == null)
                {
                    proof["tagMatch"] = false;
                    proof["reason"] = "EdgeProfiles.Get returned null";
                }
                else if (edgeTags.TryGetValue("highway", out string? edgeHw))
                {
                    edgeHw = edgeHw.ToLowerInvariant();
                    proof["getEdgeHighway"] = edgeHw;
                    proof["tagMatch"] = edgeHw == "motorway" || edgeHw == "motorway_link";
                }
                else
                {
                    proof["tagMatch"] = false;
                    proof["reason"] = "No highway tag on edge";
                }

                // Also verify profile != 0 (not already blocked)
                proof["profileId"] = edge.Data.Profile;
                proof["profileNotZero"] = edge.Data.Profile != 0;
            }
            catch (Exception ex)
            {
                proof["tagMatch"] = false;
                proof["getError"] = ex.Message;
            }

            idProofResults.Add(proof);
        }

        result["idProof"] = idProofResults;

        bool allPassed = idProofResults.All(p => p.ContainsKey("tagMatch") && (bool)p["tagMatch"]);
        result["allPassed"] = allPassed;

        var summaryMsg = $"[PROBE] Id-mapping proof: {sampleCount} samples tested, allPassed={allPassed}";
        Console.WriteLine(summaryMsg);
        statusCallback?.Invoke(summaryMsg);

        return result;
    }

    /// <summary>
    /// Permanently blocks motorway edges in the RouterDb (for load-time use).
    /// Sets Profile=0 on all motorway edges. This modification persists in the cache.
    /// Returns the number of edges successfully blocked.
    /// </summary>
    public int BlockMotorwaysPermanently(Action<string>? statusCallback = null, IProgress<double>? progress = null)
    {
        if (_mapRepository.RouterDb == null || _mapRepository.Router == null) return 0;

        var motorwayEdges = FindAllMotorwayEdges(statusCallback);
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
        private bool _counted;

        public BlockedEdgesScope(EdgeBlocker blocker, List<uint> edgeIds)
        {
            _blocker = blocker;
            _blocked = blocker.BlockEdges(edgeIds);
            if (_blocked.Count > 0)
            {
                Interlocked.Increment(ref _blocker._activeBlockCount);
                _counted = true;
            }
        }

        public List<(uint EdgeId, EdgeData Original)> BlockedEdges => _blocked;
        public bool HasEdges => _blocked.Count > 0;

        public void Dispose()
        {
            if (!_disposed)
            {
                _blocker.RestoreEdges(_blocked);
                if (_counted)
                    Interlocked.Decrement(ref _blocker._activeBlockCount);
                _disposed = true;
            }
        }
    }
}
