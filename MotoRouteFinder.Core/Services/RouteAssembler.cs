using System;
using System.Collections.Generic;
using System.Linq;
using Itinero;
using Itinero.Profiles;
using MotoRouteFinder.Helpers;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Services;

/// <summary>
/// Handles route calculation and cumulative edge avoidance.
/// </summary>
public class RouteAssembler
{
    private readonly MapRepository _mapRepository;
    private readonly RoadClassifier _roadClassifier;
    private int _routingCount;
    private long _routingCallsMs;
    // ⚠ CACHE SAFETY: This cache is keyed on coordinates only — it has no concept of edge-penalty state.
    // Do NOT have other callers share this cache while edge penalties are active (e.g. AlternativePathFinder's
    // penalty escalation ladder, which routes the same coordinate pair under different penalty levels).
    // A shared cache would silently return a pre-penalty result. See AlternativePathFinder.RouteSingleSegment
    // for the dedicated, deliberately-uncached method used for exactly this reason.
    private readonly Dictionary<(double, double, double, double), List<Coordinate>> _routingCache = new();
    private readonly Queue<(double, double, double, double)> _routingCacheOrder = new();
    private int _cacheHits;

    // Per-route counters (reset at start of each route generation)
    private long _perRouteRoutingCallsMs;
    private int _perRouteRoutingCount;
    private int _perRouteWastedCalls;

    private const double OverlapThreshold = 0.15;
    private const double OverlapRejectThreshold = 0.25;
    private const int PushAttempts = 8;
    private const int MaxRoutingCacheSize = 100;

    public int RoutingCount => _routingCount;
    public long RoutingCallsMs => _routingCallsMs;
    public int CacheHits => _cacheHits;
    public int CacheSize => _routingCache.Count;

    // Per-route accessors
    public long PerRouteRoutingCallsMs => _perRouteRoutingCallsMs;
    public int PerRouteRoutingCount => _perRouteRoutingCount;
    public int PerRouteWastedCalls => _perRouteWastedCalls;

    public RouteAssembler(MapRepository mapRepository, RoadClassifier roadClassifier)
    {
        _mapRepository = mapRepository;
        _roadClassifier = roadClassifier;
    }

    public void ResetCounters()
    {
        _routingCount = 0;
        _routingCallsMs = 0;
        _cacheHits = 0;
        _perRouteRoutingCallsMs = 0;
        _perRouteRoutingCount = 0;
        _perRouteWastedCalls = 0;
        _routingCache.Clear();
        _routingCacheOrder.Clear();
    }

    // See _routingCache field comment for cache-safety constraints.
    // AlternativePathFinder has its own uncached RouteSingleSegment — do not redirect callers here.
    public List<Coordinate>? RouteSingleSegment(IProfileInstance profile, Coordinate from, Coordinate to)
    {
        var key = (Math.Round(from.Lat, 4), Math.Round(from.Lon, 4),
                   Math.Round(to.Lat, 4), Math.Round(to.Lon, 4));

        if (_routingCache.TryGetValue(key, out var cached))
        {
            _cacheHits++;
            return cached;
        }

        var fromCoord = new Itinero.LocalGeo.Coordinate((float)from.Lat, (float)from.Lon);
        var toCoord = new Itinero.LocalGeo.Coordinate((float)to.Lat, (float)to.Lon);

        try
        {
            _routingCount++;
            _perRouteRoutingCount++;
            var rsSw = System.Diagnostics.Stopwatch.StartNew();
            var segment = _mapRepository.Router!.Calculate(profile, fromCoord, toCoord);
            rsSw.Stop();
            _routingCallsMs += rsSw.ElapsedMilliseconds;
            _perRouteRoutingCallsMs += rsSw.ElapsedMilliseconds;
            var result = RouteGeometryUtils.ExtractCoordinates(segment);
            if (result == null || result.Count == 0)
                _perRouteWastedCalls++;
            if (result != null && result.Count > 0)
            {
                // Evict oldest half when cache is full
                if (_routingCache.Count >= MaxRoutingCacheSize)
                {
                    int evict = MaxRoutingCacheSize / 2;
                    for (int j = 0; j < evict && _routingCacheOrder.Count > 0; j++)
                    {
                        var oldKey = _routingCacheOrder.Dequeue();
                        _routingCache.Remove(oldKey);
                    }
                }
                if (!_routingCache.ContainsKey(key))
                    _routingCacheOrder.Enqueue(key);
                _routingCache[key] = result;
            }
            return result;
        }
        catch
        {
            _perRouteWastedCalls++;
            return null;
        }
    }

