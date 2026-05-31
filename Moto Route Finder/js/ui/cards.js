// Route card rendering and selection
import { DOM } from '../utils/dom.js';
import { State } from '../state.js';
import { MapLayers } from '../map/layers.js';

export const RouteCards = {
  _container: null,

  /**
   * Initialize route cards
   */
  init() {
    this._container = DOM.$('#routeCards');
  },

  /**
   * Render all route cards
   */
  render() {
    const routes = State.get('routes');
    const selectedIndex = State.get('sel');
    this._container.innerHTML = '';

    if (!routes.length) {
      this._container.innerHTML = '<div class="empty">Set your location and<br>hit Generate Routes</div>';
      return;
    }

    routes.forEach((rt, i) => {
      const card = DOM.createRouteCard(rt, i, i === selectedIndex, (idx) => this._selectRoute(idx));
      this._container.appendChild(card);
    });
  },

  /**
   * Select a route
   * @private
   */
  _selectRoute(index) {
    const oldIndex = State.get('sel');
    State.set('sel', index);

    const routes = State.get('routes');
    const loc = State.getLocation();

    // Update map layers incrementally
    MapLayers.selectRoute(routes, oldIndex, index);

    // Update card styles
    const cards = DOM.$$('.rcard', this._container);
    cards.forEach((card, i) => {
      const rt = routes[i];
      const isSel = i === index;
      card.classList.toggle('sel', isSel);
      card.style.borderLeftColor = rt.col;
      card.style.background = isSel ? `rgba(${rt.rgb},.07)` : 'var(--surface)';
      card.style.boxShadow = isSel ? `0 3px 14px rgba(${rt.rgb},.18)` : 'none';

      const actEl = card.querySelector('.ract');
      if (actEl) {
        actEl.style.color = isSel ? rt.col : 'transparent';
      }
    });
  },

  /**
   * Show/hide the shuffle button
   * @param {boolean} show
   */
  showShuffleButton(show) {
    DOM.$('#randBtn').style.display = show ? 'flex' : 'none';
  },

  /**
   * Show/hide the export section
   * @param {boolean} show
   */
  showExportSection(show) {
    DOM.$('#expDiv').style.display = show ? 'block' : 'none';
    DOM.$('#expSection').style.display = show ? 'flex' : 'none';
  }
};
