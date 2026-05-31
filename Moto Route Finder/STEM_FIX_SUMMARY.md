# Stem Detection and Correction - Implementation Summary

## Problem Analysis

The previous implementation had several critical issues that prevented effective stem detection and correction:

### Issue 1: Grid Cell Size Too Small
- **Old value**: `GRID_SIZE: 0.00015` (~15 meters)
- **Problem**: When a route goes out and back on the same road, the outbound and return passes might be in different lanes or have slight GPS variations (5-10m apart). With 15m grid cells, these would round to different cells and not be detected as repetitions.
- **Fix**: Increased to `GRID_SIZE: 0.0005` (~50 meters) to reliably catch stems even with lane variations.

### Issue 2: No Bearing Validation
- **Problem**: The grid-cell detection found any repeated path, but didn't distinguish between:
  - **Stems**: Route goes out and back on the same road (opposite directions) - BAD
  - **Normal overlaps**: Route crosses itself at an intersection - OK
- **Fix**: Added bearing validation in `findRepeatedPaths()`. Now checks if the two passes through the same grid cell have opposite bearings (within 30° of 180°). Only marks as `_isStem: true` if bearings are opposite.

### Issue 3: No Dead-End Waypoint Identification
- **Problem**: When a stem was detected, the code tried removing random waypoints or the one closest to the segment midpoint. But for a stem, the problematic waypoint is at the **dead-end** (the furthest point from start along the stem).
- **Fix**: Added `findDeadEndWaypoint()` method that:
  - Identifies the waypoint furthest from start along the stem direction
  - Returns the waypoint closest to the stem's endpoint
  - Falls back to closest-to-midpoint if no clear dead-end found

### Issue 4: No Relocation Strategy
- **Problem**: The code only tried removing waypoints or adding correction points. It never tried **relocating** the dead-end waypoint to a different road.
- **Fix**: For stems, the code now:
  1. Finds the dead-end waypoint
  2. Calls `snapToRoadAlternates()` to get 3 alternate road positions
  3. Tries relocating the waypoint to each alternate position
  4. Also tries removing it entirely

### Issue 5: Stem Scoring Was Too Lenient
- **Problem**: Stems were scored the same as normal overlaps, so the optimizer didn't prioritize fixing them.
- **Fix**: Added `stemMultiplier: 2` in the scoring function. Stems now count double in the penalty calculation.

## Implementation Details

### Changes to `optimizer.js`

#### 1. Enhanced `findRepeatedPaths()`
```javascript
if (seen.has(keyRev)) {
  // Check if this is actually a stem (opposite directions)
  const prevSeg = seen.get(keyRev);
  const bearing1 = Geometry.bearing({ lat: prevSeg.midLat, lng: prevSeg.midLng }, 
                                     { lat: prevSeg.midLat + prevSeg.dLat, lng: prevSeg.midLng + prevSeg.dLng });
  const bearing2 = Geometry.bearing({ lat: (la + lb) / 2, lng: (lna + lnb) / 2 }, 
                                     { lat: lb, lng: lnb });
  const bearingDiff = Math.abs(bearing1 - bearing2);
  const normalizedDiff = Math.min(bearingDiff, 360 - bearingDiff);
  
  if (normalizedDiff > (180 - CONSTANTS.STEM_BEARING_TOLERANCE)) {
    hits.push({
      i,
      midLat: (la + lb) / 2,
      midLng: (lna + lnb) / 2,
      dLat: lb - la,
      dLng: lnb - lna,
      _isStem: true  // Mark as stem
    });
  }
}
```

