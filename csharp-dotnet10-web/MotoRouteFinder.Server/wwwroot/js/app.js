const MotoApp = {
    points: [],
    currentRoute: null,
    currentStats: null,
    alternativeCandidates: [],
    isGenerating: false,
    isTestRunning: false,
    mode: 'generate',
    abortController: null,

    init() {
        MotoMap.init();
        this.bindEvents();
        this.updateTestFileCount();
        this.loadSavedMaps();
    },

    bindEvents() {
        document.getElementById('btnLoadMap').addEventListener('click', () => {
            document.getElementById('fileInput').click();
        });

        document.getElementById('btnBrowse').addEventListener('click', () => this.openBrowse());
        document.getElementById('btnCloseBrowse').addEventListener('click', () => this.closeBrowse());
        document.getElementById('btnCancelBrowse').addEventListener('click', () => this.closeBrowse());
        document.getElementById('btnLoadSelected').addEventListener('click', () => this.loadSelectedFile());

        document.getElementById('fileInput').addEventListener('change', (e) => this.handleFileUpload(e));

        document.getElementById('btnClearRoute').addEventListener('click', () => this.clearRoute());
        document.getElementById('btnClearAll').addEventListener('click', () => this.clearAll());
        document.getElementById('btnRemoveLast').addEventListener('click', () => this.removeLastPoint());

        document.getElementById('btnGenerate').addEventListener('click', () => this.generateRoute());
        document.getElementById('btnCancel').addEventListener('click', () => this.cancelGeneration());

        document.getElementById('btnRunTest').addEventListener('click', () => this.runTest());
        document.getElementById('btnCancelTest').addEventListener('click', () => this.cancelGeneration());

        document.getElementById('btnGoogleMaps').addEventListener('click', () => this.exportGoogleMaps());
        document.getElementById('btnDownloadGpx').addEventListener('click', () => this.exportGpx());

        document.getElementById('showRepetitions').addEventListener('change', (e) => {
            MotoMap.toggleRepetitions(e.target.checked);
        });

        document.getElementById('targetDistance').addEventListener('input', (e) => {
            const v = parseFloat(e.target.value);
            if (v > 0) document.getElementById('targetDuration').value = 0;
        });

        document.getElementById('targetDuration').addEventListener('input', (e) => {
            const v = parseFloat(e.target.value);
            if (v > 0) document.getElementById('targetDistance').value = 0;
        });

        document.querySelectorAll('.mode-btn').forEach(btn => {
            btn.addEventListener('click', (e) => this.setMode(e.target.dataset.mode));
        });

        document.getElementById('testRouteCount').addEventListener('input', () => this.updateTestFileCount());
        document.getElementById('candidatesPerRoute').addEventListener('input', () => this.updateTestFileCount());
    },

    setStatus(msg) {
        document.getElementById('statusBar').textContent = msg;
    },

    setMode(mode) {
        this.mode = mode;
        document.querySelectorAll('.mode-btn').forEach(b => b.classList.toggle('active', b.dataset.mode === mode));

        document.getElementById('generateSection').style.display = mode === 'generate' ? '' : 'none';
        document.getElementById('testSection').style.display = mode === 'test' ? '' : 'none';
        document.getElementById('durationRow').style.display = mode === 'generate' ? '' : 'none';
        document.getElementById('directionRow').style.display = mode === 'generate' ? '' : 'none';
    },

    updateTestFileCount() {
        const routes = parseInt(document.getElementById('testRouteCount').value) || 5;
        const candidates = parseInt(document.getElementById('candidatesPerRoute').value) || 4;
        document.getElementById('testFileCountLabel').textContent =
            `${routes} routes x ${candidates} candidates = ${routes * candidates} diagnostics files`;
    },

    updateButtons() {
        const hasMap = document.getElementById('loadedMapsSection').style.display !== 'none';
        const hasPoints = this.points.length > 0;
        const canGenerate = hasMap && hasPoints && !this.isGenerating && !this.isTestRunning;

        document.getElementById('btnGenerate').disabled = !canGenerate;
        document.getElementById('btnRunTest').disabled = !canGenerate;
        document.getElementById('btnClearRoute').disabled = !this.currentRoute;
    },

    addPoint(lat, lon) {
        if (this.isGenerating || this.isTestRunning) return;

        const type = this.points.length === 0 ? 'start' : 'waypoint';
        const label = type === 'start' ? 'Start Point' : `Waypoint ${this.points.length}`;
        const point = {
            id: Date.now().toString(),
            type: type,
            coordinate: { lat, lon },
            label: label
        };

        this.points.push(point);
        this.updatePointsList();
        MotoMap.updateMarkers(this.points);
        this.setStatus(type === 'start' ? 'Start point set. Click to add waypoints' : `Added ${label}`);
        this.updateButtons();
    },

    removePoint(id) {
        this.points = this.points.filter(p => p.id !== id);
        this.reindexPoints();
        this.updatePointsList();
        MotoMap.updateMarkers(this.points);
        this.updateButtons();
    },

    removeLastPoint() {
        if (this.points.length === 0) return;
        this.points.pop();
        this.reindexPoints();
        this.updatePointsList();
        MotoMap.updateMarkers(this.points);
        this.updateButtons();
    },

    reindexPoints() {
        this.points.forEach((p, i) => {
            p.type = i === 0 ? 'start' : 'waypoint';
            p.label = i === 0 ? 'Start Point' : `Waypoint ${i}`;
        });
    },

    updatePointsList() {
        const container = document.getElementById('pointsList');
        container.innerHTML = this.points.map(p => {
            const badgeClass = p.type === 'start' ? 'start' : 'waypoint';
            const letter = p.label.charAt(0);
            return `<div class="point-item">
                <div class="point-badge ${badgeClass}">${letter}</div>
                <div class="point-label">${p.label}</div>
                <button class="point-remove" onclick="MotoApp.removePoint('${p.id}')">X</button>
            </div>`;
        }).join('');
    },

    updateStats(stats) {
        if (!stats) {
            document.getElementById('statsSection').style.display = 'none';
            return;
        }

        document.getElementById('statsSection').style.display = '';
        document.getElementById('statDistance').textContent = stats.totalDistanceKm.toFixed(1);
        document.getElementById('statDuration').textContent = stats.totalDurationMin.toFixed(0);

        const repRatio = stats.repetitionRatio;
        const repEl = document.getElementById('statRepetition');
        repEl.textContent = (repRatio * 100).toFixed(1) + '%';
        repEl.style.color = repRatio < 0.10 ? '#22c55e' : repRatio < 0.25 ? '#f97316' : '#ef4444';

        const roadTypesSection = document.getElementById('roadTypesSection');
        const roadTypesList = document.getElementById('roadTypesList');
        if (stats.roadTypes && Object.keys(stats.roadTypes).length > 0) {
            roadTypesSection.style.display = '';
            roadTypesList.innerHTML = Object.entries(stats.roadTypes)
                .map(([name, km]) => `<div class="road-type-item">
                    <span class="name">${name}</span>
                    <span class="km">${km.toFixed(1)} km</span>
                </div>`).join('');
        } else {
            roadTypesSection.style.display = 'none';
        }
    },

    updateAlternatives(candidates) {
        const section = document.getElementById('alternativesSection');
        const list = document.getElementById('alternativesList');

        if (!candidates || candidates.length === 0) {
            section.style.display = 'none';
            return;
        }

        section.style.display = '';
        list.innerHTML = candidates.map((c, i) => {
            const s = c.stats;
            return `<div class="alt-item" data-index="${i}" onclick="MotoApp.selectCandidate(${i})">
                <div class="alt-score">Quality: ${(s?.qualityScore || 0).toFixed(1)}</div>
                <div class="alt-detail">Distance: ${(s?.totalDistanceKm || 0).toFixed(0)} km | Repetition: ${((s?.repetitionRatio || 0) * 100).toFixed(1)}%</div>
            </div>`;
        }).join('');
    },

    selectCandidate(index) {
        if (index >= this.alternativeCandidates.length) return;

        const candidate = this.alternativeCandidates[index];
        this.currentRoute = candidate;
        this.currentStats = candidate.stats;

        MotoMap.drawRoute(candidate.routeGeometry, candidate.waypoints);
        this.updateStats(candidate.stats);
        this.zoomToFit();

        document.querySelectorAll('.alt-item').forEach((el, i) => {
            el.classList.toggle('selected', i === index);
        });

        document.getElementById('btnClearRoute').disabled = false;
        document.getElementById('exportSection').style.display = '';
    },

    zoomToFit() {
        const routePoints = this.currentRoute?.routeGeometry || [];
        const userPoints = this.points;
        MotoMap.zoomToFit(userPoints, routePoints);
    },

    clearRoute() {
        this.currentRoute = null;
        this.currentStats = null;
        this.alternativeCandidates = [];
        MotoMap.clearRoute();
        this.updateStats(null);
        document.getElementById('alternativesSection').style.display = 'none';
        document.getElementById('exportSection').style.display = 'none';
        document.getElementById('btnClearRoute').disabled = true;
        document.getElementById('showRepetitions').checked = false;
    },

    clearAll() {
        if (this.isGenerating || this.isTestRunning) this.cancelGeneration();
        this.points = [];
        this.clearRoute();
        MotoMap.clearAll();
        document.getElementById('loadedMapsSection').style.display = 'none';
        document.getElementById('loadedMapsList').innerHTML = '';
        this.setStatus('Click on the map to set start point');
        this.updateButtons();
    },

    async handleFileUpload(e) {
        const files = e.target.files;
        if (!files || files.length === 0) return;

        this.setStatus('Uploading and loading map...');
        const formData = new FormData();
        for (const file of files) {
            formData.append('files', file);
        }

        try {
            const res = await fetch('/api/route/maps/upload', {
                method: 'POST',
                body: formData
            });
            const data = await res.json();

            if (!res.ok) throw new Error(data.details || data.error || JSON.stringify(data));

            this.showLoadedMaps(data.loadedMaps);
            this.setStatus(`Map loaded: ${data.loadedMaps.join(', ')}`);
            this.loadSavedMaps();
        } catch (err) {
            this.setStatus(`Error: ${err.message}`);
        }

        e.target.value = '';
        this.updateButtons();
    },

    browseCurrentPath: '',
    browseSelectedFile: null,

    async openBrowse() {
        this.browseSelectedFile = null;
        document.getElementById('btnLoadSelected').disabled = true;
        document.getElementById('browseModal').style.display = '';
        await this.browseDirectory('');
    },

    closeBrowse() {
        document.getElementById('browseModal').style.display = 'none';
    },

    async browseDirectory(path) {
        const list = document.getElementById('browseList');
        list.innerHTML = '<div style="padding:16px;color:var(--gray);text-align:center">Loading...</div>';

        try {
            const url = path ? `/api/route/maps/browse?path=${encodeURIComponent(path)}` : '/api/route/maps/browse';
            const res = await fetch(url);
            const data = await res.json();
            if (!res.ok) throw new Error(data.detail || data.error || JSON.stringify(data.errors || data));

            this.browseCurrentPath = data.currentPath;
            document.getElementById('browseCurrentPath').textContent = data.currentPath;

            list.innerHTML = data.entries.map(e => {
                const icon = e.isDirectory ? '📁' : '🗺️';
                const size = e.sizeMB != null ? `${e.sizeMB} MB` : '';
                return `<div class="browse-item" data-path="${this.escapeAttr(e.fullPath)}" data-dir="${e.isDirectory}" onclick="MotoApp.browseSelect(this)">
                    <span class="icon">${icon}</span>
                    <span class="name">${this.escapeHtml(e.name)}</span>
                    <span class="size">${size}</span>
                </div>`;
            }).join('');

            if (data.entries.length === 0) {
                list.innerHTML = '<div style="padding:16px;color:var(--gray);text-align:center">No map files in this directory</div>';
            }
        } catch (err) {
            list.innerHTML = `<div style="padding:16px;color:var(--accent);text-align:center">Error: ${this.escapeHtml(err.message)}</div>`;
        }
    },

    browseSelect(el) {
        const path = el.dataset.path;
        const isDir = el.dataset.dir === 'true';

        if (isDir) {
            this.browseDirectory(path);
            return;
        }

        document.querySelectorAll('.browse-item').forEach(e => e.classList.remove('selected'));
        el.classList.add('selected');
        this.browseSelectedFile = path;
        document.getElementById('btnLoadSelected').disabled = false;
    },

    async loadSelectedFile() {
        if (!this.browseSelectedFile) return;

        const filePath = this.browseSelectedFile;
        this.closeBrowse();
        this.setStatus(`Loading map: ${filePath}...`);

        try {
            const res = await fetch('/api/route/maps/load-server', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ serverPath: filePath, avoidHighways: true })
            });
            const data = await res.json();
            if (!res.ok) throw new Error(data.detail || data.error || JSON.stringify(data.errors || data));

            this.showLoadedMaps(data.loadedMaps);
            this.setStatus(`Map loaded: ${data.loadedMaps.join(', ')}`);
            this.loadSavedMaps();
        } catch (err) {
            this.setStatus(`Error: ${err.message}`);
        }
        this.updateButtons();
    },

    async loadSavedMaps() {
        try {
            const res = await fetch('/api/route/maps/saved');
            const maps = await res.json();
            this.renderSavedMaps(maps);
        } catch (err) {
            console.error('Failed to load saved maps:', err);
        }
    },

    renderSavedMaps(maps) {
        const list = document.getElementById('savedMapsList');
        const section = document.getElementById('savedMapsSection');

        section.style.display = '';

        if (!maps || maps.length === 0) {
            list.innerHTML = '<div class="hint">No saved maps yet. Load a map to save it.</div>';
            return;
        }

        list.innerHTML = maps.map(m => {
            const icon = m.type === 'cache' ? '💾' : '🗺️';
            const notFound = !m.exists ? ' not-found' : '';
            const title = m.exists ? m.path : `${m.path} (not found)`;
            const date = m.lastLoaded ? new Date(m.lastLoaded).toLocaleDateString() : '';
            const size = m.sizeMB != null ? `${m.sizeMB} MB` : '';
            return `<div class="saved-map-item${notFound}" title="${this.escapeAttr(title)}" onclick="MotoApp.loadSavedMap('${this.escapeAttr(m.path)}')">
                <span class="map-icon">${icon}</span>
                <div class="map-info">
                    <div class="map-name">${this.escapeHtml(m.name)}</div>
                    <div class="map-meta">${size}${size && date ? ' · ' : ''}${date}</div>
                </div>
                <span class="map-badge ${m.type}">${m.type}</span>
                <button class="map-remove" onclick="event.stopPropagation();MotoApp.removeSavedMap('${this.escapeAttr(m.path)}')" title="Remove">X</button>
            </div>`;
        }).join('');
    },

    async loadSavedMap(path) {
        this.setStatus(`Loading: ${path}...`);
        try {
            const res = await fetch('/api/route/maps/saved/load', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ path: path, avoidHighways: true })
            });
            const data = await res.json();
            if (!res.ok) throw new Error(data.detail || data.error || JSON.stringify(data.errors || data));

            this.showLoadedMaps(data.loadedMaps);
            this.setStatus(`Map loaded: ${data.loadedMaps.join(', ')}`);
            this.loadSavedMaps();
        } catch (err) {
            this.setStatus(`Error: ${err.message}`);
        }
        this.updateButtons();
    },

    async removeSavedMap(path) {
        try {
            await fetch(`/api/route/maps/saved?path=${encodeURIComponent(path)}`, {
                method: 'DELETE'
            });
            this.loadSavedMaps();
        } catch (err) {
            this.setStatus(`Error: ${err.message}`);
        }
    },

    escapeHtml(str) {
        return str.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
    },

    escapeAttr(str) {
        return str.replace(/\\/g, '\\\\').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    },

    showLoadedMaps(maps) {
        const section = document.getElementById('loadedMapsSection');
        const list = document.getElementById('loadedMapsList');
        section.style.display = '';
        list.innerHTML = maps.map(m => `<div style="font-size:12px;padding:2px 0;color:#fff">${m}</div>`).join('');
    },

    buildRouteRequest() {
        const start = this.points[0];
        const waypoints = this.points.slice(1).map(p => p.coordinate);

        const dist = parseFloat(document.getElementById('targetDistance').value);
        const dur = parseFloat(document.getElementById('targetDuration').value);

        return {
            start: start.coordinate,
            waypoints: waypoints,
            targetDistanceKm: dist > 0 ? dist : null,
            targetDurationMin: dur > 0 ? dur : null,
            avoidHighways: document.getElementById('avoidHighways').checked,
            waypointCount: parseInt(document.getElementById('waypointCount').value) || 6,
            direction: document.getElementById('directionBias').value
        };
    },

    async generateRoute() {
        if (this.points.length === 0 || this.isGenerating) return;

        this.isGenerating = true;
        this.abortController = new AbortController();
        const candidatesCount = parseInt(document.getElementById('candidatesPerRoute').value) || 4;
        const request = this.buildRouteRequest();

        document.getElementById('btnGenerate').style.display = 'none';
        document.getElementById('btnCancel').style.display = '';
        this.setStatus(`<span class="spinner"></span>Generating route (${candidatesCount} candidates)...`);

        try {
            const res = await fetch('/api/route/routes/generate-candidates', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ routeRequest: request, candidateCount: candidatesCount }),
                signal: this.abortController.signal
            });

            const data = await res.json();
            if (!res.ok) throw new Error(data.detail || data.error || JSON.stringify(data.errors || data));

            this.currentRoute = data.best;
            this.currentStats = data.best.stats;
            this.alternativeCandidates = data.candidates;

            MotoMap.drawRoute(data.best.routeGeometry, data.best.waypoints);
            this.updateStats(data.best.stats);
            this.updateAlternatives(data.candidates);
            this.zoomToFit();

            document.getElementById('btnClearRoute').disabled = false;
            document.getElementById('exportSection').style.display = '';

            const scores = data.candidates.map(c => (c.stats?.qualityScore || 0).toFixed(1)).join(', ');
            this.setStatus(`Route generated | Quality: ${data.best.stats?.qualityScore?.toFixed(1)} | Scores: [${scores}]`);
        } catch (err) {
            if (err.name !== 'AbortError') {
                this.setStatus(`Error: ${err.message}`);
            }
        } finally {
            this.isGenerating = false;
            document.getElementById('btnGenerate').style.display = '';
            document.getElementById('btnCancel').style.display = 'none';
            this.updateButtons();
        }
    },

    async runTest() {
        if (this.points.length === 0 || this.isTestRunning) return;

        this.isTestRunning = true;
        this.abortController = new AbortController();
        const request = this.buildRouteRequest();
        const testCount = parseInt(document.getElementById('testRouteCount').value) || 5;
        const candidatesCount = parseInt(document.getElementById('candidatesPerRoute').value) || 4;
        const randomWp = document.getElementById('randomWaypointCount').checked;

        document.getElementById('btnRunTest').style.display = 'none';
        document.getElementById('btnCancelTest').style.display = '';
        document.getElementById('testProgress').style.display = '';

        const directions = ['Any', 'North', 'South', 'East', 'West', 'Northeast', 'Southeast', 'Southwest', 'Northwest'];
        let lastRoute = null;
        let totalQuality = 0;

        try {
            for (let i = 0; i < testCount; i++) {
                document.getElementById('testProgress').textContent = `Route ${i + 1}/${testCount}`;
                this.setStatus(`<span class="spinner"></span>Test: Route ${i + 1}/${testCount} (${candidatesCount} candidates)`);

                const testRequest = { ...request };
                testRequest.targetDistanceKm = 50 + Math.random() * 150;
                testRequest.direction = directions[i % directions.length];
                if (randomWp) testRequest.waypointCount = 3 + Math.floor(Math.random() * 10);

                const res = await fetch('/api/route/routes/generate-candidates', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ routeRequest: testRequest, candidateCount: candidatesCount }),
                    signal: this.abortController.signal
                });

                const data = await res.json();
                if (!res.ok) throw new Error(data.detail || data.error || JSON.stringify(data.errors || data));

                lastRoute = data.best;
                totalQuality += data.best.stats?.qualityScore || 0;
            }

            if (lastRoute) {
                this.currentRoute = lastRoute;
                this.currentStats = lastRoute.stats;
                MotoMap.drawRoute(lastRoute.routeGeometry, lastRoute.waypoints);
                this.updateStats(lastRoute.stats);
                this.zoomToFit();

                const avgQuality = totalQuality / testCount;
                this.setStatus(`Test complete: ${testCount} routes | Avg quality: ${avgQuality.toFixed(1)}`);
            }
        } catch (err) {
            if (err.name !== 'AbortError') {
                this.setStatus(`Test error: ${err.message}`);
            }
        } finally {
            this.isTestRunning = false;
            document.getElementById('btnRunTest').style.display = '';
            document.getElementById('btnCancelTest').style.display = 'none';
            document.getElementById('testProgress').style.display = 'none';
            this.updateButtons();
        }
    },

    cancelGeneration() {
        if (this.abortController) {
            this.abortController.abort();
            this.abortController = null;
        }
    },

    async exportGoogleMaps() {
        if (!this.currentRoute) return;

        try {
            const res = await fetch('/api/route/export/google-maps', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    routeGeometry: this.currentRoute.routeGeometry,
                    start: this.points[0]?.coordinate
                })
            });
            const data = await res.json();
            if (!res.ok) throw new Error(data.detail || data.error || JSON.stringify(data.errors || data));

            window.open(data.url, '_blank');
        } catch (err) {
            this.setStatus(`Export error: ${err.message}`);
        }
    },

    async exportGpx() {
        if (!this.currentRoute) return;

        try {
            const res = await fetch('/api/route/export/gpx/download', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    routeGeometry: this.currentRoute.routeGeometry,
                    start: this.points[0]?.coordinate,
                    waypoints: this.currentRoute.waypoints
                })
            });

            if (!res.ok) {
                const data = await res.json();
                throw new Error(data.error);
            }

            const blob = await res.blob();
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = res.headers.get('content-disposition')?.split('filename=')[1] || 'moto-route.gpx';
            a.click();
            URL.revokeObjectURL(url);
        } catch (err) {
            this.setStatus(`GPX error: ${err.message}`);
        }
    }
};

document.addEventListener('DOMContentLoaded', () => MotoApp.init());
