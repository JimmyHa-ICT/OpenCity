# Voronoi Sub-Road Generator

An Editor tool that generates an **interior street network** inside a closed-loop main
road, using a procedurally scattered Voronoi diagram, and places it with the same
modular road prefabs (from `Assets/Prefabs`) and connectivity "grammar" as
[the spline-based Road Generator](RoadGenerator.md) and
[the Tilemap Road Generator](TilemapRoadGenerator.md). Where those two tools author a
single road (the loop, or any path), this tool takes an already-closed loop as its
*input* and fills its interior with a organic, cul-de-sac-style network of minor
streets — think city blocks subdividing the inside of a ring road.

- **Script**: `Assets/Editor/VoronoiSubRoadWindow.cs`
- **Geometry**: `Assets/Editor/VoronoiGeometry.cs` (Delaunay triangulation, Voronoi dual,
  polygon tests — no Unity Editor/Spline dependency, pure `Vector2` math)
- **Namespace**: `OpenCity.EditorTools`
- **Menu**: `Tools > OpenCity > Sub-Road Generator (Voronoi)`
- **Requires**: `com.unity.splines` (already in `Packages/manifest.json`)

This tool shares its grammar/placement engine, `Assets/Editor/RoadGridPlacer.cs`, and
the orthogonal cell-bridging helper it uses to rasterize continuous geometry onto the
grid, `RoadGridPlacer.WalkOrthogonalBridged`, with the spline-based Road Generator.

## A key design choice: sub-roads never connect to the main loop

This tool's output — a `SubRoads` object by default — is **fully independent** from
whatever the main Road Generator already placed (typically a `Road` object). It never
reads, unions, or otherwise touches the main loop's own occupied cells; it only reads
the main loop's *shape* (as a spline), to know where its interior area is. Generated
sub-roads are always kept at least **Boundary Margin** away from the loop boundary — by
design, there is no T-junction where a sub-road meets the main road. `Clear` on this
tool only ever removes `SubRoads` (by name), never `Road`.

## Quick start

1. Make sure you have a **closed** `Spline` forming the main loop — the same kind of
   object [the spline Road Generator](RoadGenerator.md) already uses (e.g. the scene's
   `Spline` GameObject). It doesn't need to have been used to generate an actual `Road`
   first; only its shape matters here.
2. Open `Tools > OpenCity > Sub-Road Generator (Voronoi)`.
3. Click **Use Selected GameObject** (with the loop spline selected in the Hierarchy),
   or drag the `SplineContainer` into the **Spline Container** field directly.
4. Tune **Site Count**, **Min Site Spacing**, and **Boundary Margin** to taste (the
   defaults are a reasonable starting point for a loop roughly 10–20 units across) and
   watch the live preview box update.
5. Leave the **Shapes** fields on their defaults, or swap in cosmetic siblings.
6. Click **Generate**.

A GameObject named `SubRoads` (configurable) appears in the scene, containing one
prefab instance per generated cell. Don't like the layout? Change **Random Seed** and
click **Generate** again — the whole network re-rolls deterministically from the new
seed.

## How it works

1. **Polygon sampling**: the loop spline is sampled at a fixed arc-length step
   (**Polygon Sample Step**) into a continuous `List<Vector2>` polygon in world XZ —
   the same `SplineUtility.Evaluate`/`GetPointAtLinearDistance` technique the spline
   Road Generator uses to rasterize onto a grid, but collecting continuous points here
   instead of grid cells, since this polygon is used for geometric tests (point-in-polygon,
   distance-to-boundary), not placed directly.
2. **Deterministic site scattering**: candidate points are drawn from the polygon's
   bounding box and accepted only if they land inside the polygon, at least
   **Boundary Margin** from its boundary, and at least **Min Site Spacing** from every
   already-accepted site — rejection sampling, up to **Site Count** accepted sites or a
   generous attempt cap, whichever comes first. This uses a `System.Random` seeded
   fresh from **Random Seed** for every call — never Unity's global `UnityEngine.Random`
   — so the exact same settings always scatter the exact same sites. See "Determinism &
   seed" below.
3. **Delaunay triangulation**: the accepted sites are triangulated with the standard
   Bowyer-Watson incremental algorithm — build an oversized "super-triangle" around all
   the sites, insert each site one at a time (removing every triangle whose circumcircle
   contains it and re-triangulating the resulting hole), then strip away every triangle
   still touching a super-triangle vertex.
