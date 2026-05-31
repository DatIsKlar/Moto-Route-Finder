// Custom waypoint UI management
import { DOM } from '../utils/dom.js';
import { Nominatim } from '../api/nominatim.js';
import { State } from '../state.js';
import { MapManager } from '../map/map.js';
import { MapLayers } from '../map/layers.js';

// Counter for unique waypoint IDs
let _waypointIdCounter = 0;

export const WaypointsUI = {
  _addModeBtn: null,
  _hintEl: null,
  _listEl: null,

  /**
   * Initialize custom waypoint UI
   */
  init() {
    this._addModeBtn = DOM.$('#addWpBtn');
    this._hintEl = DOM.$('#cwpHint');
    this._listEl = DOM.$('#cwpList');

    // Bind add mode toggle
    this._addModeBtn.addEventListener('click', () => this._toggleAddMode());

    // Set up map click handler for adding waypoints
    MapManager.onClick((lat, lng) => this._addWaypoint(lat, lng));
  },

  /**
   * Toggle add mode on/off
   * @private
   */
  _toggleAddMode() {
    const isAddMode = MapManager.isAddMode;
    MapManager.setAddMode(!isAddMode);

    this._addModeBtn.classList.toggle('active', !isAddMode);
    this._hintEl.style.display = !isAddMode ? 'block' : 'none';

    if (!isAddMode) {
      this._addModeBtn.innerHTML = `
        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><line x1="8" y1="12" x2="16" y2="12"/></svg>
        Done — click again to add more`;
    } else {
      this._addModeBtn.innerHTML = `
        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="16"/><line x1="8" y1="12" x2="16" y2="12"/></svg>
        Click map to drop a pin`;
    }
  },

  /**
   * Add a custom waypoint
   * @private
   */
  async _addWaypoint(lat, lng) {
    const waypoints = State.get('customWps');
    const id = ++_waypointIdCounter;
    const newWp = { id, lat, lng, name: `Pin ${waypoints.length + 1}` };

    // Add to state
    waypoints.push(newWp);
    State.set('customWps', waypoints);

    // Render immediately with default name
    this._renderList();
    MapLayers.setCustomWaypoints(waypoints, (i) => this._removeWaypoint(i));

    // Reverse geocode for a friendly label
    try {
      const result = await Nominatim.reverse(lat, lng);
      if (result?.display_name) {
        const name = Nominatim.formatName(result, 2);
        // Find the waypoint by ID to ensure we update the correct one
        const wp = waypoints.find(w => w.id === id);
        if (wp) {
          wp.name = name;
          State.set('customWps', waypoints);
          this._renderList();
          MapLayers.setCustomWaypoints(waypoints, (i) => this._removeWaypoint(i));
        }
      }
    } catch (e) {
      // Keep default name on error
    }
  },

  /**
   * Remove a custom waypoint
   * @private
   */
  _removeWaypoint(index) {
    const waypoints = State.get('customWps');
    waypoints.splice(index, 1);
    State.set('customWps', waypoints);
    this._renderList();
    MapLayers.setCustomWaypoints(waypoints, (i) => this._removeWaypoint(i));
  },

  /**
   * Clear all custom waypoints
   * @private
   */
  _clearAll() {
    State.set('customWps', []);
    this._renderList();
    MapLayers.setCustomWaypoints([], () => {});
  },

  /**
   * Render the waypoint list
   * @private
   */
  _renderList() {
    const waypoints = State.get('customWps');
    this._listEl.innerHTML = '';

    if (!waypoints.length) return;

    waypoints.forEach((wp, i) => {
      const item = DOM.createWaypointItem(wp, i, (idx) => this._removeWaypoint(idx));
      this._listEl.appendChild(item);
    });

    // Add "Clear all" button if more than one waypoint (FIXED: was dead code before)
    if (waypoints.length > 1) {
      const clearBtn = DOM.createElement('button', {
        className: 'cwp-clear',
        onClick: () => this._clearAll()
      }, 'Clear all pins');
      this._listEl.appendChild(clearBtn);
    }
  }
};
