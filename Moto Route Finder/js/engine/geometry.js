// Math utilities for route geometry calculations
import { CONSTANTS } from '../utils/constants.js';

export const Geometry = {
  /**
   * Haversine distance between two points (km)
   * @param {{lat: number, lng: number}} a - First point
   * @param {{lat: number, lng: number}} b - Second point
   * @returns {number} Distance in km
   */
  haversine(a, b) {
    const R = 6371; // Earth radius in km
    const dLat = (b.lat - a.lat) * Math.PI / 180;
    const dLng = (b.lng - a.lng) * Math.PI / 180;
    const x = Math.sin(dLat / 2) ** 2 +
      Math.cos(a.lat * Math.PI / 180) * Math.cos(b.lat * Math.PI / 180) *
      Math.sin(dLng / 2) ** 2;
    return 2 * R * Math.atan2(Math.sqrt(x), Math.sqrt(1 - x));
  },

  /**
   * Calculate total route distance with road detour factor
   * @param {{lat: number, lng: number}} start - Start point
   * @param {Array<{lat: number, lng: number}>} waypoints - Route waypoints
   * @returns {number} Estimated distance in km
   */
  routeDistance(start, waypoints) {
    const pts = [start, ...waypoints, start];
    let dist = 0;
    for (let i = 0; i < pts.length - 1; i++) {
      dist += this.haversine(pts[i], pts[i + 1]);
    }
    return dist * CONSTANTS.ROAD_DETOUR_FACTOR;
  },

  /**
   * Determine compass direction of route
   * @param {{lat: number, lng: number}} start - Start point
   * @param {Array<{lat: number, lng: number}>} waypoints - Route waypoints
   * @returns {string} Compass direction label (e.g., "NORTH LOOP")
   */
  compassDirection(start, waypoints) {
    const avgLat = waypoints.reduce((a, p) => a + p.lat, 0) / waypoints.length;
    const avgLng = waypoints.reduce((a, p) => a + p.lng, 0) / waypoints.length;
    const angle = Math.atan2(avgLng - start.lng, avgLat - start.lat) * 180 / Math.PI;
    const index = Math.round(((angle + 360) % 360) / 45) % 8;
    return `${CONSTANTS.COMPASS_DIRECTIONS[index]} LOOP`;
  },

  /**
   * Format duration in hours to human readable string
   * @param {number} hours - Duration in hours
   * @returns {string} Formatted string (e.g., "2h 30m")
   */
  formatDuration(hours) {
    const hh = Math.floor(hours);
    const mm = Math.round((hours - hh) * 60);
    if (hh === 0) return `${mm}min`;
    if (mm === 0) return `${hh}h`;
    return `${hh}h ${mm}m`;
  },

  /**
   * Angular sort waypoints around a center point
   * @param {{lat: number, lng: number}} center - Center point
   * @param {Array<{lat: number, lng: number}>} points - Points to sort
   * @returns {Array} Sorted waypoints
   */
  angularSort(center, points) {
    const getAngle = p => (Math.atan2(p.lng - center.lng, p.lat - center.lat) * 180 / Math.PI + 360) % 360;
    return [...points].sort((a, c) => getAngle(a) - getAngle(c));
  },

  /**
   * Find closest waypoint index to a given point
   * @param {Array<{lat: number, lng: number}>} waypoints - Waypoints
   * @param {number} lat - Target latitude
   * @param {number} lng - Target longitude
   * @returns {number} Index of closest waypoint
   */
  closestWaypointIndex(waypoints, lat, lng) {
    return waypoints.reduce((best, wp, i) => {
      const dist = (wp.lat - lat) ** 2 + (wp.lng - lng) ** 2;
      return dist < best.dist ? { dist, i } : best;
    }, { dist: Infinity, i: -1 }).i;
  },

  /**
   * Generate a correction waypoint perpendicular to a repeated path
   * @param {Object} segment - Repeated path segment
   * @param {number} sLat - Start latitude
   * @param {number} sLng - Start longitude
   * @param {number} offsetKm - Offset in km
   * @returns {{lat: number, lng: number}} Correction point
   */
  correctionWaypoint(segment, sLat, sLng, offsetKm) {
    const { midLat, midLng, dLat, dLng } = segment;
    const len = Math.sqrt(dLat * dLat + dLng * dLng) || 1;
    const p1 = { dLat: -dLng / len, dLng: dLat / len };
    const p2 = { dLat: dLng / len, dLng: -dLat / len };

    const dot = p => p.dLat * (sLat - midLat) + p.dLng * (sLng - midLng);
    const perp = dot(p1) < 0 ? p1 : p2;

    const kLat = 111.0;
    const kLng = 111.0 * Math.cos(midLat * Math.PI / 180);
    return {
      lat: midLat + perp.dLat * offsetKm / kLat,
      lng: midLng + perp.dLng * offsetKm / kLng
    };
  },

  /**
   * Compute compass bearing from point a to point b (degrees, 0=north, clockwise)
   * @param {{lat: number, lng: number}} a - Start point
   * @param {{lat: number, lng: number}} b - End point
   * @returns {number} Bearing in degrees [0, 360)
   */
  bearing(a, b) {
    const dLng = (b.lng - a.lng) * Math.PI / 180;
    const lat1 = a.lat * Math.PI / 180;
    const lat2 = b.lat * Math.PI / 180;
    const y = Math.sin(dLng) * Math.cos(lat2);
    const x = Math.cos(lat1) * Math.sin(lat2) - Math.sin(lat1) * Math.cos(lat2) * Math.cos(dLng);
    return (Math.atan2(y, x) * 180 / Math.PI + 360) % 360;
  },

  /**
   * Generate a correction waypoint by radial push from center
   * @param {Object} segment - Repeated path segment
   * @param {number} sLat - Start latitude
   * @param {number} sLng - Start longitude
   * @param {number} offsetKm - Offset in km
   * @returns {{lat: number, lng: number}} Correction point
   */
  radialPushWaypoint(segment, sLat, sLng, offsetKm) {
    const { midLat, midLng } = segment;
    const bearing = Math.atan2(midLat - sLat, midLng - sLng);
    const kLat = 111.0;
    const kLng = 111.0 * Math.cos(midLat * Math.PI / 180);
    return {
      lat: midLat + Math.sin(bearing) * offsetKm / kLat,
      lng: midLng + Math.cos(bearing) * offsetKm / kLng
    };
  },

  /**
   * Generate all permutations of an array (Heap's algorithm)
   * @param {Array} arr - Array to permute
   * @returns {Array<Array>} All permutations
   */
  permutations(arr) {
    const result = [];
    const permute = (a, n) => {
      if (n === 1) {
        result.push([...a]);
        return;
      }
      for (let i = 0; i < n; i++) {
        permute(a, n - 1);
        if (n % 2 === 1) {
          [a[0], a[n - 1]] = [a[n - 1], a[0]];
        } else {
          [a[i], a[n - 1]] = [a[n - 1], a[i]];
        }
      }
    };
    permute([...arr], arr.length);
    return result;
  },

  /**
   * Generate candidate points at multiple radii in compass directions
   * @param {{lat: number, lng: number}} center - Center point
   * @param {Array<number>} radii - Array of radii in km
   * @param {number} directions - Number of compass directions (e.g., 8)
   * @returns {Array<{lat: number, lng: number}>} Candidate points
   */
  wideRadiusCandidates(center, radii, directions) {
    const candidates = [];
    const kLat = 111.0;
    const kLng = 111.0 * Math.cos(center.lat * Math.PI / 180);
    
    for (const radius of radii) {
      for (let i = 0; i < directions; i++) {
        const angle = (i / directions) * 2 * Math.PI;
        candidates.push({
          lat: center.lat + (radius * Math.sin(angle)) / kLat,
          lng: center.lng + (radius * Math.cos(angle)) / kLng
        });
      }
    }
    
    return candidates;
  },

  /**
   * Check if two route legs spatially overlap
   * @param {Array<Array<number>>} legA - First leg geometry [[lat,lng], ...]
   * @param {Array<Array<number>>} legB - Second leg geometry [[lat,lng], ...]
   * @param {number} thresholdKm - Proximity threshold in km
   * @returns {{overlap: boolean, overlapKm: number}} Overlap status and distance
   */
  legsOverlap(legA, legB, thresholdKm = 0.1) {
    if (!legA.length || !legB.length) return { overlap: false, overlapKm: 0, overlapRatio: 0 };
    
    let overlapPoints = 0;
    let totalPoints = 0;
    let overlapDistKm = 0;
    
    for (let i = 0; i < legA.length; i += 5) {
      const [latA, lngA] = legA[i];
      const ptA = { lat: latA, lng: lngA };
      
      for (let j = 0; j < legB.length; j += 5) {
        const [latB, lngB] = legB[j];
        const ptB = { lat: latB, lng: lngB };
        
        const dist = this.haversine(ptA, ptB);
        if (dist < thresholdKm) {
          overlapPoints++;
          if (i > 0 && j > 0) {
            const prevA = { lat: legA[i-1][0], lng: legA[i-1][1] };
            overlapDistKm += this.haversine(prevA, ptA);
          }
          break;
        }
      }
      totalPoints++;
    }
    
    const overlapRatio = totalPoints > 0 ? overlapPoints / totalPoints : 0;
    return {
      overlap: overlapRatio > 0.3,
      overlapKm: overlapDistKm,
      overlapRatio
    };
  }
};
