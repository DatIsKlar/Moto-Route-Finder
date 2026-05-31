// Toast notification system
import { CONSTANTS } from '../utils/constants.js';

export const Toast = {
  _current: null,

  /**
   * Show a toast notification
   * @param {string} message - Message to display
   * @param {string} [type='success'] - Toast type ('success' or 'error')
   */
  show(message, type = 'success') {
    // Remove previous toast if exists
    if (this._current) {
      this._current.remove();
      this._current = null;
    }

    const toast = document.createElement('div');
    toast.className = 'toast';
    toast.textContent = message;

    if (type === 'error') {
      toast.style.borderColor = 'var(--danger)';
      toast.style.color = 'var(--danger)';
    }

    document.body.appendChild(toast);
    this._current = toast;

    setTimeout(() => {
      if (toast.parentNode) {
        toast.remove();
      }
      if (this._current === toast) {
        this._current = null;
      }
    }, CONSTANTS.TOAST_DURATION_MS);
  }
};
