// GPX file download
import { State } from '../state.js';
import { Toast } from '../ui/toast.js';

export const GPXExport = {
  /**
   * Download route as GPX file
   */
  download() {
    const route = State.get('routes')[State.get('sel')];
    const loc = State.getLocation();
    if (!route || !loc) return;

    if (!route.geometry) {
      Toast.show('No road geometry available', 'error');
      return;
    }

    const name = `Moto Loop — ${route.name}`;
    const lines = [
      '<?xml version="1.0" encoding="UTF-8"?>',
      '<gpx version="1.1" creator="MotoTripPlanner" xmlns="http://www.topografix.com/GPX/1/1">',
      `  <trk><name>${name}</name><trkseg>`
    ];

    route.geometry.forEach(([lat, lng]) => {
      lines.push(`    <trkpt lat="${lat.toFixed(7)}" lon="${lng.toFixed(7)}"></trkpt>`);
    });

    lines.push('  </trkseg></trk></gpx>');

    const blob = new Blob([lines.join('\n')], { type: 'application/gpx+xml' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `moto_${route.name.replace(/\s/g, '_')}.gpx`;
    a.click();

    // FIX: Use setTimeout to ensure download starts before revoking URL
    setTimeout(() => URL.revokeObjectURL(url), 1000);

    Toast.show(`GPX downloaded — ${route.geometry.length} track points`);
  }
};
