using System;
using System.Collections.Generic;
using Itinero;
using Itinero.Attributes;
using Itinero.Profiles;
using MotoRouteFinder.Helpers;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Services;

/// <summary>
/// Classifies road quality and resolves points to road edges.
/// </summary>
public class RoadClassifier
{
    private readonly MapRepository _mapRepository;
    private readonly EdgeBlocker? _edgeBlocker;
    private int _resolveCount;
    private readonly Dictionary<(int lat, int lon), (RoadQuality quality, string? highway)> _resolveCache = new();
    private readonly Dictionary<(int lat, int lon), Coordinate?> _probeCache = new();
    private int _resolveCacheHitCount;
    private int _resolveCacheMissCount;
    private long _classificationMs;
    private long _resolutionMs;

    public enum RoadQuality { Preferred, Acceptable, Poor, Blocked }

    public int ResolveCount => _resolveCount;
    public int ResolveCacheHitCount => _resolveCacheHitCount;
    public int ResolveCacheMissCount => _resolveCacheMissCount;
    public int ConnectivityCacheHitCount => _resolveCacheHitCount; // reuse resolve cache metric
    public long ClassificationMs => _classificationMs;
    public long ResolutionMs => _resolutionMs;

    public RoadClassifier(MapRepository mapRepository, EdgeBlocker? edgeBlocker = null)
    {
        _mapRepository = mapRepository;
        _edgeBlocker = edgeBlocker;
    }

    public void ResetStats()
    {
        _resolveCount = 0;
        _resolveCacheHitCount = 0;
        _resolveCacheMissCount = 0;
        _classificationMs = 0;
        _resolutionMs = 0;
    }

    public void ResetCounters()
    {
        ResetStats();
        _resolveCache.Clear();
    }

    /// <summary>
    /// Clears the resolve cache to free memory when maps are unloaded.
    /// </summary>
    public void ClearCache()
    {
        _resolveCache.Clear();
        _probeCache.Clear();
    }

    public Coordinate? TryResolveToRoad(IProfileInstance profile, Coordinate point, float maxSearchDistance = 2000)
    {
        if (_mapRepository.Router == null) return null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = _mapRepository.Router.TryResolve(profile, (float)point.Lat, (float)point.Lon, maxSearchDistance);
        sw.Stop();
        _resolutionMs += sw.ElapsedMilliseconds;
        if (result.IsError) return null;
        var location = result.Value.LocationOnNetwork(_mapRepository.Router.Db);
        return new Coordinate(location.Latitude, location.Longitude);
    }

    public Coordinate? TryResolveToRoadCounted(IProfileInstance profile, Coordinate point, float maxSearchDistance = 2000)
    {
        _resolveCount++;
        return TryResolveToRoad(profile, point, maxSearchDistance);
    }

    /// <summary>
    /// Cached variant of TryResolveToRoad for coarse pass/fail probes only.
    /// Skips cache during block windows to prevent stale Profile=0 entries.
    /// Do NOT use for placement resolves — quantization changes route character.
    /// </summary>
    public Coordinate? TryResolveToRoadCachedProbe(IProfileInstance profile, Coordinate point, float maxSearchDistance = 2000)
    {
        if (_edgeBlocker?.HasActiveBlocks == true)
            return TryResolveToRoadCounted(profile, point, maxSearchDistance);

        int keyLat = (int)(point.Lat * 1e3);
        int keyLon = (int)(point.Lon * 1e3);
        var key = (keyLat, keyLon);

        if (_probeCache.TryGetValue(key, out var cached))
            return cached;

        var result = TryResolveToRoadCounted(profile, point, maxSearchDistance);
        _probeCache[key] = result;
        return result;
    }

    /// <summary>
    /// Single source of truth for road quality classification from OSM tags.
    /// Used by runtime ClassifyRoad() to classify resolved edges.
    /// </summary>
    public static (RoadQuality quality, string? highway) ClassifyFromTags(IAttributeCollection tags)
    {
        if (tags.TryGetValue("access", out string? access))
        {
            access = access.ToLowerInvariant();
            if (access is "private" or "no" or "customers" or "military" or "restricted")
                return (RoadQuality.Blocked, null);
        }

        if (tags.TryGetValue("motor_vehicle", out string? motorVehicle) && motorVehicle == "no")
            return (RoadQuality.Blocked, null);

        if (tags.TryGetValue("motorcycle", out string? motorcycle) && motorcycle == "no")
            return (RoadQuality.Blocked, null);

        string? highway = null;
        if (tags.TryGetValue("highway", out string? hw))
        {
            highway = hw.ToLowerInvariant();

            if (highway is "motorway" or "trunk" or "primary" or "secondary" or "tertiary")
                return (RoadQuality.Preferred, highway);

            if (highway is "unclassified" or "road")
                return (RoadQuality.Acceptable, highway);

            if (highway == "residential")
                return (RoadQuality.Acceptable, highway);

            if (highway == "living_street")
                return (RoadQuality.Poor, highway);

            if (highway == "service")
            {
                if (tags.TryGetValue("service", out string? serviceType))
                {
                    serviceType = serviceType.ToLowerInvariant();
                    if (serviceType is "driveway" or "private" or "parking_aisle")
                        return (RoadQuality.Blocked, highway);
                }
                return (RoadQuality.Poor, highway);
            }

            if (highway is "track")
            {
                if (tags.TryGetValue("tracktype", out string? tt))
                {
                    tt = tt.ToLowerInvariant();
                    if (tt is "grade1" or "grade2")
                        return (RoadQuality.Acceptable, highway);
                    if (tt is "grade4" or "grade5")
                        return (RoadQuality.Blocked, highway);
                    if (tt == "grade3")
                        return (RoadQuality.Poor, highway);
                }
                return (RoadQuality.Blocked, highway);
            }

            if (highway is "path" or "footway" or "cycleway" or "pedestrian" or "steps")
                return (RoadQuality.Blocked, highway);
        }

        if (tags.TryGetValue("surface", out string? surface))
        {
            surface = surface.ToLowerInvariant();
            if (surface is "unpaved" or "gravel" or "dirt" or "sand" or "ground" or "earth")
                return (RoadQuality.Blocked, highway);
            if (surface is "compacted" or "fine_gravel")
                return (RoadQuality.Poor, highway);
        }

        if (tags.TryGetValue("tracktype", out string? tracktype))
        {
            if (tracktype is "grade4" or "grade5")
                return (RoadQuality.Blocked, highway);
            if (tracktype == "grade3")
                return (RoadQuality.Poor, highway);
        }

        return (RoadQuality.Poor, highway);
    }

