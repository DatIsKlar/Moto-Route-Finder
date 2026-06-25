using System;
using System.Collections.Generic;
using Itinero;
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
    private int _resolveCount;
    private readonly Dictionary<(int lat, int lon), (RoadQuality quality, string? highway)> _resolveCache = new();
    private int _resolveCacheHitCount;
    private int _resolveCacheMissCount;
    private long _classificationMs;
    private long _resolutionMs;

    public enum RoadQuality { Preferred, Acceptable, Poor, Blocked }

    public int ResolveCount => _resolveCount;
    public int ResolveCacheHitCount => _resolveCacheHitCount;
    public int ResolveCacheMissCount => _resolveCacheMissCount;
    public int ConnectivityCacheHitCount => 0;
    public long ClassificationMs => _classificationMs;
    public long ResolutionMs => _resolutionMs;

    public RoadClassifier(MapRepository mapRepository)
    {
        _mapRepository = mapRepository;
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

    public RoadQuality ClassifyRoad(Coordinate point, IProfileInstance profile, bool avoidHighways = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int cacheKeyLat = (int)(point.Lat * 1e3);
        int cacheKeyLon = (int)(point.Lon * 1e3);
        var cacheKey = (cacheKeyLat, cacheKeyLon);

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
            _resolveCache[cacheKey] = (RoadQuality.Blocked, null);
            return RoadQuality.Blocked;
        }

        var result = _mapRepository.Router.TryResolve(profile, (float)point.Lat, (float)point.Lon, 50);
        if (result.IsError)
        {
            _resolveCache[cacheKey] = (RoadQuality.Blocked, null);
            return RoadQuality.Blocked;
        }

        var edge = _mapRepository.RouterDb.Network.GetEdge(result.Value.EdgeId);

        // Try pre-computed edge quality cache first (avoids re-classification)
        if (_mapRepository.EdgeQualityCache != null &&
            _mapRepository.EdgeQualityCache.TryGetValue(edge.Data.Profile, out var precomputed))
        {
            _resolveCache[cacheKey] = (precomputed.quality, precomputed.highway);
            sw.Stop();
            _classificationMs += sw.ElapsedMilliseconds;
            if (avoidHighways && precomputed.highway is "motorway" or "motorway_link")
                return RoadQuality.Blocked;
            return precomputed.quality;
        }

        // Fallback: classify from tags
        var tags = _mapRepository.RouterDb.EdgeProfiles.Get(edge.Data.Profile);
        if (tags == null)
        {
            _resolveCache[cacheKey] = (RoadQuality.Poor, null);
            sw.Stop();
            _classificationMs += sw.ElapsedMilliseconds;
            return RoadQuality.Poor;
        }

        if (tags.TryGetValue("access", out string access))
        {
            access = access.ToLowerInvariant();
            if (access is "private" or "no" or "customers" or "military" or "restricted")
            {
                _resolveCache[cacheKey] = (RoadQuality.Blocked, null);
                return RoadQuality.Blocked;
            }
        }

        if (tags.TryGetValue("motor_vehicle", out string motorVehicle) && motorVehicle == "no")
        {
            _resolveCache[cacheKey] = (RoadQuality.Blocked, null);
            return RoadQuality.Blocked;
        }

        if (tags.TryGetValue("motorcycle", out string motorcycle) && motorcycle == "no")
        {
            _resolveCache[cacheKey] = (RoadQuality.Blocked, null);
            return RoadQuality.Blocked;
        }

        string? highway = null;
        if (tags.TryGetValue("highway", out string hw))
        {
            highway = hw.ToLowerInvariant();

            if (avoidHighways && (highway == "motorway" || highway == "motorway_link"))
            {
                _resolveCache[cacheKey] = (RoadQuality.Blocked, highway);
                return RoadQuality.Blocked;
            }

            if (highway is "motorway" or "trunk" or "primary" or "secondary" or "tertiary")
            {
                _resolveCache[cacheKey] = (RoadQuality.Preferred, highway);
                return RoadQuality.Preferred;
            }

            if (highway is "unclassified" or "road")
            {
                _resolveCache[cacheKey] = (RoadQuality.Acceptable, highway);
                return RoadQuality.Acceptable;
            }

            if (highway == "residential")
            {
                _resolveCache[cacheKey] = (RoadQuality.Acceptable, highway);
                return RoadQuality.Acceptable;
            }
            if (highway == "living_street")
            {
                _resolveCache[cacheKey] = (RoadQuality.Poor, highway);
                return RoadQuality.Poor;
            }

            if (highway == "service")
            {
                if (tags.TryGetValue("service", out string serviceType))
                {
                    serviceType = serviceType.ToLowerInvariant();
                    if (serviceType is "driveway" or "private" or "parking_aisle")
                    {
                        _resolveCache[cacheKey] = (RoadQuality.Blocked, highway);
                        return RoadQuality.Blocked;
                    }
                }
                _resolveCache[cacheKey] = (RoadQuality.Poor, highway);
                return RoadQuality.Poor;
            }

            if (highway is "track")
            {
                if (tags.TryGetValue("tracktype", out string tt))
                {
                    tt = tt.ToLowerInvariant();
                    if (tt is "grade1" or "grade2")
                    {
                        _resolveCache[cacheKey] = (RoadQuality.Acceptable, highway);
                        return RoadQuality.Acceptable;
                    }
                    if (tt is "grade4" or "grade5")
                    {
                        _resolveCache[cacheKey] = (RoadQuality.Blocked, highway);
                        return RoadQuality.Blocked;
                    }
                    if (tt == "grade3")
                    {
                        _resolveCache[cacheKey] = (RoadQuality.Poor, highway);
                        return RoadQuality.Poor;
                    }
                }
                _resolveCache[cacheKey] = (RoadQuality.Blocked, highway);
                return RoadQuality.Blocked;
            }
            if (highway is "path" or "footway" or "cycleway" or "pedestrian" or "steps")
            {
                _resolveCache[cacheKey] = (RoadQuality.Blocked, highway);
                return RoadQuality.Blocked;
            }
        }

        if (tags.TryGetValue("surface", out string surface))
        {
            surface = surface.ToLowerInvariant();
            if (surface is "unpaved" or "gravel" or "dirt" or "sand" or "ground" or "earth")
            {
                _resolveCache[cacheKey] = (RoadQuality.Blocked, highway);
                return RoadQuality.Blocked;
            }
            if (surface is "compacted" or "fine_gravel")
            {
                _resolveCache[cacheKey] = (RoadQuality.Poor, highway);
                return RoadQuality.Poor;
            }
        }

        if (tags.TryGetValue("tracktype", out string tracktype))
        {
            if (tracktype is "grade4" or "grade5")
            {
                _resolveCache[cacheKey] = (RoadQuality.Blocked, highway);
                return RoadQuality.Blocked;
            }
            if (tracktype == "grade3")
            {
                _resolveCache[cacheKey] = (RoadQuality.Poor, highway);
                return RoadQuality.Poor;
            }
        }

        _resolveCache[cacheKey] = (RoadQuality.Poor, highway);
        sw.Stop();
        _classificationMs += sw.ElapsedMilliseconds;
        return RoadQuality.Poor;
    }

    public string? GetHighwayType(Coordinate point, IProfileInstance profile)
    {
        // Use same 1e3 granularity as ClassifyRoad so both share cache entries
        int cacheKeyLat = (int)(point.Lat * 1e3);
        int cacheKeyLon = (int)(point.Lon * 1e3);
        var cacheKey = (cacheKeyLat, cacheKeyLon);

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
            _resolveCache[cacheKey] = (RoadQuality.Blocked, null);
            return null;
        }

        var edge = _mapRepository.RouterDb.Network.GetEdge(result.Value.EdgeId);
        var tags = _mapRepository.RouterDb.EdgeProfiles.Get(edge.Data.Profile);
        string? highway = null;
        tags?.TryGetValue("highway", out highway);
        _resolveCache[cacheKey] = (RoadQuality.Poor, highway);
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
            var resolved = TryResolveToRoadCounted(profile, probe, 2000);
            if (resolved != null) connections++;
        }

        return connections >= 2;
    }
}
