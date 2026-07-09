# Moto Route Finder — Web Server

**Motorcycle loop route generator with repetition analysis, curvature scoring, and quality scoring. Accessible from any browser — including phones over VPN.**

## About

Moto Route Finder generates scenic motorcycle loop routes from OpenStreetMap data. You set a start point, pick a target distance and direction bias — the engine generates multiple route candidates, analyzes them for road repetition and circularity, then presents the best option.

Built on [Itinero](https://github.com/itinero/routing) (OSM routing engine) with a custom motorcycle profile, running as an ASP.NET Core web server with a Leaflet.js frontend.

### Why this exists

Standard navigation apps give you point-A-to-point-B routing. This tool generates **loop routes** — ride out, see interesting roads, come back — which is what motorcyclists actually want. The routing pipeline actively fights common problems like:

- **Road repetition** — the same road used for both outbound and return legs
- **Non-circular routes** — routes that don't cover enough compass directions

## Features

- **Loop route generation** — set a start point, target distance, and direction; get a circular route
- **Multi-attempt quality loop** — generates 3+ attempts with different turnaround strategies, picks the one with lowest repetition
- **Parallel candidate generation** — run 1-8 candidates simultaneously using a pool of pre-loaded router instances
- **3-component repetition analysis** — edge duplicates, out-and-back overlap, parallel/divided highway detection
- **Circularity scoring** — bearing spread, sector coverage, and compactness analysis
- **Road quality classification** — prefers scenic/preferred roads, penalizes highways and poor surfaces
- **Direction bias** — nudge routes north, south, east, west, or any compass direction
- **GPX export** — download routes as GPX files for GPS devices
- **Google Maps link** — open route in Google Maps for quick preview
- **Web UI from any device** — works on desktop, tablet, and phone browsers
- **Map caching** — first load processes the OSM file; subsequent loads use a fast binary cache
- **Idle memory management** — automatically unloads map data after 2 minutes of inactivity, reloads on next request
- **Test mode** — batch generate N routes with diagnostics, auto-cycling direction bias and random distances
- **Loading overlay** — spinner during generation/test with cancel support
- **Estimated arrival time** — shown in stats panel alongside distance and duration
- **Dark map tiles toggle** — switch to CartoDB Dark Matter theme
- **Custom route name** — name routes for GPX export
- **Compact diagnostics mode** — toggle verbose fields to reduce JSON size
- **Docker deployment** — single `docker-compose up` to run

## Quick Start

### Docker (recommended)

```bash
git clone <your-repo-url>
cd csharp-dotnet10-web

# Set MAPS_HOST_DIR to your map files location (absolute path recommended)
# Option 1: Create a .env file next to docker-compose.yml
echo "MAPS_HOST_DIR=/home/user/MotorTour/maps" > .env

# Option 2: Export as environment variable
export MAPS_HOST_DIR=/path/to/your/maps

docker-compose up -d
```

**Portainer users:** Set `MAPS_HOST_DIR` as a stack Environment variable in the Portainer UI with an **absolute** host path (e.g., `/home/user/MotorTour/maps`). The relative `./maps` fallback is not suitable for Portainer git stacks because Portainer clones the repo into its own data directory.

Open `http://localhost:5000` in your browser.

### Manual (.NET 10 SDK required)

```bash
dotnet restore
dotnet run --project MotoRouteFinder.Server
```

### First Use

1. **Load a map** — upload an `.osm.pbf` file or browse to one on the server
2. **Click the map** to set a start point (green marker)
3. **Set route parameters** — target distance, duration, direction bias
4. **Click Generate** — wait for the route to be computed
5. **View results** — see stats, toggle repetition overlay, export GPX

## Architecture

```
┌──────────────────────────────────────────────────────┐
│                     Browser (UI)                     │
│ Leaflet.js map  ·  Route settings  ·  Stats panel    │
│ Click markers   ·  Generate btn    ·  Export btns    │
└───────────────────────────┬──────────────────────────┘
                            │ HTTP/JSON
┌───────────────────────────▼──────────────────────────┐
│                MotoRouteFinder.Server                │
│         ASP.NET Core (Kestrel on port 5000)          │
│   REST API  ·  Static files  ·  JSON serialization   │
└───────────────────────────┬──────────────────────────┘
                            │
┌───────────────────────────▼──────────────────────────┐
│                 MotoRouteFinder.Core                 │
│                                                      │
│ RoutingService ── RouteBuilder ── RouteAssembler     │
│ │                 │               │                  │
│ MapRepository     WaypointGen     EdgeBlocker        │
│ RoadClassifier    AltPathFinder   RouteAssembler     │
│ RouterDbPool      EdgeBlocker     Diagnostics        │
│                                                      │
│ ┌────▼─────┐                                         │
│ │ Itinero   │  Graph routing on OSM data             │
│ │ (v1.6.0)  │  Custom MotorcycleProfile.lua          │
│ └──────────┘                                         │
└──────────────────────────────────────────────────────┘
```

## API Reference

All endpoints are under `/api/route/`.

### Map Management

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/maps/status` | Map loading status, loaded maps, memory usage |
| `POST` | `/maps/upload` | Upload `.osm`/`.pbf`/`.routerdb` files (up to 1 GB) |
| `POST` | `/maps/load-server` | Load a map from a server-side file path |
| `GET` | `/maps/browse` | Browse server directories for map files |
| `POST` | `/maps/saved/load` | Load a map from the saved maps list |
| `GET` | `/maps/saved` | List saved maps with existence and size info |
| `DELETE` | `/maps/saved` | Remove a map from the saved list |
| `POST` | `/maps/unload` | Unload all maps from memory |

### Route Generation

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/routes/generate` | Generate a single loop route |
| `POST` | `/routes/generate-candidates` | Generate N candidates in parallel, return best + all |

### Export

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/export/gpx` | Generate GPX XML string |
| `POST` | `/export/gpx/download` | Download GPX file |
| `POST` | `/export/google-maps` | Generate Google Maps URL |

### Test

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/routes/test-run` | Batch test: generate N routes, save diagnostics, return summary |

### System

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/health` | Health check (status, memory, uptime) |
| `GET/POST` | `/heartbeat` | Client keepalive ping (resets idle timer) |

## How It Works

### Routing Pipeline

1. **Map Loading** — OSM PBF files are parsed into an Itinero `RouterDb` graph. A binary cache (`.routerdb`) is created for fast subsequent loads.

2. **Waypoint Generation** — Intermediate waypoints are placed at calculated angles around the start point, filtered by road quality (preferred > acceptable > poor).

3. **Multi-Candidate Generation** — The engine generates 3 candidates using `AlternativePathFinder` (GraphHopper-inspired forward-then-alternative path with edge penalties). Candidates are scored by bearing spread × (1 - overlap). Falls back to `BuildProgressiveLoop` if the alternative path fails.

4. **Route Building** — Each attempt progressively places waypoints and routes between them. The builder tracks edge usage to avoid repetition and enforces spatial spread.

5. **Repetition Analysis** — 3-component breakdown:
   - Edge overlap (exact duplicates)
   - Out-and-back (forward vs return path)
   - Parallel overlap (divided highways)

6. **Quality Scoring** — Weighted formula: repetition (35.3%), circularity (23.5%), road types (17.6%), curvature (11.8%), distance accuracy (11.8%).

7. **Selection** — Best attempt chosen by QualityScore. Tiebreaker uses RepetitionRatio.

### Parallel Candidates

When generating multiple candidates, the system uses a `RouterDbPool` — pre-loaded copies of the graph (each ~500 MB) that run in parallel via `Parallel.For`. Each candidate gets its own graph copy, avoiding thread contention on Itinero's internal state.

## Project Structure

```
csharp-dotnet10-web/
├── Dockerfile                    # Multi-stage Docker build
├── docker-compose.yml            # Docker deployment config
├── MotoRouteFinder.Web.sln       # Solution file
│
├── MotoRouteFinder.Core/         # Core routing library
│   ├── Helpers/
│   │   ├── GeoConstants.cs       # Geographic constants
│   │   └── RouteGeometryUtils.cs # 1570+ lines of geometry math
│   ├── Models/
│   │   ├── Coordinate.cs         # Lat/Lon record
│   │   ├── RouteRequest.cs       # Input: start, waypoints, distance, direction
│   │   ├── RouteResponse.cs      # Output: geometry, stats, repetition segments
│   │   ├── RouteStats.cs         # Quality score formula, 30+ metrics
│   │   ├── RouteGenerationOptions.cs # 45+ configurable parameters
│   │   ├── DebugStemEvent.cs     # Detailed diagnostic records
│   │   ├── DiagnosticsOutput.cs  # Diagnostic data model
│   │   ├── BuildContext.cs       # Shared state for route construction
│   │   ├── MapPoint.cs           # Map point model
│   │   └── ...
│   ├── Resources/
│   │   └── MotorcycleProfile.lua # Itinero routing profile
│   └── Services/
│       ├── RoutingService.cs     # Main orchestrator + memory management
│       ├── RouteBuilder.cs       # Core loop construction
│       ├── RouteAssembler.cs     # Segment routing with push-reroute
│       ├── WaypointGenerator.cs  # Angular waypoint placement + sector blocking
│       ├── AlternativePathFinder.cs # GraphHopper-inspired corridor blocking
│       ├── MapRepository.cs      # OSM loading, caching, lifecycle
│       ├── RoadClassifier.cs     # Road quality from OSM tags
│       ├── EdgeBlocker.cs        # Edge blocking/penalty (disposable scopes)
│       ├── RouterDbPool.cs       # Thread-safe pool for parallel candidates
│       ├── RouteStatistics.cs    # Repetition breakdown + route metrics
│       ├── DiagnosticsCollector.cs # Aggregated debug output
│       └── ExportService.cs      # GPX + Google Maps URL generation
│
└── MotoRouteFinder.Server/       # ASP.NET Core web host
    ├── Program.cs                # Kestrel config, DI, JSON options
    ├── Controllers/
    │   └── RouteController.cs    # All REST API endpoints
    └── wwwroot/
        ├── index.html            # Single-page app
        ├── css/style.css         # Dark theme, mobile responsive
        └── js/
            ├── app.js            # App logic, API calls, heartbeat
            └── map.js            # Leaflet map, markers, route drawing
```

## Configuration

### appsettings.json

The `RouteGeneration` section contains every tunable parameter with inline documentation. See `appsettings.json` for the full list with descriptions — all defaults match the code. Override any value by setting it in this section.

| Key | Default | Description |
|-----|---------|-------------|
| `MaxUploadSizeBytes` | `1073741824` | Max upload size (1 GB) |

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `PORT` | `5000` | HTTP listen port |
| `HOST` | `127.0.0.1` | HTTP listen address (set to `0.0.0.0` to expose on all interfaces) |
| `MAPS_DIR` | `/data/maps` | Directory for map files and cache |
| `ASPNETCORE_ENVIRONMENT` | `Production` | .NET environment |

### Docker Volumes

| Container Path | Purpose |
|----------------|---------|
| `/data/maps` | Map files (`.osm.pbf`) and cache (`.routerdb`) |

## Tech Stack

| Component | Technology |
|-----------|-----------|
| **Routing Engine** | [Itinero](https://github.com/itinero/routing) 1.6.0-pre037 |
| **Map Data** | OpenStreetMap (`.osm.pbf` format) |
| **Backend** | ASP.NET Core / .NET 10 (C#) |
| **Frontend** | Leaflet.js, vanilla JavaScript |
| **Containerization** | Docker + Docker Compose |
| **Geometry** | NetTopologySuite 2.6.0, custom haversine/bearing math |

### Hardware Notes

Tested on:
- **Laptop:** Intel 8540U (6c/12t, 4.9 GHz boost, 16 MB L3) — handles 20+ routes comfortably
- **Desktop:** AMD 9800X3D (8c/16t, 5.3 GHz boost, 96 MB L3) — faster parallel generation

The application should work well on slower PCs too. The `CandidateCount` setting lets you scale parallelism to match your hardware.

## Current Status

**Verified quality (batch 59, unified QualityScore selection):**
- Quality Score: mean 92.0, median 92+
- Repetition Ratio: mean 0.009 (well under 5% threshold)
- Overshoot Ratio: mean 0.97–1.00 (routes hitting target distance)
- Penalty escalation ladder: 89–93% `very_high` tier success rate
- CurvatureScore: mean 99.2 (piecewise-linear plateau, recalibrated)
- CircularitySpread: mean 91.3 (de-saturated with 270° max spread)

### Known Limitations

- **Memory baseline** — After idle unload, actual app memory (`anon` in cgroup stats) drops to ~85 MB (normal .NET runtime + ASP.NET overhead). However, Docker/Portainer memory stats include Linux page cache for files the app has read (e.g. `.routerdb` map caches), which can make total reported container memory appear much higher (~1.2–1.4 GB observed) even though the app's working memory is small. This is reclaimable, not a leak — the kernel evicts these pages instantly under real memory pressure. To check actual usage, run `docker exec <container> cat /sys/fs/cgroup/memory.stat` (cgroup v2) or `/sys/fs/cgroup/memory/memory.stat` (cgroup v1) and look at `anon` (real usage) vs `file`/`inactive_file` (reclaimable cache).
- **Single-waypoint circularity** — A single waypoint won't produce a true circular route; the algorithm needs 2+ waypoints for meaningful loops.
- **Forward-path-only circularity scoring** — Circularity is scored on the forward path only (not the full loop). Scores are lower but more honest.
- **Manual waypoint mode disabled** — Adding intermediate waypoints via the map UI is currently disabled. The return-path routing lacks the loop-diversity logic (sector blocking, edge-penalty escalation, turnaround search) that auto-loop mode uses, which previously produced out-and-back "stem" routes instead of loops. The underlying code (`GenerateWaypointRoute`/`IterativeRouteLoop`) is preserved for future work.

### Routing Algorithm

`alternative_path` is the primary route builder with a working penalty escalation ladder (normal → very_high → high → push_fallback). `progressive_loop` exists only as a fallback when the alternative path fails.

### Planned Improvements

- [ ] Circularity minimum threshold — reject routes below a circularity score
- [ ] Parallel candidate pool scaling based on hardware
- [ ] Save/load route history in the browser
- [ ] Multi-map support improvements (merge adjacent regions)
- [ ] Real-time progress streaming via SignalR

## License

This project's code is licensed under the [MIT License](LICENSE).

Map data is © [OpenStreetMap](https://www.openstreetmap.org/copyright) contributors, available under the [Open Database License (ODbL)](https://opendatacommons.org/licenses/odbl/). This project does not redistribute OSM data or derived route-cache files — maps are loaded and cached locally by each user from their own OSM extracts.