    public (List<Coordinate>? route, bool pushRerouted, double overlapBefore, double overlapAfter, int pushAttemptsUsed) RouteSegmentWithCumulativeAvoidance(
        IProfileInstance profile,
        Coordinate from,
        Coordinate to,
        HashSet<RouteGeometryUtils.EdgeKey> usedEdges,
        Dictionary<RouteGeometryUtils.EdgeKey, int>? edgeAge = null,
        int segmentAge = -1)
    {
        var normal = RouteSingleSegment(profile, from, to);
        if (normal == null) return (null, false, 0, 0, 0);

        if (usedEdges.Count == 0)
            return (normal, false, 0, 0, 0);

        double overlap = edgeAge != null
            ? (segmentAge >= 0
                ? RouteGeometryUtils.CalculateWeightedOverlap(normal, edgeAge, segmentAge)
                : RouteGeometryUtils.CalculateWeightedOverlap(normal, edgeAge))
            : RouteGeometryUtils.CalculateSegmentOverlap(normal, usedEdges);
        if (overlap <= OverlapThreshold)
            return (normal, false, overlap, overlap, 0);

        List<Coordinate>? bestRoute = normal;
        double bestOverlap = overlap;

        double segDist = RouteGeometryUtils.HaversineDistance(from, to);

        Coordinate pushCenter = RouteGeometryUtils.FindOverlapCenter(normal, usedEdges) ?? new Coordinate((from.Lat + to.Lat) / 2, (from.Lon + to.Lon) / 2);
        double roadAngle = RouteGeometryUtils.ComputeSegmentDirection(normal, pushCenter);

        double bestDirectness = bestRoute != null ? RouteGeometryUtils.CalculateDirectness(bestRoute) : 1.0;

        for (int attempt = 0; attempt < PushAttempts; attempt++)
        {
            double prevBestOverlap = bestOverlap;
            double pushDist = Math.Max(1000, segDist * (0.3 + attempt * 0.25));
            int dir = attempt % 4;
            double angle;
            if (dir == 0)
                angle = roadAngle + Math.PI / 2 + (Random.Shared.NextDouble() - 0.5) * 0.8;
            else if (dir == 1)
                angle = roadAngle - Math.PI / 2 + (Random.Shared.NextDouble() - 0.5) * 0.8;
            else if (dir == 2)
                angle = roadAngle + (Random.Shared.NextDouble() - 0.5) * 0.8;
            else
                angle = roadAngle + Math.PI + (Random.Shared.NextDouble() - 0.5) * 0.8;

            double pushLat = pushCenter.Lat + (pushDist / GeoConstants.MetersPerDegreeLat) * Math.Sin(angle);
            double pushLon = pushCenter.Lon + (pushDist / (GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(pushCenter.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(angle);

            var pushPoint = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(pushLat, pushLon));
            if (pushPoint == null) continue;

            var viaRoute1 = RouteSingleSegment(profile, from, pushPoint);
            if (viaRoute1 == null) continue;

            var viaRoute2 = RouteSingleSegment(profile, pushPoint, to);
            if (viaRoute2 == null) continue;

            var combined = new List<Coordinate>(viaRoute1);
            combined.AddRange(viaRoute2.GetRange(1, viaRoute2.Count - 1));

            double combinedOverlap = edgeAge != null
                ? (segmentAge >= 0
                    ? RouteGeometryUtils.CalculateWeightedOverlap(combined, edgeAge, segmentAge)
                    : RouteGeometryUtils.CalculateWeightedOverlap(combined, edgeAge))
                : RouteGeometryUtils.CalculateSegmentOverlap(combined, usedEdges);

            // Composite quality scoring (GraphHopper-inspired)
            // Score = overlapWeight * overlap + plateauWeight * (1 - plateau) + directnessWeight * directness
            // Lower score = better route
            double directness = RouteGeometryUtils.CalculateDirectness(combined);
            double plateauRatio = 1.0 - combinedOverlap; // unique sections ratio
            double compositeScore = 7.0 * combinedOverlap + (-0.2) * plateauRatio + 0.8 * (1.0 - directness);

            double bestCompositeScore = 7.0 * bestOverlap + (-0.2) * (1.0 - bestOverlap) + 0.8 * (1.0 - bestDirectness);

            if (compositeScore < bestCompositeScore)
            {
                bestOverlap = combinedOverlap;
                bestRoute = combined;
                bestDirectness = directness;
            }

            if (bestOverlap <= OverlapThreshold)
                return (bestRoute, true, overlap, bestOverlap, attempt + 1);

            if (bestOverlap <= 0.01)
                return (bestRoute, true, overlap, bestOverlap, attempt + 1);

            if (attempt > 0 && bestOverlap >= prevBestOverlap * 0.95)
                return (bestRoute, bestOverlap < overlap, overlap, bestOverlap, attempt + 1);
        }

        if (bestOverlap > OverlapRejectThreshold)
            return (null, false, overlap, bestOverlap, PushAttempts);

        return (bestRoute, bestOverlap < overlap, overlap, bestOverlap, PushAttempts);
    }
}
