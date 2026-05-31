// Google Maps export
import { State } from '../state.js';
import { CONSTANTS } from '../utils/constants.js';
import { Geometry } from '../engine/geometry.js';

export const GMapsExport = {
  /**
   * Sample a polyline to reduce point count
   * @private
   */
  _samplePolyline(geometry, maxPts) {
    if (!geometry || geometry.length < 3) return [];
    const interior = geometry.slice(1, -1);
    if (interior.length <= maxPts) return interior.map(([lat, lng]) => ({ lat, lng }));

    const step = (interior.length - 1) / (maxPts - 1);
    const result = [];
    for (let i = 0; i < maxPts; i++) {
      const idx = Math.round(i * step);
      result.push({ lat: interior[idx][0], lng: interior[idx][1] });
    }
    return result;
  },

  /**
   * Open route in Google Maps
   */
  open() {
    const route = State.get('routes')[State.get('sel')];
    const loc = State.getLocation();
    if (!route || !loc) return;

    const origin = `${loc.lat},${loc.lng}`;
    const samples = this._samplePolyline(route.geometry, CONSTANTS.MAX_GOOGLE_MAPS_WAYPOINTS);
    const waypoints = samples.map(p => `${p.lat.toFixed(6)},${p.lng.toFixed(6)}`).join('|');

    window.open(
      `https://www.google.com/maps/dir/?api=1&origin=${origin}&destination=${origin}&waypoints=${encodeURIComponent(waypoints)}&travelmode=driving`,
      '_blank'
    );
  }
};