4. **Voronoi dual, interior edges only**: for every pair of triangles sharing an edge,
   a Voronoi road segment is drawn between their two circumcenters. An edge belonging to
   only *one* triangle sits on the outer hull of the triangulation — it would need to be
   extended as an unbounded ray to form a complete Voronoi cell, but this tool has no use
   for that (sub-roads must stay inset from the loop regardless), so hull edges are
   simply dropped, not extended. See "What is a Voronoi diagram?" below if this
   description doesn't mean much yet.
5. **Interior clipping**: each kept Voronoi segment is sampled at a fixed world-space
   step (**Edge Sample Step**); only the *maximal runs* of samples that stay inside the
   polygon **and** at least **Boundary Margin** from its boundary are kept. A run never
   bridges across a sample that falls outside that interior band — a segment briefly
   leaving the safe interior zone becomes a real gap, not a staircase cutting through the
   excluded margin.
6. **Grid rasterization + placement**: each interior sample converts to a grid cell with
   the same `WorldToCell` formula the spline Road Generator uses (so a `SubRoads`
   network sits on the exact same absolute lattice as `Road`, even though the two are
   never merged), bridged into orthogonally-adjacent runs via
   `RoadGridPlacer.WalkOrthogonalBridged`. Every sub-run's cells are pooled, deduplicated,
   and sorted for reproducible ordering, then handed to
   `RoadGridPlacer.BuildGraph`/`PlaceCells` — the exact same classify → solve-rotation →
   correct-pivot → instantiate → undo-safe-clear pipeline the other two tools use. See
   [Docs/RoadGenerator.md#how-it-works](RoadGenerator.md#how-it-works) for that part in
   full detail; it isn't repeated here.

## What is a Voronoi diagram?

Scatter a handful of points ("sites") inside the loop. Every location inside the loop
"belongs" to whichever site is nearest to it — imagine each site claiming its own little
territory. The **Voronoi diagram** is the set of boundary lines between one site's
territory and its neighbors': draw roads along those boundaries and you get an organic,
irregular street pattern — not a rigid grid, more like how real neighborhoods often
subdivide unevenly around a few landmarks.

The **Delaunay triangulation** is the mathematical mirror image of this: it connects
every pair of sites whose territories actually touch. This tool builds the Voronoi
boundaries by walking that triangulation's edges (each shared edge's two triangles
"point at" the boundary between the two sites on either side of it) rather than
constructing each site's bounded territory polygon directly — see "Delaunay-dual
segments, not true bounded cells" in Limitations for what that trades away.

## Determinism & seed

**Random Seed**, together with every other field (the loop spline's current shape,
Site Count, Min Site Spacing, Boundary Margin, the sample-step fields, and the resolved
cell size), fully determines the network: the whole pipeline — site scattering,
triangulation, the Voronoi dual, clipping, rasterization — is a pure function of these
serialized values, computed by one method (`BuildNetwork`) that both the live preview
box and the actual `Generate` call, unchanged, every time. There is no second code path
that could quietly drift from the first. Concretely, this means:

- The live preview box always shows *exactly* what `Generate` will produce for the
  current field values — never an approximation.
- Re-running `Generate` with the same **Random Seed** and the same other settings always
  reproduces the identical cell layout, byte for byte.
- Changing **Random Seed** alone re-rolls the site scattering (and therefore the whole
  downstream network) to a new, still-deterministic layout — the standard way to
  "try another layout" with this tool.

## Window reference

### Loop
| Field | Description |
|---|---|
| Spline Container | The `SplineContainer` whose spline forms the main loop. **Must be Closed** — an open spline has no interior and is rejected. |
| Use Selected GameObject | Grabs the `SplineContainer` off the currently selected Hierarchy object. |
| Spline Index | Only shown when the container has more than one spline; picks which one to follow. |

### Voronoi
| Field | Description |
|---|---|
| Site Count | How many Voronoi sites to scatter inside the loop. More sites make a denser network of smaller blocks. Rejection sampling may accept fewer than requested if Min Site Spacing/Boundary Margin leave too little room — reported in the preview, never silently relaxed. |
| Min Site Spacing | Minimum world-space distance between accepted sites — keeps blocks from collapsing to near-zero size. |
| Boundary Margin | Minimum distance both sites and rasterized road cells must stay from the main loop's boundary. This is what enforces "sub-roads never connect to the main loop." |
| Random Seed | Same seed + same settings always scatters the same sites and produces the same network. Change it to re-roll the layout. |
| Polygon Sample Step | Arc-length step used to sample the loop spline into the polygon used for site scattering and clipping. Lower = more faithful to a tightly-curved loop, at the cost of a larger sample count. |
| Edge Sample Step | World-space step used to sample each Voronoi edge when rasterizing it onto the grid. Lower = more faithful clipping at the Boundary Margin, especially where an edge grazes it at a shallow angle. |

