// DOM utility functions for safe element creation and manipulation
export const DOM = {
  /**
   * Escape HTML special characters to prevent XSS
   * @param {string} str - Raw string
   * @returns {string} Escaped string safe for innerHTML
   */
  escapeHtml(str) {
    if (!str) return '';
    const div = document.createElement('div');
    div.textContent = str;
    return div.innerHTML;
  },

  /**
   * Shorthand for querySelector
   * @param {string} selector - CSS selector
   * @param {Element} [parent=document] - Parent element
   * @returns {Element|null}
   */
  $(selector, parent = document) {
    return parent.querySelector(selector);
  },

  /**
   * Shorthand for querySelectorAll
   * @param {string} selector - CSS selector
   * @param {Element} [parent=document] - Parent element
   * @returns {NodeListOf<Element>}
   */
  $$(selector, parent = document) {
    return parent.querySelectorAll(selector);
  },

  /**
   * Create an element with attributes and children
   * @param {string} tag - Tag name
   * @param {Object} [attrs={}] - Attributes
   * @param {Array|string} [children=[]] - Children (elements or text)
   * @returns {Element}
   */
  createElement(tag, attrs = {}, children = []) {
    const el = document.createElement(tag);

    for (const [key, value] of Object.entries(attrs)) {
      if (key === 'className') {
        el.className = value;
      } else if (key === 'style' && typeof value === 'object') {
        Object.assign(el.style, value);
      } else if (key.startsWith('on') && typeof value === 'function') {
        el.addEventListener(key.slice(2).toLowerCase(), value);
      } else if (key === 'dataset') {
        Object.assign(el.dataset, value);
      } else {
        el.setAttribute(key, value);
      }
    }

    for (const child of Array.isArray(children) ? children : [children]) {
      if (typeof child === 'string') {
        el.appendChild(document.createTextNode(child));
      } else if (child instanceof Element) {
        el.appendChild(child);
      }
    }

    return el;
  },

  /**
   * Create a suggestion item element safely
   * @param {Object} item - Nominatim result
   * @returns {Element}
   */
  createSuggestionItem(item) {
    const parts = item.display_name.split(',');
    const name = parts.slice(0, 2).join(',').trim();
    const rest = parts.slice(2, 4).join(',').trim();

    const strong = this.createElement('strong', {}, name);
    const span = this.createElement('span', {}, rest);

    return this.createElement('div', {
      className: 'sug-item',
      dataset: { lat: item.lat, lon: item.lon, name: item.display_name }
    }, [strong, span]);
  },

  /**
   * Create a waypoint list item element safely
   * @param {Object} wp - Waypoint data
   * @param {number} index - Index in the list
   * @param {Function} onRemove - Remove callback
   * @returns {Element}
   */
  createWaypointItem(wp, index, onRemove) {
    const dot = this.createElement('span', {
      innerHTML: '<svg width="10" height="10" viewBox="0 0 24 24" fill="#7c3aed" style="flex-shrink:0"><circle cx="12" cy="12" r="10"/></svg>'
    });

    const text = this.createElement('span', {
      className: 'cwp-item-text',
      title: wp.name
    }, wp.name);

    const removeBtn = this.createElement('button', {
      className: 'cwp-item-rm',
      title: 'Remove',
      onClick: () => onRemove(index),
      innerHTML: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>'
    });

    return this.createElement('div', { className: 'cwp-item' }, [dot, text, removeBtn]);
  },

  /**
   * Create a route card element safely
   * @param {Object} route - Route data
   * @param {number} index - Route index
   * @param {boolean} isSelected - Is this route selected
   * @param {Function} onSelect - Selection callback
   * @returns {Element}
   */
  createRouteCard(route, index, isSelected, onSelect) {
    const dot = this.createElement('div', {
      className: 'rdot',
      style: { background: route.col }
    });

    const name = this.createElement('span', {
      className: 'rname'
    }, route.name);

    const active = this.createElement('span', {
      className: 'ract',
      style: { color: isSelected ? route.col : 'transparent' }
    }, '● ACTIVE');

    const top = this.createElement('div', { className: 'rcard-top' }, [
      this.createElement('div', { style: { display: 'flex', alignItems: 'center' } }, [dot, name]),
      active
    ]);

    const distStat = this.createElement('div', { className: 'rstat' }, [
      document.createTextNode('~'),
      this.createElement('b', {}, Math.round(route.estDist).toString()),
      document.createTextNode(' km')
    ]);

    const timeStat = this.createElement('div', { className: 'rstat' }, [
      this.createElement('b', {}, this.formatTime(route.estTime))
    ]);

    const stats = this.createElement('div', { className: 'rstats' }, [distStat, timeStat]);

    const card = this.createElement('div', {
      className: `rcard${isSelected ? ' sel' : ''}`,
      style: {
        animation: `cardIn .25s ease ${index * 70}ms both`,
        borderLeftColor: route.col,
        background: isSelected ? `rgba(${route.rgb},.07)` : 'var(--surface)',
        boxShadow: isSelected ? `0 3px 14px rgba(${route.rgb},.18)` : 'none'
      },
      onClick: () => onSelect(index)
    }, [top, stats]);

    return card;
  },

  /**
   * Format time duration
   * @param {number} hours - Duration in hours
   * @returns {string} Formatted string
   */
  formatTime(hours) {
    const hh = Math.floor(hours);
    const mm = Math.round((hours - hh) * 60);
    if (hh === 0) return `${mm}min`;
    if (mm === 0) return `${hh}h`;
    return `${hh}h ${mm}m`;
  }
};
