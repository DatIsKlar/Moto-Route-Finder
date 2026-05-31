// Routing API wrappers (Valhalla and OSRM)
import { CONSTANTS } from '../utils/constants.js';
import { State } from '../state.js';

// Cache for Valhalla availability
let _valhallaAvailable = true;

export const Routing = {
  /**
   * Decode Valhalla 6-decimal encoded polyline
   * @param {string} encoded - Encoded polyline
   * @param {number} [precision=6] - Coordinate precision
   * @returns {Array<Array<number>>} Array of [lat, lng] pairs
   */
  decodePolyline(encoded, precision = 6) {
    const factor = 10 ** precision;
    const coords = [];
    let idx = 0, lat = 0, lng = 0;

    while (idx < encoded.length) {
      let shift = 0, result = 0, byte;
      do {
        byte = encoded.charCodeAt(idx++) - 63;
        result |= (byte & 0x1f) << shift;
        shift += 5;
      } while (byte >= 0x20);
      lat += (result & 1) ? ~(result >> 1) : (result >> 1);

      shift = result = 0;
      do {
        byte = encoded.charCodeAt(idx++) - 63;
        result |= (byte & 0x1f) << shift;
        shift += 5;
      } while (byte >= 0x20);
      lng += (result & 1) ? ~(result >> 1) : (result >> 1);

      coords.push([lat / factor, lng / factor]);
    }
    return coords;
  },

  /**
   * Route waypoints via Valhalla motorcycle profile
   * @private
   */
  async _routeValhalla(sLat, sLng, wps) {
    const allPts = [{ lat: sLat, lng: sLng }, ...wps, { lat: sLat, lng: sLng }];
    const body = {
      locations: allPts.map((p, i) => ({
        lon: p.lng,
        lat: p.lat,
        type: (i === 0 || i === allPts.length - 1) ? 'break' : 'through'
      })),
      costing: 'motorcycle',
      costing_options: {
        motorcycle: {
          use_highways: State.get('avoidMotorways') ? 0.0 : 0.5,
          use_trails: State.get('avoidMotorways') ? 0.5 : 0.2,
          use_ferry: 0.15,
          service_penalty: 0
        }
      },
      directions_options: { units: 'kilometers', alternates: 2 }
    };

    const response = await fetch('https://valhalla1.openstreetmap.de/route', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(CONSTANTS.ROUTING_TIMEOUT_MS)
    });

    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const data = await response.json();

    if (!data.trip?.legs?.length) throw new Error('No trip data');

    const trips = data.alternates?.length ? [data.trip, ...data.alternates] : [data.trip];
    let bestGeom = null, bestDist = 0, bestScore = Infinity;

    for (const trip of trips) {
      if (!trip?.legs?.length) continue;
      const geom = [];
      trip.legs.forEach((leg, i) => {
        const pts = this.decodePolyline(leg.shape);
        geom.push(...(i === 0 ? pts : pts.slice(1)));
      });
      const repPaths = this._findRepeatedPaths(geom);
      let repScore = 0;
      for (const p of repPaths) {
        const weight = p.nearStart ? 3 : 1;
        const stemWeight = p._isStem ? 2.5 : 1;
        repScore += p.distanceKm * weight * stemWeight;
      }
      const score = repScore * 100 + trip.summary.length;
      if (score < bestScore) {
        bestScore = score;
        bestGeom = geom;
        bestDist = trip.summary.length;
      }
    }

    return { wps, geometry: bestGeom, distance: bestDist };
  },

  /**
   * Route waypoints via OSRM (fallback)
   * @private
   */
  async _routeOSRM(sLat, sLng, wps) {
    if (!wps.length) return null;

    const all = [{ lat: sLat, lng: sLng }, ...wps];
    const coords = all.map(p => `${p.lng.toFixed(6)},${p.lat.toFixed(6)}`).join(';');
    const exclude = State.get('avoidMotorways') ? '&exclude=motorway' : '';

    const response = await fetch(
      `https://router.project-osrm.org/trip/v1/driving/${coords}?overview=full&geometries=geojson&source=first&destination=last&roundtrip=true${exclude}`,
      { signal: AbortSignal.timeout(CONSTANTS.ROUTING_TIMEOUT_MS) }
    );

    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const data = await response.json();

    if (data.code !== 'Ok' || !data.trips?.length) return null;

    const trip = data.trips[0];
    const snappedWps = data.waypoints
      .filter(w => w.waypoint_index !== 0)
      .map(w => ({ lat: w.location[1], lng: w.location[0] }));

    return {
      wps: snappedWps,
      geometry: trip.geometry.coordinates.map(c => [c[1], c[0]]),
      distance: trip.distance / 1000
    };
  },

  /**
   * Route waypoints with automatic fallback
   * @param {number} sLat - Start latitude
   * @param {number} sLng - Start longitude
   * @param {Array} wps - Waypoints
   * @returns {Promise<Object|null>} Route result
   */
  async getRoute(sLat, sLng, wps) {
    if (_valhallaAvailable) {
      try {
        return await this._routeValhalla(sLat, sLng, wps);
      } catch (e) {
        console.warn('Valhalla failed, falling back to OSRM:', e);
        _valhallaAvailable = false;
        // Reset after 5 minutes
        setTimeout(() => { _valhallaAvailable = true; }, 5 * 60 * 1000);
      }
    }
    return this._routeOSRM(sLat, sLng, wps);
  },

  /**
   * Snap a point to the nearest road
   * @param {number} lat - Latitude
   * @param {number} lng - Longitude
   * @returns {Promise<{lat: number, lng: number}>} Snapped coordinates
   */
  async snapToRoad(lat, lng) {
    try {
      const response = await fetch(
        `https://router.project-osrm.org/nearest/v1/driving/${lng.toFixed(6)},${lat.toFixed(6)}?number=3`,
        { signal: AbortSignal.timeout(CONSTANTS.SNAP_TIMEOUT_MS) }
      );

      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const data = await response.json();

      if (data.code === 'Ok' && data.waypoints?.length) {
        const named = data.waypoints.find(w => w.name?.trim()) || data.waypoints[0];
        return { lat: named.location[1], lng: named.location[0] };
      }
    } catch (e) {
      console.warn('Snap to road failed:', e);
    }
    return { lat, lng };
  },

  /**
   * Snap a point to all nearby roads (returns all OSRM nearest candidates)
   * @param {number} lat - Latitude
   * @param {number} lng - Longitude
   * @returns {Promise<Array<{lat: number, lng: number}>>} Array of snapped candidates
   */
  async snapToRoadAlternates(lat, lng) {
    try {
      const response = await fetch(
        `https://router.project-osrm.org/nearest/v1/driving/${lng.toFixed(6)},${lat.toFixed(6)}?number=3`,
        { signal: AbortSignal.timeout(CONSTANTS.SNAP_TIMEOUT_MS) }
      );

      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const data = await response.json();

      if (data.code === 'Ok' && data.waypoints?.length) {
        return data.waypoints.map(w => ({ lat: w.location[1], lng: w.location[0] }));
      }
    } catch (e) {
      console.warn('Snap alternates failed:', e);
    }
    return [{ lat, lng }];
  },

  /**
   * Find repeated paths in geometry (exposed for optimizer)
   * @private
   */
  _findRepeatedPaths(geometry) {
    const G = CONSTANTS.GRID_SIZE;
    const seen = new Map();
    const hits = [];
    const totalLen = geometry.length;

    for (let i = 0; i < geometry.length - 1; i++) {
      const [la, lna] = geometry[i];
      const [lb, lnb] = geometry[i + 1];
      if (Math.abs(lb - la) < CONSTANTS.MIN_SEGMENT_LENGTH && Math.abs(lnb - lna) < CONSTANTS.MIN_SEGMENT_LENGTH) continue;

      const ga = `${Math.round(la / G)},${Math.round(lna / G)}`;
      const gb = `${Math.round(lb / G)},${Math.round(lnb / G)}`;
      const keyFwd = `${ga}>${gb}`;
      const keyRev = `${gb}>${ga}`;

      if (seen.has(keyRev)) {
        const prevSeg = seen.get(keyRev);
        const bearing1 = this._bearing({ lat: prevSeg.midLat, lng: prevSeg.midLng }, 
                                        { lat: prevSeg.midLat + (prevSeg.dLat || 0), lng: prevSeg.midLng + (prevSeg.dLng || 0) });
        const bearing2 = this._bearing({ lat: (la + lb) / 2, lng: (lna + lnb) / 2 }, 
                                        { lat: lb, lng: lnb });
        const bearingDiff = Math.abs(bearing1 - bearing2);
        const normalizedDiff = Math.min(bearingDiff, 360 - bearingDiff);
        
        if (normalizedDiff > (180 - CONSTANTS.STEM_BEARING_TOLERANCE)) {
          hits.push({ i, midLat: (la + lb) / 2, midLng: (lna + lnb) / 2, dLat: lb - la, dLng: lnb - lna, _isStem: true });
        } else {
          hits.push({ i, midLat: (la + lb) / 2, midLng: (lna + lnb) / 2, dLat: lb - la, dLng: lnb - lna });
        }
      } else if (seen.has(keyFwd)) {
        hits.push({ i, midLat: (la + lb) / 2, midLng: (lna + lnb) / 2, dLat: lb - la, dLng: lnb - lna, _sameDir: true });
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
        distKm += this._haversine({ lat: la, lng: lna }, { lat: lb, lng: lnb });
      }
      const mid = grp[Math.floor(grp.length / 2)];
      const frac = mid.i / totalLen;
      const nearStart = frac < CONSTANTS.NEAR_START_THRESHOLD || frac > CONSTANTS.NEAR_END_THRESHOLD;
      const isStem = grp.some(h => h._isStem);

      return {
        segs: grp.map(h => ({ midLat: h.midLat, midLng: h.midLng, dLat: h.dLat, dLng: h.dLng })),
        midLat: mid.midLat,
        midLng: mid.midLng,
        dLat: mid.dLat,
        dLng: mid.dLng,
        distanceKm: distKm,
        count: grp.length,
        nearStart,
        _isStem: isStem
      };
    }).sort((a, b) => b.distanceKm - a.distanceKm);
  },

  _bearing(a, b) {
    const dLng = (b.lng - a.lng) * Math.PI / 180;
    const lat1 = a.lat * Math.PI / 180;
    const lat2 = b.lat * Math.PI / 180;
    const y = Math.sin(dLng) * Math.cos(lat2);
    const x = Math.cos(lat1) * Math.sin(lat2) - Math.sin(lat1) * Math.cos(lat2) * Math.cos(dLng);
    return (Math.atan2(y, x) * 180 / Math.PI + 360) % 360;
  },

  /**
   * Haversine distance between two points
   * @private
   */
  _haversine(a, b) {
    const R = 6371;
    const dLat = (b.lat - a.lat) * Math.PI / 180;
    const dLng = (b.lng - a.lng) * Math.PI / 180;
    const x = Math.sin(dLat / 2) ** 2 +
      Math.cos(a.lat * Math.PI / 180) * Math.cos(b.lat * Math.PI / 180) *
      Math.sin(dLng / 2) ** 2;
    return 2 * R * Math.atan2(Math.sqrt(x), Math.sqrt(1 - x));
  }
};
