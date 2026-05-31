// Sidebar controls for speed, duration, waypoints, etc.
import { DOM } from '../utils/dom.js';
import { Geometry } from '../engine/geometry.js';
import { State } from '../state.js';

export const Controls = {
  _els: {},

  /**
   * Initialize all controls
   */
  init() {
    // Cache DOM references
    this._els = {
      durSlider: DOM.$('#durSlider'),
      durVal: DOM.$('#durVal'),
      distHint: DOM.$('#distHint'),
      wpSlider: DOM.$('#wpSlider'),
      wpVal: DOM.$('#wpVal'),
      speedBtns: DOM.$$('#speedBtns .seg-btn'),
      routeBtns: DOM.$$('#routeBtns .seg-btn'),
      roadTypeBtns: DOM.$$('#roadTypeBtns .seg-btn'),
      kurvBtns: DOM.$$('#kurvBtns .seg-btn')
    };

    // Bind event listeners
    this._bindDurationControl();
    this._bindSpeedControl();
    this._bindWaypointControl();
    this._bindRouteCountControl();
    this._bindRoadTypeControl();
    this._bindKurvigerProfileControl();

    // Initialize display
    this._updateDurationDisplay();
  },

  /**
   * Bind duration slider
   * @private
   */
  _bindDurationControl() {
    this._els.durSlider.addEventListener('input', (e) => {
      State.set('dur', parseFloat(e.target.value));
      this._updateDurationDisplay();
    });
  },

  /**
   * Bind speed buttons
   * @private
   */
  _bindSpeedControl() {
    this._els.speedBtns.forEach(btn => {
      btn.addEventListener('click', () => {
        const speed = parseInt(btn.dataset.v);
        State.set('speed', speed);
        this._els.speedBtns.forEach(b => b.classList.toggle('on', parseInt(b.dataset.v) === speed));
        this._updateDurationDisplay();
      });
    });
  },

  /**
   * Bind waypoint count slider
   * @private
   */
  _bindWaypointControl() {
    this._els.wpSlider.addEventListener('input', (e) => {
      State.set('wp', parseInt(e.target.value));
      this._els.wpVal.textContent = e.target.value;
    });
  },

  /**
   * Bind route count buttons
   * @private
   */
  _bindRouteCountControl() {
    this._els.routeBtns.forEach(btn => {
      btn.addEventListener('click', () => {
        const count = parseInt(btn.dataset.v);
        State.set('numRoutes', count);
        this._els.routeBtns.forEach(b => b.classList.toggle('on', parseInt(b.dataset.v) === count));
      });
    });
  },

  /**
   * Bind road type buttons
   * @private
   */
  _bindRoadTypeControl() {
    this._els.roadTypeBtns.forEach(btn => {
      btn.addEventListener('click', () => {
        const avoid = btn.dataset.v === 'avoid';
        State.set('avoidMotorways', avoid);
        this._els.roadTypeBtns.forEach(b => {
          b.classList.toggle('on', b.dataset.v === (avoid ? 'avoid' : 'all'));
        });
      });
    });
  },

  /**
   * Bind Kurviger profile buttons
   * @private
   */
  _bindKurvigerProfileControl() {
    this._els.kurvBtns.forEach(btn => {
      btn.addEventListener('click', () => {
        const profile = btn.dataset.v;
        State.set('kurvProfile', profile);
        this._els.kurvBtns.forEach(b => b.classList.toggle('on', b.dataset.v === profile));
      });
    });
  },

  /**
   * Update duration display and distance hint
   * @private
   */
  _updateDurationDisplay() {
    const dur = State.get('dur');
    const speed = State.get('speed');
    const h = Math.floor(dur);
    const m = Math.round((dur - h) * 60);

    if (h > 0) {
      this._els.durVal.textContent = m > 0 ? `${h}h ${m}m` : `${h}h`;
    } else {
      this._els.durVal.textContent = `${m}m`;
    }

    this._els.distHint.textContent = `≈ ${Math.round(dur * speed)} km at ${speed} km/h`;
  }
};
