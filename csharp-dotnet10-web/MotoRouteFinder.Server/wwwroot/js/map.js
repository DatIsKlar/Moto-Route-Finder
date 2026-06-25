const MotoMap = {
    map: null,
    pointLayer: null,
    routeLayer: null,
    repetitionLayer: null,
    points: [],

    init() {
        this.map = L.map('map', {
            center: [50.0, 10.0],
            zoom: 6,
            zoomControl: true
        });

        L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            attribution: '&copy; OpenStreetMap contributors'
        }).addTo(this.map);

        this.pointLayer = L.layerGroup().addTo(this.map);
        this.routeLayer = L.layerGroup().addTo(this.map);
        this.repetitionLayer = L.layerGroup().addTo(this.map);

        this.map.on('click', (e) => {
            if (typeof MotoApp !== 'undefined') {
                MotoApp.addPoint(e.latlng.lat, e.latlng.lng);
            }
        });
    },

    clearPoints() {
        this.points = [];
        this.pointLayer.clearLayers();
    },

    clearRoute() {
        this.routeLayer.clearLayers();
        this.repetitionLayer.clearLayers();
    },

    clearAll() {
        this.clearPoints();
        this.clearRoute();
    },

    addMarker(lat, lon, type, label) {
        const isStart = type === 'start';
        const color = isStart ? '#4ade80' : '#fbbf24';
        const radius = isStart ? 8 : 6;

        const marker = L.circleMarker([lat, lon], {
            radius: radius,
            fillColor: color,
            color: isStart ? '#166534' : '#92400e',
            weight: 2,
            fillOpacity: 0.9
        });

        marker.bindTooltip(label, { permanent: false, direction: 'top', offset: [0, -8] });
        this.pointLayer.addLayer(marker);
    },

    updateMarkers(points) {
        this.clearPoints();
        this.points = points;
        points.forEach(p => {
            this.addMarker(p.coordinate.lat, p.coordinate.lon, p.type, p.label);
        });
    },

    drawRoute(geometry, waypoints) {
        this.routeLayer.clearLayers();
        if (!geometry || geometry.length < 2) return;

        const latLngs = geometry.map(c => [c.lat, c.lon]);

        // Find loop midpoint (closest point to start between 25%-75%)
        const start = geometry[0];
        let bestIdx = Math.floor(geometry.length / 2);
        let bestDist = Infinity;
        const from = Math.floor(geometry.length * 0.25);
        const to = Math.floor(geometry.length * 0.75);
        for (let i = from; i <= to; i++) {
            const d = Math.hypot(geometry[i].lat - start.lat, geometry[i].lon - start.lon);
            if (d < bestDist) {
                bestDist = d;
                bestIdx = i;
            }
        }

        // Outgoing segment (blue)
        const outgoing = latLngs.slice(0, bestIdx + 1);
        if (outgoing.length > 1) {
            L.polyline(outgoing, {
                color: '#3b82f6',
                weight: 4,
                opacity: 0.9
            }).addTo(this.routeLayer);
        }

        // Return segment (green)
        const returnSeg = latLngs.slice(bestIdx);
        if (returnSeg.length > 1) {
            L.polyline(returnSeg, {
                color: '#22c55e',
                weight: 4,
                opacity: 0.9
            }).addTo(this.routeLayer);
        }

        // Full loop overlay (purple)
        L.polyline(latLngs, {
            color: '#a855f7',
            weight: 2,
            opacity: 0.6
        }).addTo(this.routeLayer);

        // Auto-generated waypoint markers (orange)
        if (waypoints && waypoints.length > 2) {
            for (let i = 1; i < waypoints.length - 1; i++) {
                L.circleMarker([waypoints[i].lat, waypoints[i].lon], {
                    radius: 5,
                    fillColor: '#ffa500',
                    color: '#cc8400',
                    weight: 1.5,
                    fillOpacity: 0.85
                }).addTo(this.routeLayer);
            }
        }
    },

    drawRepetitions(segments) {
        this.repetitionLayer.clearLayers();
        if (!segments) return;

        segments.forEach(seg => {
            L.polyline(
                [[seg.from.lat, seg.from.lon], [seg.to.lat, seg.to.lon]],
                {
                    color: '#ef4444',
                    weight: 6,
                    opacity: 0.7
                }
            ).addTo(this.repetitionLayer);
        });
    },

    toggleRepetitions(show) {
        if (show) {
            this.map.addLayer(this.repetitionLayer);
        } else {
            this.map.removeLayer(this.repetitionLayer);
        }
    },

    zoomToFit(points, geometry) {
        const allCoords = [];

        if (points) {
            points.forEach(p => allCoords.push([p.coordinate.lat, p.coordinate.lon]));
        }

        if (geometry) {
            geometry.forEach(c => allCoords.push([c.lat, c.lon]));
        }

        if (allCoords.length === 0) return;

        if (allCoords.length === 1) {
            this.map.setView(allCoords[0], 12);
            return;
        }

        const bounds = L.latLngBounds(allCoords);
        this.map.fitBounds(bounds, { padding: [40, 40] });
    }
};
