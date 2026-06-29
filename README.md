# Moto Route Finder — Web Server

**Motorcycle loop route generator with automatic stem detection, repetition analysis, and quality scoring. Accessible from any browser — including phones over VPN.**

## About

Moto Route Finder generates scenic motorcycle loop routes from OpenStreetMap data. You set a start point, optionally add waypoints, pick a target distance and direction bias — the engine generates multiple route candidates, analyzes them for road repetition, backtracking stems, and circularity, then presents the best option.

Built on [Itinero](https://github.com/itinero/routing) (OSM routing engine) with a custom motorcycle profile, running as an ASP.NET Core web server with a Leaflet.js frontend.

### Why this exists

Standard navigation apps give you point-A-to-point-B routing. This tool generates **loop routes** — ride out, see interesting roads, come back — which is what motorcyclists actually want. The routing pipeline actively fights common problems like:

- **Backtracking stems** — where the route tracks back on itself because of one-way streets or dead ends
- **Road repetition** — the same road used for both outbound and return legs
- **Non-circular routes** — routes that don't cover enough compass directions

## Features

- **Loop route generation** — set a start point, target distance, and direction; get a circular route
- **Multi-attempt quality loop** — generates 3+ attempts with different turnaround strategies, picks the one with lowest repetition
- **Parallel candidate generation** — run 1-8 candidates simultaneously using a pool of pre-loaded router instances
- **Automatic stem detection & fixing** — identifies backtracking geometry and repairs it with alternative waypoints
- **4-component repetition analysis** — edge duplicates, out-and-back overlap, stem overlap, parallel/divided highway detection
- **Circularity scoring** — bearing spread, sector coverage, and compactness analysis
- **Road quality classification** — prefers scenic/preferred roads, penalizes highways and poor surfaces
- **Direction bias** — nudge routes north, south, east, west, or any compass direction
- **GPX export** — download routes as GPX files for GPS devices
- **Google Maps link** — open route in Google Maps for quick preview
- **Web UI from any device** — works on desktop, tablet, and phone browsers
- **Map caching** — first load processes the OSM file; subsequent loads use a fast binary cache
- **Idle memory management** — automatically unloads map data after 2 minutes of inactivity, reloads on next request
- **Docker deployment** — single `docker-compose up` to run

## Quick Start

### Docker (recommended)

```bash
git clone <your-repo-url>
cd csharp-dotnet10-web

# Edit docker-compose.yml to point volumes at your map files
docker-compose up -d
```

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
┌─────────────────────────────────────────────────────┐
│                    Browser (UI)                      │
│  Leaflet.js map  ·  Route settings  ·  Stats panel  │
│  Click markers   ·  Generate btn    ·  Export btns   │
└──────────────────────┬──────────────────────────────┘
                       │ HTTP/JSON
┌──────────────────────▼──────────────────────────────┐
│              MotoRouteFinder.Server                  │
│         ASP.NET Core (Kestrel on port 5000)         │
│    REST API  ·  Static files  ·  JSON serialization │
└──────────────────────┬──────────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────────┐
│              MotoRouteFinder.Core                    │
│                                                      │
│  RoutingService ── RouteBuilder ── RouteAssembler    │
│       │                │               │             │
│  MapRepository    WaypointGen    StemFixer           │
│  RoadClassifier   AltPathFinder  StemDetector        │
│  RouterDbPool     EdgeBlocker    Diagnostics         │
│       │                                                  │
│  ┌────▼─────┐                                            │
│  │ Itinero   │  Graph routing on OSM data               │
│  │ (v1.6.0)  │  Custom MotorcycleProfile.lua            │
│  └──────────┘                                            │
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
| `POST` | `/maps/clear` | Clear all loaded maps |

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

### System

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET/POST` | `/heartbeat` | Client keepalive ping (resets idle timer) |

## How It Works

### Routing Pipeline

1. **Map Loading** — OSM PBF files are parsed into an Itinero `RouterDb` graph. A binary cache (`.routerdb`) is created for fast subsequent loads.

2. **Waypoint Generation** — Intermediate waypoints are placed at calculated angles around the start point, filtered by road quality (preferred > acceptable > poor).

3. **Multi-Attempt Generation** — The engine runs up to 3 attempts with different turnaround ratios (45%, 55%, 50%) for geometric diversity.

4. **Route Building** — Each attempt progressively places waypoints and routes between them. The builder tracks edge usage to avoid repetition and enforces spatial spread.

5. **Stem Detection & Fixing** — After each segment, geometry analysis checks for backtracking. If detected, the stem's root cause is identified (one-way street, dead end, private road, terrain, overshoot) and repaired with alternative waypoints.

6. **Repetition Analysis** — 4-component breakdown:
   - Edge overlap (exact duplicates)
   - Out-and-back (forward vs return path)
   - Stem overlap (backtracking geometry)
   - Parallel overlap (divided highways)

7. **Quality Scoring** — Weighted formula: repetition (30%), circularity (20%), stem penalty (15%), road types (15%), curvature (10%), distance accuracy (10%).

8. **Selection** — Best attempt chosen by lowest repetition ratio. Tiebreaker uses quality score when within 2%.

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
│   │   └── RouteGeometryUtils.cs # 1400+ lines of geometry math
│   ├── Models/
│   │   ├── Coordinate.cs         # Lat/Lon record
│   │   ├── RouteRequest.cs       # Input: start, waypoints, distance, direction
│   │   ├── RouteResponse.cs      # Output: geometry, stats, repetition segments
│   │   ├── RouteStats.cs         # Quality score formula, 30+ metrics
│   │   ├── DebugStemEvent.cs     # Detailed diagnostic records
│   │   └── ...
│   ├── Resources/
│   │   └── MotorcycleProfile.lua # Itinero routing profile
│   └── Services/
│       ├── RoutingService.cs     # Main orchestrator + memory management
│       ├── RouteBuilder.cs       # Core loop construction + stem fix
│       ├── RouteAssembler.cs     # Segment routing with push-reroute
│       ├── WaypointGenerator.cs  # Angular waypoint placement + sector blocking
│       ├── StemDetector.cs       # Backtracking geometry analysis
│       ├── StemFixer.cs          # Multi-strategy stem repair pipeline
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

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `PORT` | `5000` | HTTP listen port |
| `MAPS_DIR` | `/data/maps` | Directory for map files and cache |
| `ASPNETCORE_ENVIRONMENT` | `Production` | .NET environment |

### Docker Volumes

| Container Path | Purpose |
|----------------|---------|
| `/data/maps` | Map files (`.osm.pbf`) and cache (`.routerdb`) |
| `/data/uploads` | Uploaded map files |

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

## Current Issues / TODO

### Known Issues

- **Repetition overlay not wired up** — `drawRepetitions()` exists in `map.js` but is never called from the frontend. The API returns repetition segments but they aren't displayed on the map.
- **Memory baseline** — After idle unload, ~117 MB RSS remains (normal .NET runtime + ASP.NET overhead).
- **Single-waypoint circularity** — A single waypoint won't produce a true circular route; the algorithm needs 2+ waypoints for meaningful loops.

### Planned Improvements

- [ ] Wire up repetition segment overlay on the map
- [ ] Better waypoint angular spread enforcement for more circular routes
- [ ] Circularity minimum threshold — reject routes below a circularity score
- [ ] Save/load route history in the browser
- [ ] Multi-map support improvements (merge adjacent regions)
- [ ] User-configurable memory pool size for candidate generation
- [ ] Real-time progress streaming via SignalR

## License

MIT
