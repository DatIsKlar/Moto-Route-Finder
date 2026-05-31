// Centralized state management with change notification
import { CONSTANTS } from './utils/constants.js';

class StateManager {
  constructor() {
    this._data = {
      loc: null,
      dur: 2,
      speed: 70,
      wp: 5,
      numRoutes: 3,
      kurvProfile: 'curvaturefastest',
      avoidMotorways: true,
      customWps: [],
      routes: [],
      sel: 0,
      isGenerating: false
    };
    this._listeners = new Map();
    this._abortController = null;
  }

  /**
   * Get a state value
   * @param {string} key - State key
   * @returns {*} Value
   */
  get(key) {
    return this._data[key];
  }

  /**
   * Set a state value and notify listeners
   * @param {string} key - State key
   * @param {*} value - New value
   */
  set(key, value) {
    const old = this._data[key];
    this._data[key] = value;
    this._notify(key, value, old);
  }

  /**
   * Subscribe to state changes
   * @param {string} key - State key to watch
   * @param {Function} fn - Callback (value, oldValue)
   * @returns {Function} Unsubscribe function
   */
  on(key, fn) {
    if (!this._listeners.has(key)) {
      this._listeners.set(key, new Set());
    }
    this._listeners.get(key).add(fn);
    return () => this._listeners.get(key)?.delete(fn);
  }

  /**
   * Notify listeners of a state change
   * @private
   */
  _notify(key, value, old) {
    this._listeners.get(key)?.forEach(fn => {
      try { fn(value, old); } catch (e) { console.error(`State listener error for "${key}":`, e); }
    });
  }

  /**
   * Get the current abort controller for route generation
   * @returns {AbortController}
   */
  getAbortController() {
    return this._abortController;
  }

  /**
   * Create a new abort controller (cancels any previous generation)
   * @returns {AbortController}
   */
  createAbortController() {
    if (this._abortController) {
      this._abortController.abort();
    }
    this._abortController = new AbortController();
    return this._abortController;
  }

  /**
   * Check if location is set
   * @returns {boolean}
   */
  hasLocation() {
    return this._data.loc !== null;
  }

  /**
   * Get location coordinates
   * @returns {{ lat: number, lng: number } | null}
   */
  getLocation() {
    return this._data.loc;
  }

  /**
   * Set location
   * @param {number} lat
   * @param {number} lng
   */
  setLocation(lat, lng) {
    this.set('loc', { lat, lng });
  }
}

// Export singleton instance
export const State = new StateManager();
