// Map layer management for routes, markers, and waypoints
import { CONSTANTS } from '../utils/constants.js';
import { MapManager } from './map.js';

export const MapLayers = {
  _polys: [],
  _outlines: [],
  _waypointMarkers: [],
  _customMarkers: [],

  /**
   * Clear all map layers
   */
  clear() {
    const map = MapManager.instance;
    [...this._polys, ...this._outlines, ...this._waypointMarkers].forEach(l => map.removeLayer(l));
    this._polys = [];
    this._outlines = [];
    this._waypointMarkers = [];
  },

  /**
   * Clear only custom waypoint markers
   */
  clearCustomMarkers() {
    const map = MapManager.instance;
    this._customMarkers.forEach(m => map.removeLayer(m));
    this._customMarkers = [];
  },

  /**
   * Render all routes on the map
   * @param {Array} routes - Array of route objects
   * @param {number} selectedIndex - Currently selected route index
   * @param {{lat: number, lng: number}} center - Route center point
   */
  setRoutes(routes, selectedIndex, center) {
    const map = MapManager.instance;

    // Remove existing route layers
    [...this._polys, ...this._outlines, ...this._waypointMarkers].forEach(l => map.removeLayer(l));
    this._polys = [];
    this._outlines = [];
    this._waypointMarkers = [];

    // Draw white outlines first (below colour lines)
    routes.forEach((rt, i) => {
      const isSel = i === selectedIndex;
      const lls = rt.geometry || [[center.lat, center.lng], ...rt.wps.map(p => [p.lat, p.lng]), [center.lat, center.lng]];

      const ol = L.polyline(lls, {
        color: 'white',
        weight: isSel ? 8 : 5,
        opacity: isSel ? 0.55 : 0.22,
        interactive: false
      }).addTo(map);
      this._outlines.push(ol);
    });

    // Draw colored route lines
    routes.forEach((rt, i) => {
      const isSel = i === selectedIndex;
      const lls = rt.geometry || [[center.lat, center.lng], ...rt.wps.map(p => [p.lat, p.lng]), [center.lat, center.lng]];

      const poly = L.polyline(lls, {
        color: rt.col,
        weight: isSel ? 5 : 3,
        opacity: isSel ? 0.95 : 0.5,
        dashArray: isSel ? null : '10,8'
      }).addTo(map);

      poly.on('click', () => {
        // Dispatch custom event for route selection
        document.dispatchEvent(new CustomEvent('route-select', { detail: { index: i } }));
      });

      this._polys.push(poly);

      if (isSel) {
        this._addWaypointMarkers(rt);
      }
    });
  },

  /**
   * Update route selection without full rebuild
   * @param {Array} routes - Array of route objects
   * @param {number} oldIndex - Previously selected index
   * @param {number} newIndex - Newly selected index
   */
  selectRoute(routes, oldIndex, newIndex) {
    // Update outline styles
    if (this._outlines[oldIndex]) {
      this._outlines[oldIndex].setStyle({ weight: 5, opacity: 0.22 });
    }
    if (this._outlines[newIndex]) {
      this._outlines[newIndex].setStyle({ weight: 8, opacity: 0.55 });
    }

    // Update polyline styles
    if (this._polys[oldIndex]) {
      this._polys[oldIndex].setStyle({
        weight: 3,
        opacity: 0.5,
        dashArray: '10,8'
      });
    }
    if (this._polys[newIndex]) {
      this._polys[newIndex].setStyle({
        weight: 5,
        opacity: 0.95,
        dashArray: null
      });
    }

    // Update waypoint markers
    this._waypointMarkers.forEach(m => MapManager.instance.removeLayer(m));
    this._waypointMarkers = [];
    if (routes[newIndex]) {
      this._addWaypointMarkers(routes[newIndex]);
    }
  },

  /**
   * Add numbered waypoint markers for a route
   * @private
   * @param {Object} rt - Route object
   */
  _addWaypointMarkers(rt) {
    const map = MapManager.instance;
    rt.wps.forEach((wp, i) => {
      const m = L.marker([wp.lat, wp.lng], {
        icon: L.divIcon({
          className: '',
          html: `<div class="wp-dot" style="background:${rt.col}">${i + 1}</div>`,
          iconSize: [22, 22],
          iconAnchor: [11, 11]
        }),
        zIndexOffset: 500
      }).addTo(map);
      this._waypointMarkers.push(m);
    });
  },

  /**
   * Add the home/start marker
   * @param {number} lat - Latitude
   * @param {number} lng - Longitude
   * @returns {Object} Marker instance
   */
  addHomeMarker(lat, lng) {
    const map = MapManager.instance;

    // Remove existing home marker if present
    if (this._homeMarker) {
      map.removeLayer(this._homeMarker);
    }

    this._homeMarker = L.marker([lat, lng], {
      icon: L.divIcon({
        className: '',
        html: '<div class="home-wrap"><div class="home-pulse"></div><div class="home-core"></div></div>',
        iconSize: [22, 22],
        iconAnchor: [11, 11]
      }),
      zIndexOffset: 1000
    }).addTo(map);

    return this._homeMarker;
  },

  /**
   * Render custom waypoint markers
   * @param {Array} waypoints - Custom waypoints
   * @param {Function} onRemove - Callback when waypoint is removed
   */
  setCustomWaypoints(waypoints, onRemove) {
    const map = MapManager.instance;

    // Remove existing custom markers
    this._customMarkers.forEach(m => map.removeLayer(m));
    this._customMarkers = [];

    waypoints.forEach((wp, i) => {
      const m = L.marker([wp.lat, wp.lng], {
        icon: L.divIcon({
          className: '',
          html: '<div class="cwp-map-dot"></div>',
          iconSize: [14, 14],
          iconAnchor: [7, 7]
        }),
        zIndexOffset: 800,
        title: wp.name
      }).addTo(map);

      m.bindTooltip(
        `<b style="color:#7c3aed">📍 ${wp.name}</b><br><span style="color:#9ca3af;font-size:10px">Click × to remove</span>`,
        { direction: 'top', offset: [0, -6] }
      );

      m.on('click', (e) => {
        L.DomEvent.stopPropagation(e);
        onRemove(i);
      });

      this._customMarkers.push(m);
    });
  },

  /**
   * Get the home marker
   * @returns {Object|null}
   */
  get homeMarker() {
    return this._homeMarker || null;
  }
};