### Grid
| Field | Description |
|---|---|
| Cell Size Override | Force a grid cell size instead of auto-measuring the Straight prefab's renderer bounds along X. `0` = auto. |
| Ground Y | World Y every placed tile sits at. This is a **flat-grid generator** — there is no per-cell height or ramp support. |

### Shapes and Placement
Identical fields to the other two tools — see
[Docs/RoadGenerator.md](RoadGenerator.md#shapes) (Dead-End / Straight / Bend /
T-Junction / Four-Way prefab slots) and its Placement section (Parent Container). The
one difference: **Parent Name** defaults to `SubRoads`, not `Road`, and Generate/Clear
only ever look up and touch that name — never whatever the main Road Generator produced.

### Live preview
Below the fields, a read-only box reports (recomputed via the same `BuildNetwork` call
`Generate` uses — see "Determinism & seed"): the sampled polygon's vertex count and loop
length; accepted vs. requested site count; Delaunay triangle count; kept vs. dropped
Voronoi edge count (broken down into hull-boundary drops and runaway-circumcenter drops);
and the final cell count broken down per shape category, plus any skipped-cell count.
Non-blocking warnings cover: too few sites accepted, zero interior room left by the
current Boundary Margin, zero road segments surviving clipping, and a non-identity
rotation/scale on the loop spline's transform (same warning as the other tools, and for
the same reason — it desyncs the grid axes from world X/Z).

### Buttons
- **Generate** — disabled until settings are valid (missing/open spline, missing
  required prefab, Site Count below 3, negative spacing/margin, unresolvable cell size —
  these show as a warning in the preview box instead). Clears and rebuilds the named
  parent, and logs a one-line summary to the Console.
- **Clear** — deletes the generated parent (by name) without regenerating.

## Limitations

- **Stays interior only, by design** — this tool never draws a T-junction or any other
  connection into the main loop; every sub-road cell is inset by at least Boundary
  Margin. If you want interior streets that actually connect to the ring road, that's
  intentionally out of scope for this tool (see "A key design choice" above).
- **Delaunay-dual segments, not true bounded Voronoi cells** — this tool draws the
  segment between each pair of adjacent triangles' circumcenters, but never constructs
  each site's full bounded territory polygon, and simply drops (rather than extends as a
  ray) any edge on the triangulation's outer hull. This is a deliberate simplification:
  since sub-roads must stay inset from the loop boundary anyway, an unbounded ray would
  always get clipped away in step 5 of "How it works" regardless of whether it was ever
  constructed — so building it would cost real complexity for zero visible difference in
  the output.
- **Rejection-sampling can under-deliver the requested Site Count** for a tight
  Min Site Spacing/Boundary Margin relative to the loop's interior area. This is
  reported in the preview, never silently relaxed or auto-corrected — lower one of those
  two fields, or reduce Site Count, if you see this warning.
- **No self-intersection handling**: the loop polygon is assumed simple (non-self-crossing).
  A self-intersecting spline will still produce *some* result rather than crashing, but
  it isn't a validated or supported input shape.
- Shares the same underlying-engine limitations as the other two tools (see
  [Docs/RoadGenerator.md#safety--limitations](RoadGenerator.md#safety--limitations)):
  1×1-footprint pieces only, cosmetic-variant slots are manual (the tool never auto-picks
  between kit siblings), flat grid only (no ramp/slope integration), name-based
  Generate/Clear lookup, and the whole Generate (including the implicit clear of a prior
  run) is one undo-safe group.

## Files touched by this feature
- `Assets/Editor/VoronoiSubRoadWindow.cs` — the tool (UI, spline→polygon sampling, pipeline orchestration).
- `Assets/Editor/VoronoiGeometry.cs` — the geometry/algorithm layer (polygon tests, deterministic site scattering, Bowyer-Watson Delaunay triangulation, Voronoi dual).
- `Assets/Editor/RoadGridPlacer.cs` — shared grammar/placement engine, plus the newly-promoted `WalkOrthogonalBridged` helper (also used by the spline Road Generator).
- `Docs/VoronoiSubRoadGenerator.md` — this document.