#### 2. New `findDeadEndWaypoint()` Method
```javascript
findDeadEndWaypoint(waypoints, stem, start) {
  // Find the waypoint furthest from start along the stem direction
  const stemEnd = { lat: stem.midLat + stem.dLat * 2, lng: stem.midLng + stem.dLng * 2 };
  
  let maxDist = -1;
  let deadEndIdx = -1;
  
  for (let i = 0; i < waypoints.length; i++) {
    const wp = waypoints[i];
    const distFromStart = Geometry.haversine(start, wp);
    const distFromStemEnd = Geometry.haversine(stemEnd, wp);
    
    // Waypoint is likely the dead-end if it's far from start and close to stem end
    if (distFromStemEnd < 5 && distFromStart > maxDist) {
      maxDist = distFromStart;
      deadEndIdx = i;
    }
  }
  
  // If no clear dead-end found, return the waypoint closest to stem midpoint
  if (deadEndIdx === -1) {
    return Geometry.closestWaypointIndex(waypoints, stem.midLat, stem.midLng);
  }
  
  return deadEndIdx;
}
```

#### 3. Enhanced `score()` Method
```javascript
score(geometry, distance) {
  const paths = this.findRepeatedPaths(geometry);
  let totalRepKm = 0;

  for (const p of paths) {
    const weight = p.nearStart ? 3 : 1;
    const stemMultiplier = p._isStem ? 2 : 1;  // Stems count double
    totalRepKm += p.distanceKm * weight * stemMultiplier;
  }

  const repRatio = distance > 0 ? totalRepKm / distance : 0;
  return totalRepKm * CONSTANTS.REPEATED_PATH_WEIGHT +
         repRatio * CONSTANTS.REP_RATIO_WEIGHT +
         distance;
}
```

### Changes to `waypoints.js`

#### Stem-Specific Correction Strategy
```javascript
// For each repeated path (especially stems)
for (const seg of paths) {
  const canPrune = wps.length > CONSTANTS.MIN_WAYPOINTS;

  // If this is a stem, find the dead-end waypoint and try relocating it
  if (seg._isStem) {
    const deadEndIdx = RouteOptimizer.findDeadEndWaypoint(wps, seg, center);
    const deadEndWp = wps[deadEndIdx];
    
    // Try alternate snap positions for the dead-end waypoint
    const alts = await Routing.snapToRoadAlternates(deadEndWp.lat, deadEndWp.lng);
    for (const alt of alts) {
      const relocated = [...wps];
      relocated[deadEndIdx] = alt;
      addCandidate(relocated);
    }
    
    // Try removing the dead-end waypoint entirely
    if (canPrune) {
      const pruned = wps.filter((_, i) => i !== deadEndIdx);
      addCandidate(pruned);
    }
  }

  // ... rest of correction strategies (correction points, brute-force removal)
}
```

### Changes to `constants.js`

```javascript
// Optimizer
GRID_SIZE: 0.0005, // 50m grid cells (was 0.00015 = 15m)
STEM_BEARING_TOLERANCE: 30, // degrees (new)
```

## How It Works Now

### Detection Phase
1. Route geometry is scanned for repeated grid cells (50m cells)
2. For each repeated cell, bearings are calculated for both passes
3. If bearings are opposite (within 30° of 180°), it's marked as a stem
4. Stems are scored with 2x penalty

### Correction Phase
For each detected stem:
1. **Identify dead-end waypoint**: Find the waypoint at the end of the stem
2. **Relocate**: Try 3 alternate road positions for that waypoint
3. **Remove**: Try removing the dead-end waypoint entirely
4. **Correction points**: Generate perpendicular/radial correction points (existing strategy)
5. **Brute-force**: Try removing each waypoint (existing strategy)

All candidates are routed in parallel, and the best-scoring one is selected.

## Expected Improvements

1. **Better detection**: 50m grid cells catch stems even with lane variations
2. **Fewer false positives**: Bearing validation prevents marking normal intersections as stems
3. **Targeted correction**: Dead-end waypoint identification means we fix the actual problem
4. **More options**: Relocation gives the optimizer 3 new candidates per stem
5. **Stronger incentive**: 2x scoring penalty makes the optimizer prioritize fixing stems

## Testing Recommendations

1. Test with routes that have obvious stems (waypoints on dead-end roads)
2. Verify that stems are detected and corrected
3. Check that normal intersections are not incorrectly flagged as stems
4. Monitor performance - the relocation strategy adds 3 candidates per stem, but they're routed in parallel
5. Adjust `STEM_BEARING_TOLERANCE` if needed (30° seems reasonable, but could be tuned)
