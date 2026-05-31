// Address search UI and suggestion handling
import { CONSTANTS } from '../utils/constants.js';
import { DOM } from '../utils/dom.js';
import { Nominatim } from '../api/nominatim.js';
import { State } from '../state.js';
import { MapManager } from '../map/map.js';
import { MapLayers } from '../map/layers.js';

export const SearchUI = {
  _debounceTimer: null,
  _inputEl: null,
  _suggestionsEl: null,
  _statusEl: null,

  /**
   * Initialize search UI
   */
  init() {
    this._inputEl = DOM.$('#addrInput');
    this._suggestionsEl = DOM.$('#suggestions');
    this._statusEl = DOM.$('#addrStatus');

    // Bind event listeners
    this._inputEl.addEventListener('input', (e) => this._onInput(e.target.value));
    this._inputEl.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') this._doSearch();
    });

    // Close suggestions on outside click
    document.addEventListener('click', (e) => {
      if (!e.target.closest('.addr-wrap')) {
        this._hideSuggestions();
      }
    });

    // Bind search button
    DOM.$('.search-btn').addEventListener('click', () => this._doSearch());

    // Bind GPS button
    DOM.$('.gps-btn').addEventListener('click', () => this._detectGPS());
  },

  /**
   * Handle input with debounce
   * @private
   */
  _onInput(value) {
    clearTimeout(this._debounceTimer);
    if (value.length < 3) {
      this._hideSuggestions();
      return;
    }
    this._debounceTimer = setTimeout(() => this._fetchSuggestions(value), CONSTANTS.DEBOUNCE_DELAY_MS);
  },

  /**
   * Fetch suggestions from Nominatim
   * @private
   */
  async _fetchSuggestions(query) {
    try {
      const results = await Nominatim.search(query);
      this._showSuggestions(results);
    } catch (e) {
      this._hideSuggestions();
    }
  },

  /**
   * Show suggestion dropdown
   * @private
   */
  _showSuggestions(items) {
    if (!items || !items.length) {
      this._hideSuggestions();
      return;
    }

    this._suggestionsEl.innerHTML = '';
    items.forEach(item => {
      const el = DOM.createSuggestionItem(item);
      el.addEventListener('click', () => {
        this._pickSuggestion(parseFloat(item.lat), parseFloat(item.lon), item.display_name);
      });
      this._suggestionsEl.appendChild(el);
    });
    this._suggestionsEl.style.display = 'block';
  },

  /**
   * Hide suggestion dropdown
   * @private
   */
  _hideSuggestions() {
    this._suggestionsEl.style.display = 'none';
  },

  /**
   * Handle suggestion selection
   * @private
   */
  _pickSuggestion(lat, lng, name) {
    State.setLocation(lat, lng);
    this._inputEl.value = name.split(',').slice(0, 3).join(',');
    this._hideSuggestions();
    this._onLocationSet(name);
  },

  /**
   * Perform direct search
   * @private
   */
  async _doSearch() {
    const query = this._inputEl.value.trim();
    if (!query) return;

    this._hideSuggestions();
    this._setStatus('searching', '🔍 Searching…');

    try {
      const result = await Nominatim.searchOne(query);
      if (!result) {
        this._setStatus('err', '❌ Address not found. Try a different search.');
        return;
      }
      State.setLocation(parseFloat(result.lat), parseFloat(result.lon));
      this._onLocationSet(result.display_name);
    } catch (e) {
      this._setStatus('err', '❌ Network error. Check your connection.');
    }
  },

  /**
   * Detect GPS location
   * @private
   */
  _detectGPS() {
    if (!navigator.geolocation) {
      this._setStatus('err', '❌ GPS not supported by this browser.');
      return;
    }

    this._setStatus('searching', '📡 Getting GPS location…');

    navigator.geolocation.getCurrentPosition(
      (pos) => {
        State.setLocation(pos.coords.latitude, pos.coords.longitude);
        this._onLocationSet('GPS location');
      },
      (err) => {
        this._setStatus('err', '❌ Could not get GPS. Try entering an address above.');
      },
      { timeout: 10000 }
    );
  },

  /**
   * Handle location being set
   * @private
   */
  _onLocationSet(name) {
    const loc = State.getLocation();
    if (!loc) return;

    const short = name.split(',').slice(0, 3).join(',');
    this._setStatus('ok', '📍 ' + short);
    DOM.$('#hdrBadge').textContent = `${loc.lat.toFixed(4)}, ${loc.lng.toFixed(4)}`;
    MapManager.setView(loc.lat, loc.lng, CONSTANTS.LOCATION_ZOOM);
    MapLayers.addHomeMarker(loc.lat, loc.lng);
  },

  /**
   * Set status message
   * @private
   */
  _setStatus(cls, msg) {
    this._statusEl.className = 'addr-status ' + cls;
    this._statusEl.textContent = msg;
  }
};
