// Leaflet map initialization and tile switching
import { CONSTANTS } from '../utils/constants.js';

export const MapManager = {
  _map: null,
  _osmLayer: null,
  _satLayer: null,
  _currentTile: 'osm',
  _addMode: false,
  _mapClickHandler: null,

  /**
   * Initialize the Leaflet map
   * @param {string} elementId - Map container element ID
   * @returns {Object} Leaflet map instance
   */
  init(elementId) {
    this._map = L.map(elementId, { zoomControl: true }).setView([51, 10], CONSTANTS.DEFAULT_ZOOM);

    this._osmLayer = L.tileLayer(
      'https://server.arcgisonline.com/ArcGIS/rest/services/World_Street_Map/MapServer/tile/{z}/{y}/{x}',
      {
        attribution: 'Tiles © Esri — Esri, DeLorme, NAVTEQ, TomTom, Intermap, iPC, USGS, FAO, NPS, NRCAN, GeoBase, IGN, Kadaster NL, Ordnance Survey, Esri Japan, METI, Esri China (Hong Kong), and the GIS User Community',
        maxZoom: 19
      }
    );

    this._satLayer = L.tileLayer(
      'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
      {
        attribution: 'Tiles © Esri — Source: Esri, USGS, Aerogrid, IGN, the GIS User Community',
        maxZoom: 19
      }
    );

    this._osmLayer.addTo(this._map);
    return this._map;
  },

  /**
   * Get the map instance
   * @returns {Object} Leaflet map
   */
  get instance() {
    return this._map;
  },

  /**
   * Set the map view
   * @param {number} lat - Latitude
   * @param {number} lng - Longitude
   * @param {number} [zoom] - Zoom level
   */
  setView(lat, lng, zoom) {
    if (zoom) {
      this._map.setView([lat, lng], zoom);
    } else {
      this._map.setView([lat, lng]);
    }
  },

  /**
   * Fit map to bounds
   * @param {Array} bounds - [[minLat, minLng], [maxLat, maxLng]]
   * @param {number} [padding] - Padding in pixels
   */
  fitBounds(bounds, padding = CONSTANTS.FIT_BOUNDS_PADDING) {
    this._map.fitBounds(bounds, { padding: [padding, padding] });
  },

  /**
   * Switch map tile layer
   * @param {string} type - 'osm' or 'sat'
   */
  setTile(type) {
    this._currentTile = type;
    if (type === 'osm') {
      this._map.removeLayer(this._satLayer);
      this._osmLayer.addTo(this._map);
    } else {
      this._map.removeLayer(this._osmLayer);
      this._satLayer.addTo(this._map);
    }
  },

  /**
   * Get current tile type
   * @returns {string} 'osm' or 'sat'
   */
  get currentTile() {
    return this._currentTile;
  },

  /**
   * Toggle add mode for custom waypoints
   * @param {boolean} enabled - Enable/disable add mode
   */
  setAddMode(enabled) {
    this._addMode = enabled;
    const mapEl = document.getElementById('map');
    mapEl.classList.toggle('add-mode', enabled);
  },

  /**
   * Check if add mode is active
   * @returns {boolean}
   */
  get isAddMode() {
    return this._addMode;
  },

  /**
   * Set up map click handler
   * @param {Function} callback - Called with (lat, lng) on map click
   */
  onClick(callback) {
    if (this._mapClickHandler) {
      this._map.off('click', this._mapClickHandler);
    }
    this._mapClickHandler = (e) => {
      if (this._addMode) {
        callback(e.latlng.lat, e.latlng.lng);
      }
    };
    this._map.on('click', this._mapClickHandler);
  }
};
