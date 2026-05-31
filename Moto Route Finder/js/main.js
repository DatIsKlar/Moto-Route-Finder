// Main entry point - initializes all modules
import { State } from './state.js';
import { CONSTANTS } from './utils/constants.js';
import { DOM } from './utils/dom.js';
import { MapManager } from './map/map.js';
import { MapLayers } from './map/layers.js';
import { WaypointEngine } from './engine/waypoints.js';
import { Geometry } from './engine/geometry.js';
import { SearchUI } from './ui/search.js';
import { Controls } from './ui/controls.js';
import { WaypointsUI } from './ui/waypoints.js';
import { RouteCards } from './ui/cards.js';
import { Toast } from './ui/toast.js';
import { GMapsExport } from './export/gmaps.js';
import { KurvigerExport } from './export/kurviger.js';
import { ClipboardExport } from './export/clipboard.js';
import { GPXExport } from './export/gpx.js';

// Initialize the application
function init() {
  // Initialize map
  MapManager.init('map');

  // Set up tile switching
  DOM.$('#tOSM').addEventListener('click', () => {
    MapManager.setTile('osm');
    DOM.$('#tOSM').classList.add('on');
    DOM.$('#tSAT').classList.remove('on');
  });
  DOM.$('#tSAT').addEventListener('click', () => {
    MapManager.setTile('sat');
    DOM.$('#tSAT').classList.add('on');
    DOM.$('#tOSM').classList.remove('on');
  });

  // Initialize UI modules
  SearchUI.init();
  Controls.init();
  WaypointsUI.init();
  RouteCards.init();

  // Set up route selection from map clicks
  document.addEventListener('route-select', (e) => {
    const index = e.detail.index;
    const oldIndex = State.get('sel');
    State.set('sel', index);

    const routes = State.get('routes');
    MapLayers.selectRoute(routes, oldIndex, index);
    RouteCards.render();
  });

  // Bind generate button
  DOM.$('#genBtn').addEventListener('click', generate);

  // Bind shuffle button
  DOM.$('#randBtn').addEventListener('click', generate);

  // Bind export buttons
  DOM.$('.exp-btn.gm').addEventListener('click', () => GMapsExport.open());
  DOM.$('.exp-btn.kv').addEventListener('click', () => KurvigerExport.open());
  DOM.$('.exp-btn.cp').addEventListener('click', () => ClipboardExport.copy());
  DOM.$('.exp-btn.gx').addEventListener('click', () => GPXExport.download());

  console.log('Moto Trip Planner initialized');
}

// Generate routes
async function generate() {
  if (!State.hasLocation()) {
    Toast.show('Please set your location first.', 'error');
    return;
  }

  // Guard against concurrent generation
  if (State.get('isGenerating')) {
    Toast.show('Route generation already in progress...', 'error');
    return;
  }

  State.set('isGenerating', true);
  const abortController = State.createAbortController();

  const btn = DOM.$('#genBtn');
  const originalContent = btn.innerHTML;
  btn.innerHTML = '<svg class="spin" width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-6.219-8.56"/></svg> Routing & optimising…';
  btn.disabled = true;

  try {
    const loc = State.getLocation();
    const dist = State.get('dur') * State.get('speed');
    const base = Math.random() * Math.PI * 2;

    // Generate raw waypoint sets
    const rawRoutes = Array.from({ length: State.get('numRoutes') }, (_, i) => {
      const off = (i / State.get('numRoutes')) * 2 * Math.PI;
      const wps = WaypointEngine.generate(loc.lat, loc.lng, dist, State.get('wp'), base + off + Math.random() * 0.25);
      // Append custom waypoints
      return [...wps, ...State.get('customWps').map(w => ({ lat: w.lat, lng: w.lng }))];
    });

    // Snap to roads and optimize in parallel
    const snapped = await Promise.all(rawRoutes.map(wps => WaypointEngine.snapToRoads(loc.lat, loc.lng, wps)));

    // Check if aborted during generation
    if (abortController.signal.aborted) return;

    // Build route objects
    State.set('routes', snapped.map((res, i) => ({
      wps: res.wps,
      geometry: res.geometry,
      estDist: res.distance,
      estTime: res.distance / State.get('speed'),
      name: Geometry.compassDirection(loc, res.wps),
      col: CONSTANTS.COLORS[i],
      rgb: CONSTANTS.RGBS[i]
    })));

    State.set('sel', 0);
    MapLayers.setRoutes(State.get('routes'), 0, loc);
    RouteCards.render();
    RouteCards.showShuffleButton(true);
    RouteCards.showExportSection(true);

    // Fit map to show all routes
    const allLats = State.get('routes').flatMap(r => r.wps.map(p => p.lat)).concat(loc.lat);
    const allLngs = State.get('routes').flatMap(r => r.wps.map(p => p.lng)).concat(loc.lng);
    MapManager.fitBounds([
      [Math.min(...allLats), Math.min(...allLngs)],
      [Math.max(...allLats), Math.max(...allLngs)]
    ]);
  } catch (error) {
    console.error('Route generation failed:', error);
    Toast.show('Route generation failed. Please try again.', 'error');
  } finally {
    btn.innerHTML = originalContent;
    btn.disabled = false;
    State.set('isGenerating', false);
  }
}

// Initialize when DOM is ready
document.addEventListener('DOMContentLoaded', init);
