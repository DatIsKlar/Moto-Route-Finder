// Copy coordinates to clipboard
import { State } from '../state.js';
import { CONSTANTS } from '../utils/constants.js';
import { Toast } from '../ui/toast.js';

export const ClipboardExport = {
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
   * Copy route coordinates to clipboard
   */
  async copy() {
    const route = State.get('routes')[State.get('sel')];
    const loc = State.getLocation();
    if (!route || !loc) return;

    const samples = this._samplePolyline(route.geometry, CONSTANTS.MAX_KURVIGER_WAYPOINTS);
    const lines = [
      `Start: ${loc.lat.toFixed(6)}, ${loc.lng.toFixed(6)}`,
      ...samples.map((p, i) => `WP${i + 1}:  ${p.lat.toFixed(6)}, ${p.lng.toFixed(6)}`),
      `End:   ${loc.lat.toFixed(6)}, ${loc.lng.toFixed(6)}`
    ];

    try {
      await navigator.clipboard.writeText(lines.join('\n'));
      Toast.show(`Copied ${samples.length + 2} waypoints!`);
    } catch (e) {
      Toast.show('Failed to copy to clipboard', 'error');
    }
  }
};
