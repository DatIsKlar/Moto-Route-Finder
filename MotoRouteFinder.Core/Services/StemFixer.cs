using System;
using System.Collections.Generic;
using System.Linq;
using Itinero;
using Itinero.Profiles;
using MotoRouteFinder.Helpers;
using MotoRouteFinder.Models;

namespace MotoRouteFinder.Services;

/// <summary>
/// Handles stem fixing pipeline: TryFixStem, GenerateReplacementWaypoint, TryMultiHopSplit.
/// </summary>
public class StemFixer
{
    private readonly MapRepository _mapRepository;
    private readonly RoadClassifier _roadClassifier;
    private readonly RouteAssembler _routeAssembler;
    private readonly EdgeBlocker _edgeBlocker;
    private long _fixStemTotalMs;
    private long _replacementTotalMs;
    private long _multiHopTotalMs;
    private int _fixStemCallCount;
    private int _fixStemSuccessCount;
    private int _replacementCallCount;
    private int _replacementSuccessCount;
    private int _multiHopCallCount;
    private int _multiHopSuccessCount;

    public long FixStemTotalMs => _fixStemTotalMs;
    public long ReplacementTotalMs => _replacementTotalMs;
    public long MultiHopTotalMs => _multiHopTotalMs;
    public int FixStemCallCount => _fixStemCallCount;
    public int FixStemSuccessCount => _fixStemSuccessCount;
    public int ReplacementCallCount => _replacementCallCount;
    public int ReplacementSuccessCount => _replacementSuccessCount;
    public int MultiHopCallCount => _multiHopCallCount;
    public int MultiHopSuccessCount => _multiHopSuccessCount;

    public StemFixer(MapRepository mapRepository, RoadClassifier roadClassifier, RouteAssembler routeAssembler, EdgeBlocker edgeBlocker)
    {
        _mapRepository = mapRepository;
        _roadClassifier = roadClassifier;
        _routeAssembler = routeAssembler;
        _edgeBlocker = edgeBlocker;
    }

    public void ResetTimers()
    {
        _fixStemTotalMs = 0;
        _replacementTotalMs = 0;
        _multiHopTotalMs = 0;
        _fixStemCallCount = 0;
        _fixStemSuccessCount = 0;
        _replacementCallCount = 0;
        _replacementSuccessCount = 0;
        _multiHopCallCount = 0;
        _multiHopSuccessCount = 0;
    }

    public void AddFixStemTime(long ms) => _fixStemTotalMs += ms;
    public void AddReplacementTime(long ms) => _replacementTotalMs += ms;
    public void AddMultiHopTime(long ms) => _multiHopTotalMs += ms;
    public void IncrementFixStemCall() => _fixStemCallCount++;
    public void IncrementFixStemSuccess() => _fixStemSuccessCount++;
    public void IncrementReplacementCall() => _replacementCallCount++;
    public void IncrementReplacementSuccess() => _replacementSuccessCount++;
    public void IncrementMultiHopCall() => _multiHopCallCount++;
    public void IncrementMultiHopSuccess() => _multiHopSuccessCount++;

