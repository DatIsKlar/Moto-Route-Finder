// Waypoint generation and snapping logic
import { CONSTANTS } from '../utils/constants.js';
import { Geometry } from './geometry.js';
import { Routing } from '../api/routing.js';
import { RouteOptimizer } from './optimizer.js';

export const WaypointEngine = {
  /**
   * Generate waypoints in a circular pattern around a center point
   * @param {number} cLat - Center latitude
   * @param {number} cLng - Center longitude
   * @param {number} dist - Target route distance in km
   * @param {number} n - Number of waypoints
   * @param {number} base - Base angle in radians
   * @returns {Array<{lat: number, lng: number, _a: number}>} Generated waypoints
   */
  generate(cLat, cLng, dist, n, base) {
    const r = dist / CONSTANTS.ROAD_DETOUR_FACTOR / (2 * Math.PI);
    const kL = 111.0;
    const kG = 111.0 * Math.cos(cLat * Math.PI / 180);
    const minAngle = (2 * Math.PI / n) * CONSTANTS.MIN_ANGULAR_SEPARATION;
    const sector = (2 * Math.PI) / n;
    const wps = [];

    for (let i = 0; i < n; i++) {
      const target = base + i * sector;
      const jitter = (Math.random() - 0.5) * sector * CONSTANTS.JITTER_FACTOR;
      let a = target + jitter;

      if (i > 0) {
        const prevA = wps[i - 1]._a;
        if (a - prevA < minAngle) a = prevA + minAngle;
        if (i === n - 1) {
          const firstA = wps[0]._a;
          if (firstA + 2 * Math.PI - a < minAngle) {
            a = firstA + 2 * Math.PI - minAngle - 0.01;
          }
        }
      }

      const ri = r * (CONSTANTS.RADIUS_VARIATION_MIN + Math.random() * (CONSTANTS.RADIUS_VARIATION_MAX - CONSTANTS.RADIUS_VARIATION_MIN));
      wps.push({
        _a: a,
        lat: cLat + (ri * Math.sin(a)) / kL,
        lng: cLng + (ri * Math.cos(a)) / kG
      });
    }

    return wps;
  },

  /**
   * Snap waypoints to roads and optimize the route
   * @param {number} sLat - Start latitude
   * @param {number} sLng - Start longitude
   * @param {Array} rawWps - Raw waypoints
   * @returns {Promise<Object>} Route result with geometry
   */
  async snapToRoads(sLat, sLng, rawWps) {
    const center = { lat: sLat, lng: sLng };

    const roadWps = await Promise.all(rawWps.map(p => Routing.snapToRoad(p.lat, p.lng)));
    const spaced = await this._enforceSpacing(rawWps, roadWps);

    let wps, result;

    if (spaced.length <= CONSTANTS.PERMUTATION_THRESHOLD) {
      const permResult = await this._tryPermutations(sLat, sLng, center, spaced);
      wps = permResult.wps;
      result = permResult.result;
    } else {
      const cw = Geometry.angularSort(center, spaced);
      const ccw = [...cw].reverse();

      const [initCw, initCcw] = await Promise.all([
        Routing.getRoute(sLat, sLng, cw),
        Routing.getRoute(sLat, sLng, ccw)
      ]);

      if (initCcw && (!initCw || RouteOptimizer.score(initCcw.geometry, initCcw.distance, ccw, center) < RouteOptimizer.score(initCw.geometry, initCw.distance, cw, center))) {
        wps = ccw;
        result = initCcw;
      } else if (initCw) {
        wps = cw;
        result = initCw;
      } else {
        wps = cw;
        result = initCw;
      }
    }

    if (!result) {
      return { wps, geometry: null, distance: Geometry.routeDistance(center, wps) };
    }

    let bestScore = RouteOptimizer.score(result.geometry, result.distance, wps, center);
    let noImprovStreak = 0;

    for (let iter = 0; iter < CONSTANTS.MAX_OPTIMIZATION_ITERATIONS; iter++) {
      const repeated = RouteOptimizer.findRepeatedPaths(result.geometry);
      if (!repeated.length) break;

      const paths = repeated.slice(0, CONSTANTS.MAX_PATHS_PER_ITERATION);

      const candidates = [];
      const seen = new Set();

      const addCandidate = (cwps, ordered = false) => {
        const key = cwps.map(p => `${p.lat.toFixed(5)},${p.lng.toFixed(5)}`).join('|');
        if (!seen.has(key)) {
          seen.add(key);
          candidates.push({ wps: cwps, ordered });
        }
      };

      for (const seg of paths) {
        const canPrune = wps.length > CONSTANTS.MIN_WAYPOINTS;

        if (seg._isStem) {
          const deadEndIdx = RouteOptimizer.findDeadEndWaypoint(wps, seg, center);
          const deadEndWp = wps[deadEndIdx];
          
          const wideAlts = await this._wideRadiusRelocate(deadEndWp);
          for (const alt of wideAlts) {
            const relocated = [...wps];
            relocated[deadEndIdx] = alt;
            addCandidate(relocated);
          }
          
          const alts = await Routing.snapToRoadAlternates(deadEndWp.lat, deadEndWp.lng);
          for (const alt of alts) {
            const relocated = [...wps];
            relocated[deadEndIdx] = alt;
            addCandidate(relocated);
          }
          
          if (canPrune) {
            const pruned = wps.filter((_, i) => i !== deadEndIdx);
            addCandidate(pruned);
          }
        }

        for (const offset of CONSTANTS.CORRECTION_OFFSETS) {
          const corrPt = await Routing.snapToRoad(
            ...(p => [p.lat, p.lng])(Geometry.correctionWaypoint(seg, sLat, sLng, offset))
          );
          addCandidate([...wps, corrPt]);
        }

        for (const offset of CONSTANTS.RADIAL_OFFSETS) {
          const radPt = await Routing.snapToRoad(
            ...(p => [p.lat, p.lng])(Geometry.radialPushWaypoint(seg, sLat, sLng, offset))
          );
          addCandidate([...wps, radPt]);
        }

        if (canPrune) {
          for (let wi = 0; wi < wps.length; wi++) {
            const pruned = wps.filter((_, i) => i !== wi);
            addCandidate(pruned);
          }
        }
      }

      if (wps.length >= 3) {
        const twoOptCandidates = this._generate2OptCandidates(wps);
        for (const cand of twoOptCandidates) {
          addCandidate(cand, true);
        }
      }

      const cResults = await Promise.all(
        candidates.map(c => {
          const routeWps = c.ordered ? c.wps : Geometry.angularSort(center, c.wps);
          return Routing.getRoute(sLat, sLng, routeWps);
        })
      );

      let improved = false;
      cResults.forEach((r, i) => {
        if (!r || r.distance > result.distance * CONSTANTS.DISTANCE_REJECTION_FACTOR) return;
        const c = candidates[i];
        const routeWps = c.ordered ? c.wps : Geometry.angularSort(center, c.wps);
        const score = RouteOptimizer.score(r.geometry, r.distance, routeWps, center);
        if (score < bestScore) {
          bestScore = score;
          result = r;
          wps = routeWps;
          improved = true;
        }
      });

      if (improved) {
        noImprovStreak = 0;
      } else {
        noImprovStreak++;
        if (noImprovStreak >= CONSTANTS.NO_IMPROVEMENT_LIMIT) break;
      }
    }

    return result;
  },

  /**
   * Try all permutations of waypoints (for small n)
   * @param {number} sLat - Start latitude
   * @param {number} sLng - Start longitude
   * @param {{lat: number, lng: number}} center - Center point
   * @param {Array} wps - Waypoints
   * @returns {Promise<{wps: Array, result: Object}>} Best permutation result
   */
  async _tryPermutations(sLat, sLng, center, wps) {
    if (wps.length <= 1) {
      const result = await Routing.getRoute(sLat, sLng, wps);
      return { wps, result };
    }

    const first = wps[0];
    const rest = wps.slice(1);
    const perms = Geometry.permutations(rest);
    
    const allOrderings = perms.map(perm => [first, ...perm]);
    
    const BATCH_SIZE = 12;
    let bestWps = allOrderings[0];
    let bestResult = null;
    let bestScore = Infinity;

    for (let i = 0; i < allOrderings.length; i += BATCH_SIZE) {
      const batch = allOrderings.slice(i, i + BATCH_SIZE);
      const results = await Promise.all(
        batch.map(ordering => Routing.getRoute(sLat, sLng, ordering))
      );

      results.forEach((r, j) => {
        if (!r) return;
        const score = RouteOptimizer.score(r.geometry, r.distance, batch[j], center);
        if (score < bestScore) {
          bestScore = score;
          bestResult = r;
          bestWps = batch[j];
        }
      });
    }

    return { wps: bestWps, result: bestResult };
  },

  /**
   * Generate 2-opt candidates by swapping pairs and reversing segments
   * @param {Array} wps - Current waypoints
   * @returns {Array<Array>} Array of candidate waypoint orderings
   */
  _generate2OptCandidates(wps) {
    const candidates = [];
    const n = wps.length;

    for (let i = 0; i < n - 1; i++) {
      for (let j = i + 1; j < n; j++) {
        const swapped = [...wps];
        [swapped[i], swapped[j]] = [swapped[j], swapped[i]];
        candidates.push(swapped);
      }
    }

    for (let i = 0; i < n - 1; i++) {
      for (let j = i + 2; j < n; j++) {
        const reversed = [...wps];
        const segment = reversed.splice(i, j - i + 1);
        reversed.splice(i, 0, ...segment.reverse());
        candidates.push(reversed);
      }
    }

    return candidates;
  },

  /**
   * Relocate a waypoint using wide-radius candidates
   * @param {{lat: number, lng: number}} wp - Waypoint to relocate
   * @returns {Promise<Array<{lat: number, lng: number}>>} Snapped candidates
   */
  async _wideRadiusRelocate(wp) {
    const rawCandidates = Geometry.wideRadiusCandidates(
      wp,
      CONSTANTS.RELOCATION_RADII_KM,
      CONSTANTS.RELOCATION_DIRECTIONS
    );

    const snapped = await Promise.all(
      rawCandidates.map(c => Routing.snapToRoad(c.lat, c.lng))
    );

    const unique = [];
    const seen = new Set();
    for (const s of snapped) {
      const key = `${s.lat.toFixed(5)},${s.lng.toFixed(5)}`;
      if (!seen.has(key)) {
        seen.add(key);
        unique.push(s);
      }
    }

    return unique;
  },

  /**
   * Enforce minimum spacing between waypoints by re-snapping close ones to alternates
   * @param {Array} rawWps - Original unsnapped waypoints (for alternate snapping)
   * @param {Array} snapped - Snapped waypoints
   * @returns {Promise<Array>} Spaced waypoints
   */
  async _enforceSpacing(rawWps, snapped) {
    const result = [...snapped];

    for (let i = 0; i < result.length; i++) {
      for (let j = i + 1; j < result.length; j++) {
        const dist = Geometry.haversine(result[i], result[j]);
        if (dist < CONSTANTS.MIN_WAYPOINT_SPACING_KM) {
          const alts = await Routing.snapToRoadAlternates(rawWps[j].lat, rawWps[j].lng);
          let bestAlt = result[j];
          let bestDist = dist;

          for (const alt of alts) {
            const d = Geometry.haversine(result[i], alt);
            if (d > bestDist) {
              bestDist = d;
              bestAlt = alt;
            }
          }

          if (bestDist < CONSTANTS.MIN_WAYPOINT_SPACING_KM) {
            const wideAlts = await this._wideRadiusRelocate(rawWps[j]);
            for (const alt of wideAlts) {
              const d = Geometry.haversine(result[i], alt);
              if (d > bestDist) {
                bestDist = d;
                bestAlt = alt;
              }
            }
          }

          result[j] = bestAlt;
        }
      }
    }

    return result;
  }
};
