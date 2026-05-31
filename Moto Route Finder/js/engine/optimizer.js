// Route scoring and repeated path detection
import { CONSTANTS } from '../utils/constants.js';
import { Geometry } from './geometry.js';

export const RouteOptimizer = {
  /**
   * Find repeated/overlapping paths in route geometry via grid-cell detection
   * @param {Array<Array<number>>} geometry - Array of [lat, lng] pairs
   * @returns {Array<Object>} Array of repeated path segments
   */
  findRepeatedPaths(geometry) {
    const G = CONSTANTS.GRID_SIZE;
    const seen = new Map();
    const hits = [];
    const totalLen = geometry.length;

    for (let i = 0; i < geometry.length - 1; i++) {
      const [la, lna] = geometry[i];
      const [lb, lnb] = geometry[i + 1];

      if (Math.abs(lb - la) < CONSTANTS.MIN_SEGMENT_LENGTH &&
          Math.abs(lnb - lna) < CONSTANTS.MIN_SEGMENT_LENGTH) continue;

      const ga = `${Math.round(la / G)},${Math.round(lna / G)}`;
      const gb = `${Math.round(lb / G)},${Math.round(lnb / G)}`;
      const keyFwd = `${ga}>${gb}`;
      const keyRev = `${gb}>${ga}`;

      if (seen.has(keyRev)) {
        // Check if this is actually a stem (opposite directions)
        const prevSeg = seen.get(keyRev);
        const bearing1 = Geometry.bearing({ lat: prevSeg.midLat, lng: prevSeg.midLng }, 
                                           { lat: prevSeg.midLat + prevSeg.dLat, lng: prevSeg.midLng + prevSeg.dLng });
        const bearing2 = Geometry.bearing({ lat: (la + lb) / 2, lng: (lna + lnb) / 2 }, 
                                           { lat: lb, lng: lnb });
        const bearingDiff = Math.abs(bearing1 - bearing2);
        const normalizedDiff = Math.min(bearingDiff, 360 - bearingDiff);
        
        if (normalizedDiff > (180 - CONSTANTS.STEM_BEARING_TOLERANCE)) {
          hits.push({
            i,
            midLat: (la + lb) / 2,
            midLng: (lna + lnb) / 2,
            dLat: lb - la,
            dLng: lnb - lna,
            _isStem: true
          });
        }
      } else if (seen.has(keyFwd)) {
        hits.push({
          i,
          midLat: (la + lb) / 2,
          midLng: (lna + lnb) / 2,
          dLat: lb - la,
          dLng: lnb - lna,
          _sameDir: true
        });
      }

      seen.set(keyFwd, { i, midLat: (la + lb) / 2, midLng: (lna + lnb) / 2, dLat: lb - la, dLng: lnb - lna });
    }

    if (!hits.length) return [];

    const groups = [];
    let cur = [hits[0]];
    for (let j = 1; j < hits.length; j++) {
      if (hits[j].i - cur[cur.length - 1].i <= CONSTANTS.STEM_GROUP_GAP) {
        cur.push(hits[j]);
      } else {
        groups.push(cur);
        cur = [hits[j]];
      }
    }
    groups.push(cur);

    return groups.map(grp => {
      let distKm = 0;
      const startI = grp[0].i;
      const endI = grp[grp.length - 1].i;

      for (let k = startI; k <= endI && k < geometry.length - 1; k++) {
        const [la, lna] = geometry[k];
        const [lb, lnb] = geometry[k + 1] || [0, 0];
        distKm += Geometry.haversine({ lat: la, lng: lna }, { lat: lb, lng: lnb });
      }

      const mid = grp[Math.floor(grp.length / 2)];
      const frac = mid.i / totalLen;
      const nearStart = frac < CONSTANTS.NEAR_START_THRESHOLD || frac > CONSTANTS.NEAR_END_THRESHOLD;
      const isStem = grp.some(h => h._isStem);

      const turnaroundI = Math.floor((startI + endI) / 2);
      const turnaroundPt = geometry[turnaroundI] || geometry[startI];
      const turnaroundLat = turnaroundPt[0];
      const turnaroundLng = turnaroundPt[1];

      return {
        segs: grp.map(h => ({
          midLat: h.midLat,
          midLng: h.midLng,
          dLat: h.dLat,
          dLng: h.dLng
        })),
        midLat: mid.midLat,
        midLng: mid.midLng,
        dLat: mid.dLat,
        dLng: mid.dLng,
        turnaroundLat,
        turnaroundLng,
        distanceKm: distKm,
        count: grp.length,
        nearStart,
        _isStem: isStem
      };
    }).sort((a, b) => b.distanceKm - a.distanceKm);
  },

  /**
   * Identify the waypoint causing a stem (the dead-end waypoint)
   * @param {Array<{lat: number, lng: number}>} waypoints - Route waypoints
   * @param {Object} stem - Stem segment from findRepeatedPaths
   * @param {{lat: number, lng: number}} start - Start point
   * @returns {number} Index of the dead-end waypoint
   */
  findDeadEndWaypoint(waypoints, stem, start) {
    const turnaround = { lat: stem.turnaroundLat, lng: stem.turnaroundLng };
    
    let minDist = Infinity;
    let deadEndIdx = -1;
    
    for (let i = 0; i < waypoints.length; i++) {
      const wp = waypoints[i];
      const distFromTurnaround = Geometry.haversine(turnaround, wp);
      
      if (distFromTurnaround < minDist) {
        minDist = distFromTurnaround;
        deadEndIdx = i;
      }
    }
    
    if (deadEndIdx === -1) {
      return Geometry.closestWaypointIndex(waypoints, stem.midLat, stem.midLng);
    }
    
    return deadEndIdx;
  },

  /**
   * Find overlapping legs by splitting geometry at waypoints
   * @param {Array<Array<number>>} geometry - Route geometry
   * @param {Array<{lat: number, lng: number}>} waypoints - Route waypoints
   * @param {{lat: number, lng: number}} start - Start point
   * @returns {Array<Object>} Array of overlapping leg pairs
   */
  findLegOverlaps(geometry, waypoints, start) {
    if (!geometry.length || !waypoints.length) return [];
    
    const allPts = [start, ...waypoints, start];
    const legs = [];
    let geomIdx = 0;
    
    for (let i = 0; i < allPts.length - 1; i++) {
      const legStart = allPts[i];
      const legEnd = allPts[i + 1];
      const legGeom = [];
      
      while (geomIdx < geometry.length) {
        const [lat, lng] = geometry[geomIdx];
        legGeom.push([lat, lng]);
        
        const distToEnd = Geometry.haversine({ lat, lng }, legEnd);
        geomIdx++;
        
        if (distToEnd < 0.05) break;
      }
      
      if (legGeom.length > 1) {
        legs.push({
          index: i,
          startWp: legStart,
          endWp: legEnd,
          geometry: legGeom
        });
      }
    }
    
    const overlaps = [];
    for (let i = 0; i < legs.length; i++) {
      for (let j = i + 1; j < legs.length; j++) {
        const isAdjacent = Math.abs(i - j) === 1;
        
        const result = Geometry.legsOverlap(legs[i].geometry, legs[j].geometry, 0.15);
        if (result.overlap) {
          if (isAdjacent && result.overlapRatio < 0.5) {
            continue;
          }
          
          overlaps.push({
            legA: i,
            legB: j,
            overlapKm: result.overlapKm,
            overlapRatio: result.overlapRatio,
            wpBetween: waypoints[i] || waypoints[j - 1]
          });
        }
      }
    }
    
    return overlaps;
  },

  /**
   * Score a route based on repeated paths and distance
   * Lower score = better route
   * @param {Array<Array<number>>} geometry - Route geometry
   * @param {number} distance - Total route distance in km
   * @param {Array<{lat: number, lng: number}>} [waypoints] - Optional waypoints for leg analysis
   * @param {{lat: number, lng: number}} [start] - Optional start point for leg analysis
   * @returns {number} Score (lower is better)
   */
  score(geometry, distance, waypoints, start) {
    const paths = this.findRepeatedPaths(geometry);
    let totalRepKm = 0;

    for (const p of paths) {
      const weight = p.nearStart ? 3 : 1;
      const stemMultiplier = p._isStem ? 2.5 : 1;
      totalRepKm += p.distanceKm * weight * stemMultiplier;
    }
    
    if (waypoints && start) {
      const legOverlaps = this.findLegOverlaps(geometry, waypoints, start);
      for (const overlap of legOverlaps) {
        totalRepKm += overlap.overlapKm * 3.0;
      }
    }

    const repRatio = distance > 0 ? totalRepKm / distance : 0;
    return totalRepKm * CONSTANTS.REPEATED_PATH_WEIGHT +
           repRatio * CONSTANTS.REP_RATIO_WEIGHT +
           distance;
  }
};