    public (List<Coordinate>? fixedRoute, Dictionary<string, double> debugInfo, FailureReasonCode reasonCode) TryFixStem(
        IProfileInstance profile,
        Coordinate from,
        Coordinate to,
        List<Coordinate> failedSegment,
        HashSet<RouteGeometryUtils.EdgeKey> usedEdges)
    {
        var resolvedFrom = _roadClassifier.TryResolveToRoadCounted(profile, from, 2000) ?? from;
        var resolvedTo = _roadClassifier.TryResolveToRoadCounted(profile, to, 2000) ?? to;

        var failedEdges = new HashSet<RouteGeometryUtils.EdgeKey>(RouteGeometryUtils.ExtractEdges(failedSegment));
        var stemEdgeIds = new HashSet<uint>();
        for (int i = 0; i < failedSegment.Count - 1; i++)
        {
            var result = _mapRepository.Router!.TryResolve(profile, (float)failedSegment[i].Lat, (float)failedSegment[i].Lon, 2000);
            if (!result.IsError)
                stemEdgeIds.Add(result.Value.EdgeId);
        }

        if (stemEdgeIds.Count == 0)
            return (null, new Dictionary<string, double> { ["stemEdgeCount"] = 0, ["reason"] = 0 }, FailureReasonCode.StemEdgesUnresolvable);

        var allEdgesList = stemEdgeIds.ToList();
        using var scope = new EdgeBlocker.BlockedEdgesScope(_edgeBlocker, allEdgesList);
        if (!scope.HasEdges)
            return (null, new Dictionary<string, double> { ["stemEdgeCount"] = stemEdgeIds.Count, ["reason"] = 1 }, FailureReasonCode.NoEdgesToBlock);

        var fixedRoute = _routeAssembler.RouteSingleSegment(profile, resolvedFrom, resolvedTo);
        if (fixedRoute != null)
        {
            double overlap = RouteGeometryUtils.CalculateSegmentOverlap(fixedRoute, usedEdges);
            if (overlap <= 0.30)
                return (fixedRoute, new Dictionary<string, double> { ["stemEdgeCount"] = stemEdgeIds.Count, ["altOverlap"] = overlap, ["strategy"] = 1 }, FailureReasonCode.None);
        }

        scope.Dispose();
        int halfCount = allEdgesList.Count / 2;
        if (halfCount > 0)
        {
            var halfEdges = allEdgesList.GetRange(halfCount, allEdgesList.Count - halfCount);
            using var scope2 = new EdgeBlocker.BlockedEdgesScope(_edgeBlocker, halfEdges);
            if (scope2.HasEdges)
            {
                var fixedRoute2 = _routeAssembler.RouteSingleSegment(profile, resolvedFrom, resolvedTo);
                if (fixedRoute2 != null)
                {
                    double overlap2 = RouteGeometryUtils.CalculateSegmentOverlap(fixedRoute2, usedEdges);
                    if (overlap2 <= 0.30)
                        return (fixedRoute2, new Dictionary<string, double> { ["stemEdgeCount"] = halfEdges.Count, ["altOverlap"] = overlap2, ["strategy"] = 2 }, FailureReasonCode.None);
                }
            }
        }

        int quarterCount = allEdgesList.Count / 4;
        if (quarterCount > 0)
        {
            var quarterEdges = allEdgesList.GetRange(0, quarterCount);
            using var scope3 = new EdgeBlocker.BlockedEdgesScope(_edgeBlocker, quarterEdges);
            if (scope3.HasEdges)
            {
                var fixedRoute3 = _routeAssembler.RouteSingleSegment(profile, resolvedFrom, resolvedTo);
                if (fixedRoute3 != null)
                {
                    double overlap3 = RouteGeometryUtils.CalculateSegmentOverlap(fixedRoute3, usedEdges);
                    if (overlap3 <= 0.30)
                        return (fixedRoute3, new Dictionary<string, double> { ["stemEdgeCount"] = quarterEdges.Count, ["altOverlap"] = overlap3, ["strategy"] = 3 }, FailureReasonCode.None);
                }
            }
        }

        if (quarterCount > 0)
        {
            var lastQuarterEdges = allEdgesList.GetRange(allEdgesList.Count - quarterCount, quarterCount);
            using var scope4 = new EdgeBlocker.BlockedEdgesScope(_edgeBlocker, lastQuarterEdges);
            if (scope4.HasEdges)
            {
                var fixedRoute4 = _routeAssembler.RouteSingleSegment(profile, resolvedFrom, resolvedTo);
                if (fixedRoute4 != null)
                {
                    double overlap4 = RouteGeometryUtils.CalculateSegmentOverlap(fixedRoute4, usedEdges);
                    if (overlap4 <= 0.30)
                        return (fixedRoute4, new Dictionary<string, double> { ["stemEdgeCount"] = lastQuarterEdges.Count, ["altOverlap"] = overlap4, ["strategy"] = 4 }, FailureReasonCode.None);
                }
            }
        }

        if (halfCount > 0 && halfCount < allEdgesList.Count)
        {
            var firstHalfEdges = allEdgesList.GetRange(0, halfCount);
            using var scope5 = new EdgeBlocker.BlockedEdgesScope(_edgeBlocker, firstHalfEdges);
            if (scope5.HasEdges)
            {
                var fixedRoute5 = _routeAssembler.RouteSingleSegment(profile, resolvedFrom, resolvedTo);
                if (fixedRoute5 != null)
                {
                    double overlap5 = RouteGeometryUtils.CalculateSegmentOverlap(fixedRoute5, usedEdges);
                    if (overlap5 <= 0.30)
                        return (fixedRoute5, new Dictionary<string, double> { ["stemEdgeCount"] = firstHalfEdges.Count, ["altOverlap"] = overlap5, ["strategy"] = 5 }, FailureReasonCode.None);
                }
            }
        }

        if (halfCount > 0 && halfCount < allEdgesList.Count)
        {
            int quarterStart = allEdgesList.Count / 4;
            int quarterLen = allEdgesList.Count / 2;
            var middleHalfEdges = allEdgesList.GetRange(quarterStart, quarterLen);
            using var scope6 = new EdgeBlocker.BlockedEdgesScope(_edgeBlocker, middleHalfEdges);
            if (scope6.HasEdges)
            {
                var fixedRoute6 = _routeAssembler.RouteSingleSegment(profile, resolvedFrom, resolvedTo);
                if (fixedRoute6 != null)
                {
                    double overlap6 = RouteGeometryUtils.CalculateSegmentOverlap(fixedRoute6, usedEdges);
                    if (overlap6 <= 0.30)
                        return (fixedRoute6, new Dictionary<string, double> { ["stemEdgeCount"] = middleHalfEdges.Count, ["altOverlap"] = overlap6, ["strategy"] = 6 }, FailureReasonCode.None);
                }
            }
        }

        return (null, new Dictionary<string, double> { ["stemEdgeCount"] = stemEdgeIds.Count, ["altRouteFound"] = fixedRoute != null ? 1 : 0, ["reason"] = 2 }, FailureReasonCode.NoAlternativeRoute);
    }

