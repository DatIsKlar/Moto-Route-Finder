// Nominatim API wrapper with rate limiting
import { CONSTANTS } from '../utils/constants.js';

export const Nominatim = {
  _lastRequestTime: 0,

  /**
   * Enforce rate limiting (1 request per second per Nominatim TOS)
   * @private
   */
  async _throttle() {
    const now = Date.now();
    const elapsed = now - this._lastRequestTime;
    if (elapsed < CONSTANTS.NOMINATIM_RATE_LIMIT_MS) {
      await new Promise(r => setTimeout(r, CONSTANTS.NOMINATIM_RATE_LIMIT_MS - elapsed));
    }
    this._lastRequestTime = Date.now();
  },

  /**
   * Search for locations by query string
   * @param {string} query - Search query
   * @param {number} [limit=4] - Max results
   * @returns {Promise<Array>} Array of location results
   */
  async search(query, limit = 4) {
    await this._throttle();
    try {
      const url = `https://nominatim.openstreetmap.org/search?format=json&q=${encodeURIComponent(query)}&limit=${limit}&addressdetails=1`;
      const response = await fetch(url, {
        headers: { 'Accept-Language': 'en' }
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return await response.json();
    } catch (error) {
      console.error('Nominatim search error:', error);
      return [];
    }
  },

  /**
   * Search for a single location (for direct search)
   * @param {string} query - Search query
   * @returns {Promise<Object|null>} First result or null
   */
  async searchOne(query) {
    const results = await this.search(query, 1);
    return results.length > 0 ? results[0] : null;
  },

  /**
   * Reverse geocode coordinates to address
   * @param {number} lat - Latitude
   * @param {number} lng - Longitude
   * @returns {Promise<Object|null>} Address result or null
   */
  async reverse(lat, lng) {
    await this._throttle();
    try {
      const url = `https://nominatim.openstreetmap.org/reverse?format=json&lat=${lat}&lon=${lng}`;
      const response = await fetch(url);
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return await response.json();
    } catch (error) {
      console.error('Nominatim reverse error:', error);
      return null;
    }
  },

  /**
   * Get a friendly display name from a result
   * @param {Object} result - Nominatim result
   * @param {number} [parts=3] - Number of comma-separated parts to include
   * @returns {string} Formatted name
   */
  formatName(result, parts = 3) {
    if (!result?.display_name) return '';
    return result.display_name.split(',').slice(0, parts).join(',').trim();
  }
};