    public RoadQuality ClassifyRoad(Coordinate point, IProfileInstance profile, bool avoidHighways = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int cacheKeyLat = (int)(point.Lat * 1e3);
        int cacheKeyLon = (int)(point.Lon * 1e3);
        var cacheKey = (cacheKeyLat, cacheKeyLon);
        bool canCache = _edgeBlocker?.HasActiveBlocks != true;

        if (_resolveCache.TryGetValue(cacheKey, out var cached))
        {
            _resolveCacheHitCount++;
            sw.Stop();
            _classificationMs += sw.ElapsedMilliseconds;
            if (avoidHighways && cached.highway is "motorway" or "motorway_link")
                return RoadQuality.Blocked;
            return cached.quality;
        }

        _resolveCacheMissCount++;
        _resolveCount++;
        if (_mapRepository.Router == null || _mapRepository.RouterDb == null)
        {
            if (canCache) _resolveCache[cacheKey] = (RoadQuality.Blocked, null);
            return RoadQuality.Blocked;
        }

        var result = _mapRepository.Router.TryResolve(profile, (float)point.Lat, (float)point.Lon, 50);
        if (result.IsError)
        {
            if (canCache) _resolveCache[cacheKey] = (RoadQuality.Blocked, null);
            return RoadQuality.Blocked;
        }

        var edge = _mapRepository.RouterDb.Network.GetEdge(result.Value.EdgeId);

        // Classify from tags using shared logic
        var tags = _mapRepository.RouterDb.EdgeProfiles.Get(edge.Data.Profile);
        if (tags == null)
        {
            if (canCache) _resolveCache[cacheKey] = (RoadQuality.Poor, null);
            sw.Stop();
            _classificationMs += sw.ElapsedMilliseconds;
            return RoadQuality.Poor;
        }

        var (quality, highway) = ClassifyFromTags(tags);
        if (canCache) _resolveCache[cacheKey] = (quality, highway);
        sw.Stop();
        _classificationMs += sw.ElapsedMilliseconds;

        if (avoidHighways && highway is "motorway" or "motorway_link")
            return RoadQuality.Blocked;
        return quality;
    }

    public string? GetHighwayType(Coordinate point, IProfileInstance profile)
    {
        // Use same 1e3 granularity as ClassifyRoad so both share cache entries
        int cacheKeyLat = (int)(point.Lat * 1e3);
        int cacheKeyLon = (int)(point.Lon * 1e3);
        var cacheKey = (cacheKeyLat, cacheKeyLon);
        bool canCache = _edgeBlocker?.HasActiveBlocks != true;

        if (_resolveCache.TryGetValue(cacheKey, out var cached))
        {
            _resolveCacheHitCount++;
            return cached.highway;
        }

        _resolveCacheMissCount++;
        _resolveCount++;
        if (_mapRepository.Router == null || _mapRepository.RouterDb == null) return null;

        var result = _mapRepository.Router.TryResolve(profile, (float)point.Lat, (float)point.Lon, 50);
        if (result.IsError)
        {
            if (canCache) _resolveCache[cacheKey] = (RoadQuality.Blocked, null);
            return null;
        }

        var edge = _mapRepository.RouterDb.Network.GetEdge(result.Value.EdgeId);
        var tags = _mapRepository.RouterDb.EdgeProfiles.Get(edge.Data.Profile);
        string? highway = null;
        tags?.TryGetValue("highway", out highway);
        if (canCache) _resolveCache[cacheKey] = (RoadQuality.Poor, highway);
        return highway;
    }

    public bool HasRoadConnectivity(IProfileInstance profile, Coordinate point, double cosLat)
    {
        double dLat = 2000.0 / GeoConstants.MetersPerDegreeLat;
        double dLon = 2000.0 / (GeoConstants.MetersPerDegreeLat * Math.Max(cosLat, GeoConstants.MinCosLat));

        var probes = new[]
        {
            new Coordinate(point.Lat + dLat, point.Lon),
            new Coordinate(point.Lat - dLat, point.Lon),
            new Coordinate(point.Lat, point.Lon + dLon),
            new Coordinate(point.Lat, point.Lon - dLon),
        };

        int connections = 0;
        foreach (var probe in probes)
        {
            var resolved = TryResolveToRoadCachedProbe(profile, probe, 2000);
            if (resolved != null) connections++;
        }

        return connections >= 2;
    }
}