    public (Coordinate? point, Dictionary<string, double> debugInfo) GenerateReplacementWaypoint(
        IProfileInstance profile,
        Coordinate failed,
        Coordinate currentPos,
        bool avoidHighways,
        double stemLengthM)
    {
        double bearing = Math.Atan2(failed.Lat - currentPos.Lat, failed.Lon - currentPos.Lon);
        double dist = RouteGeometryUtils.HaversineDistance(currentPos, failed);
        double minReplaceDist = Math.Max(300, stemLengthM * 0.05);

        int onRoad = 0;
        int tooClose = 0;
        int wrongClass = 0;

        // Optimization: Reduced probe count from 31 to 20 (~35% reduction)
        // Maintains same strategy diversity while reducing expensive router resolves
        for (int attempt = 0; attempt < 8; attempt++)
        {
            double newDist = dist * (0.7 + Random.Shared.NextDouble() * 0.6);
            double newBearing = bearing + (Random.Shared.NextDouble() - 0.5) * Math.PI / 4;

            double lat = currentPos.Lat + (newDist / GeoConstants.MetersPerDegreeLat) * Math.Sin(newBearing);
            double lon = currentPos.Lon + (newDist / (GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(currentPos.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(newBearing);

            var resolved = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(lat, lon));
            if (resolved == null) continue;

            onRoad++;

            if (RouteGeometryUtils.HaversineDistance(resolved, failed) <= minReplaceDist)
            {
                tooClose++;
                continue;
            }

            var quality = _roadClassifier.ClassifyRoad(resolved, profile, avoidHighways);
            if (quality != RoadClassifier.RoadQuality.Preferred && quality != RoadClassifier.RoadQuality.Acceptable)
            {
                wrongClass++;
                continue;
            }

            return (resolved, new Dictionary<string, double> { ["attempts"] = attempt + 1, ["onRoad"] = onRoad });
        }

        int wideOnRoad = 0;
        int wideWrongClass = 0;
        int wideTooClose = 0;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            double newDist = dist * (1.2 + Random.Shared.NextDouble() * 0.8);
            double newBearing = bearing + (Random.Shared.NextDouble() - 0.5) * Math.PI / 2;

            double lat = currentPos.Lat + (newDist / GeoConstants.MetersPerDegreeLat) * Math.Sin(newBearing);
            double lon = currentPos.Lon + (newDist / (GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(currentPos.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(newBearing);

            var resolved = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(lat, lon));
            if (resolved == null) continue;

            wideOnRoad++;

            if (RouteGeometryUtils.HaversineDistance(resolved, failed) <= minReplaceDist)
            {
                wideTooClose++;
                continue;
            }

            var quality = _roadClassifier.ClassifyRoad(resolved, profile, avoidHighways);
            if (quality != RoadClassifier.RoadQuality.Preferred && quality != RoadClassifier.RoadQuality.Acceptable)
            {
                wideWrongClass++;
                continue;
            }

            return (resolved, new Dictionary<string, double> { ["attempts"] = 8 + attempt + 1, ["onRoad"] = onRoad + wideOnRoad, ["widePass"] = 1 });
        }

        int poorOnRoad = 0;
        int poorTooClose = 0;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            double newDist = dist * (0.6 + Random.Shared.NextDouble() * 1.0);
            double newBearing = bearing + (Random.Shared.NextDouble() - 0.5) * Math.PI / 3 * 2;

            double lat = currentPos.Lat + (newDist / GeoConstants.MetersPerDegreeLat) * Math.Sin(newBearing);
            double lon = currentPos.Lon + (newDist / (GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(currentPos.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(newBearing);

            var resolved = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(lat, lon));
            if (resolved == null) continue;

            poorOnRoad++;

            if (RouteGeometryUtils.HaversineDistance(resolved, failed) <= minReplaceDist)
            {
                poorTooClose++;
                continue;
            }

            var quality = _roadClassifier.ClassifyRoad(resolved, profile, avoidHighways);
            if (quality == RoadClassifier.RoadQuality.Blocked) continue;

            return (resolved, new Dictionary<string, double> { ["attempts"] = 12 + attempt + 1, ["onRoad"] = onRoad + wideOnRoad + poorOnRoad, ["poorPass"] = 1 });
        }

        // Fix 6: Bearing rotation fallback - try 90° and 270° rotations (reduced from 4 to 2 attempts each)
        double[] rotationAngles = { Math.PI / 2, -Math.PI / 2 };
        foreach (var rotation in rotationAngles)
        {
            double rotatedBearing = bearing + rotation;
        for (int attempt = 0; attempt < 2; attempt++)
            {
                double newDist = dist * (0.5 + Random.Shared.NextDouble() * 0.8);
                double newBearing = rotatedBearing + (Random.Shared.NextDouble() - 0.5) * Math.PI / 6;

                double lat = currentPos.Lat + (newDist / GeoConstants.MetersPerDegreeLat) * Math.Sin(newBearing);
                double lon = currentPos.Lon + (newDist / (GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(currentPos.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(newBearing);

                var resolved = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(lat, lon));
                if (resolved == null) continue;

                if (RouteGeometryUtils.HaversineDistance(resolved, failed) <= minReplaceDist)
                    continue;

                var quality = _roadClassifier.ClassifyRoad(resolved, profile, avoidHighways);
                if (quality == RoadClassifier.RoadQuality.Blocked) continue;

                return (resolved, new Dictionary<string, double> { ["attempts"] = 16 + attempt + 1, ["onRoad"] = onRoad + wideOnRoad + poorOnRoad, ["bearingRotation"] = 1 });
            }
        }

        return (null, new Dictionary<string, double> { ["attempts"] = 20, ["onRoad"] = onRoad + wideOnRoad + poorOnRoad, ["tooClose"] = tooClose + wideTooClose + poorTooClose, ["wrongClass"] = wrongClass + wideWrongClass });
    }

    public (Coordinate? hopMid, List<Coordinate>? hop1Seg, List<Coordinate>? hop2Seg, Dictionary<string, double> details) TryMultiHopSplit(
        IProfileInstance profile,
        Coordinate currentPos,
        Coordinate candidate,
        List<Coordinate> failedSeg,
        HashSet<RouteGeometryUtils.EdgeKey> allUsedEdges,
        bool avoidHighways)
    {
        double segDist = RouteGeometryUtils.CalculateDistance(failedSeg);
        if (segDist <= 5000)
            return (null, null, null, new Dictionary<string, double> { ["segmentTooShort"] = 1, ["segDistM"] = segDist });

        double bearing = Math.Atan2(candidate.Lat - currentPos.Lat, candidate.Lon - currentPos.Lon);

        int midpointsOnRoad = 0;
        int midpointsAcceptClass = 0;
        int midpointsTooClose = 0;
        int hop1Failed = 0;
        int hop2Failed = 0;

        double[][] fixedMidpoints = {
            new[] { 0.3, 0.0 },
            new[] { 0.5, 0.0 },
            new[] { 0.7, 0.0 },
            new[] { 0.5, 3000.0 },
            new[] { 0.5, 5000.0 },
            new[] { 0.4, 3000.0 },
        };
        var mhBudget = System.Diagnostics.Stopwatch.StartNew();
        foreach (var fm in fixedMidpoints)
        {
            if (mhBudget.ElapsedMilliseconds > 10000) break;

            double fRatio = fm[0];
            double fPerpOff = fm[1];
            double fAlongDist = segDist * fRatio;
            double fPerpAngle = bearing + Math.PI / 2;

            double fLat = currentPos.Lat + (fAlongDist / GeoConstants.MetersPerDegreeLat) * Math.Sin(bearing);
            double fLon = currentPos.Lon + (fAlongDist / (GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(currentPos.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(bearing);

            fLat += (fPerpOff / GeoConstants.MetersPerDegreeLat) * Math.Sin(fPerpAngle);
            fLon += (fPerpOff / (GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(currentPos.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(fPerpAngle);

            var hopMid = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(fLat, fLon));
            if (hopMid == null) continue;
            midpointsOnRoad++;

            var quality = _roadClassifier.ClassifyRoad(hopMid, profile, avoidHighways);
            if (quality != RoadClassifier.RoadQuality.Preferred && quality != RoadClassifier.RoadQuality.Acceptable) continue;
            midpointsAcceptClass++;

            if (RouteGeometryUtils.HaversineDistance(hopMid, currentPos) < 2000) { midpointsTooClose++; continue; }
            if (RouteGeometryUtils.HaversineDistance(hopMid, candidate) < 2000) { midpointsTooClose++; continue; }

            var (hop1, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, hopMid, allUsedEdges);
            if (hop1 == null || StemDetector.IsStemSegment(hop1, strict: true)) { hop1Failed++; continue; }

            double hop1Overlap = RouteGeometryUtils.CalculateSegmentOverlap(hop1, allUsedEdges);
            if (hop1Overlap > 0.35) { hop1Failed++; continue; }

            var tempUsedEdges = new HashSet<RouteGeometryUtils.EdgeKey>(allUsedEdges);
            tempUsedEdges.UnionWith(RouteGeometryUtils.ExtractEdges(hop1));

            var (hop2, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, hopMid, candidate, tempUsedEdges);
            if (hop2 == null || StemDetector.IsStemSegment(hop2, strict: true)) { hop2Failed++; continue; }

            double hop2Overlap = RouteGeometryUtils.CalculateSegmentOverlap(hop2, tempUsedEdges);
            if (hop2Overlap > 0.35) { hop2Failed++; continue; }

            var details = new Dictionary<string, double>
            {
                ["splitLat"] = hopMid.Lat,
                ["splitLon"] = hopMid.Lon,
                ["hop1LengthM"] = RouteGeometryUtils.CalculateDistance(hop1),
                ["hop2LengthM"] = RouteGeometryUtils.CalculateDistance(hop2),
                ["hop1Overlap"] = hop1Overlap,
                ["hop2Overlap"] = hop2Overlap,
                ["attemptsTaken"] = 0,
            };

            return (hopMid, hop1, hop2, details);
        }

        if (hop1Failed + hop2Failed >= 4)
        {
            return (null, null, null, new Dictionary<string, double>
            {
                ["segDistM"] = segDist,
                ["midpointsOnRoad"] = midpointsOnRoad,
                ["midpointsAcceptClass"] = midpointsAcceptClass,
                ["midpointsTooClose"] = midpointsTooClose,
                ["hop1Failed"] = hop1Failed,
                ["hop2Failed"] = hop2Failed,
                ["earlyExit"] = 1,
            });
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (mhBudget.ElapsedMilliseconds > 10000) break;

            double ratio = 0.3 + Random.Shared.NextDouble() * 0.4;
            double alongDist = segDist * ratio;
            double perpOffset = (Random.Shared.NextDouble() - 0.5) * segDist * 0.3;
            double perpAngle = bearing + Math.PI / 2 * (attempt % 2 == 0 ? 1 : -1);

            double lat = currentPos.Lat + (alongDist / GeoConstants.MetersPerDegreeLat) * Math.Sin(bearing);
            double lon = currentPos.Lon + (alongDist / (GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(currentPos.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(bearing);

            lat += (perpOffset / GeoConstants.MetersPerDegreeLat) * Math.Sin(perpAngle);
            lon += (perpOffset / (GeoConstants.MetersPerDegreeLat * Math.Max(Math.Cos(currentPos.Lat * Math.PI / 180), GeoConstants.MinCosLat))) * Math.Cos(perpAngle);

            var hopMid = _roadClassifier.TryResolveToRoadCounted(profile, new Coordinate(lat, lon));
            if (hopMid == null) continue;
            midpointsOnRoad++;

            var quality = _roadClassifier.ClassifyRoad(hopMid, profile, avoidHighways);
            if (quality != RoadClassifier.RoadQuality.Preferred && quality != RoadClassifier.RoadQuality.Acceptable) continue;
            midpointsAcceptClass++;

            if (RouteGeometryUtils.HaversineDistance(hopMid, currentPos) < 2000) { midpointsTooClose++; continue; }
            if (RouteGeometryUtils.HaversineDistance(hopMid, candidate) < 2000) { midpointsTooClose++; continue; }

            var (hop1, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, currentPos, hopMid, allUsedEdges);
            if (hop1 == null || StemDetector.IsStemSegment(hop1, strict: true)) { hop1Failed++; continue; }

            double hop1Overlap = RouteGeometryUtils.CalculateSegmentOverlap(hop1, allUsedEdges);
            if (hop1Overlap > 0.35) { hop1Failed++; continue; }

            var tempUsedEdges = new HashSet<RouteGeometryUtils.EdgeKey>(allUsedEdges);
            tempUsedEdges.UnionWith(RouteGeometryUtils.ExtractEdges(hop1));

            var (hop2, _, _, _, _) = _routeAssembler.RouteSegmentWithCumulativeAvoidance(profile, hopMid, candidate, tempUsedEdges);
            if (hop2 == null || StemDetector.IsStemSegment(hop2, strict: true)) { hop2Failed++; continue; }

            double hop2Overlap = RouteGeometryUtils.CalculateSegmentOverlap(hop2, tempUsedEdges);
            if (hop2Overlap > 0.35) { hop2Failed++; continue; }

            var details = new Dictionary<string, double>
            {
                ["splitLat"] = hopMid.Lat,
                ["splitLon"] = hopMid.Lon,
                ["hop1LengthM"] = RouteGeometryUtils.CalculateDistance(hop1),
                ["hop2LengthM"] = RouteGeometryUtils.CalculateDistance(hop2),
                ["hop1Overlap"] = hop1Overlap,
                ["hop2Overlap"] = hop2Overlap,
                ["attemptsTaken"] = attempt + 1,
            };

            return (hopMid, hop1, hop2, details);
        }

        return (null, null, null, new Dictionary<string, double>
        {
            ["segDistM"] = segDist,
            ["midpointsOnRoad"] = midpointsOnRoad,
            ["midpointsAcceptClass"] = midpointsAcceptClass,
            ["midpointsTooClose"] = midpointsTooClose,
            ["hop1Failed"] = hop1Failed,
            ["hop2Failed"] = hop2Failed,
        });
    }

}
