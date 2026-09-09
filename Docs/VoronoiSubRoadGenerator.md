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
grid, `RoadGridPlacer.WalkOrthogonalBridged`, with the spline-based Road Generator. It
additionally shares `Assets/Editor/SubRoadNetworkShared.cs` — spline→polygon sampling,
loop rasterization, interior-clipped segment rasterization, and the whole main-road
connection mechanic described below — with
[the BSP Sub-Road Generator](BspSubRoadGenerator.md), the rectilinear alternative to this
tool: same job and same connection behavior, but recursive axis-aligned rectangle splitting
instead of a Voronoi diagram, so its streets never staircase. The two are drop-in
alternatives to each other (both default to the same `SubRoads` parent name); they differ
*only* in how the network's geometry is decided.

## A key design choice: sub-roads connect wherever they touch the main loop

This tool's output — a `SubRoads` object by default — is **its own independent
GameObject** from whatever the main Road Generator already placed (typically a `Road`
object): its own Clear, its own Generate, its own parent name/container, never confused
with `Road`. But the two networks are not otherwise kept apart. Generated sub-roads are
kept inset from the loop boundary by **Boundary Margin**, and wherever that margin lets
a sub-road cell end up orthogonally adjacent to a main-road cell, that's a deliberate
connection point: Generate opens the sub-road side toward it *and* replaces the actual
placed `Road` tile at that cell with a connector piece (a bend/T-junction/four-way,
whichever the combined connectivity resolves to), so the join reads as one continuous
network rather than a network butting into a solid wall. See "Connecting to the main
road" in "How it works" below for exactly how a connection is found and applied. As a
hard backstop *unrelated* to connections, the loop shape is also rasterized onto the
same grid the Road Generator would use for it (see "Main-loop overlap exclusion" below),
so a sub-road cell can never land on the exact same cell a main-road cell already
occupies — connections join two adjacent cells, they never merge two into one.
`Clear` on this tool only ever removes `SubRoads` (by name), never `Road` — connecting
tiles into `Road` happens only as part of Generate, never Clear.

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
   watch both the live preview box *and* the Scene view update — colored, oriented
   squares show exactly where each dead-end/straight/bend/T-junction/four-way piece will
   land, before you commit to Generate.
5. Don't like the layout? Click **Reroll** instead of hand-editing Random Seed. Like
   what you see? Click **Lock** to pin it — you can then freely tweak Ground Y or which
   prefab is assigned without the layout itself changing underneath you.
6. Leave the **Shapes** fields on their defaults, or swap in cosmetic siblings.
7. Click **Generate**.

A GameObject named `SubRoads` (configurable) appears in the scene, containing one
prefab instance per generated cell, matching exactly what was shown in the Scene view
(the locked layout if locked, otherwise whatever the live fields currently produce).

## How it works

1. **Polygon sampling**: the loop spline is sampled at a fixed arc-length step
   (**Polygon Sample Step**) into a continuous `List<Vector2>` polygon in world XZ —
   the same `SplineUtility.Evaluate`/`GetPointAtLinearDistance` technique the spline
   Road Generator uses to rasterize onto a grid, but collecting continuous points here
   instead of grid cells, since this polygon is used for geometric tests (point-in-polygon,
   distance-to-boundary), not placed directly.
2. **Deterministic site scattering**: candidate points are drawn from the polygon's
   bounding box and, if **Site Grid Bias** is above `0`, first nudged toward the nearest
   point on an implicit world-X/Z-aligned lattice (spacing **Site Grid Spacing**, or
   **Min Site Spacing** if that's `0`) by that bias amount — before any acceptance test
   runs. This makes neighboring sites more likely to end up sharing an X or Y coordinate,
   which is what makes the Voronoi edge between them land near-cardinal instead of at an
   arbitrary skew (see "Simplify Staircases" in step 9 for the complementary fix, which
   cleans up whatever skew remains after rasterization). At the default **Site Grid
   Bias** of `0` this nudge is skipped entirely, not just a no-op. Candidates are then
   accepted only if they land inside the polygon, at least **Boundary Margin** from its
   boundary, and at least **Min Site Spacing** from every already-accepted site —
   rejection sampling, up to **Site Count** accepted sites or a generous attempt cap,
   whichever comes first. This uses a `System.Random` seeded fresh from **Random Seed**
   for every call — never Unity's global `UnityEngine.Random` — so the exact same
   settings always scatter the exact same sites. See "Determinism & seed" below.
3. **Optional anisotropic stretch**: if **Stretch Factor** is above `1`, the accepted
   sites (and the polygon, for the bounding-radius check in step 4) are temporarily
   warped into a squeezed coordinate space — compressed along **Stretch Angle**,
   expanded perpendicular to it, pivoting on the polygon's centroid
   (`VoronoiGeometry.WarpAnisotropic`) — before triangulation, and the resulting Voronoi
   segments are mapped back with the exact inverse (`VoronoiGeometry.UnwarpAnisotropic`)
   right after step 4. A circle computed in that squeezed space becomes an ellipse once
   unwarped, which is what makes the final blocks elongate along **Stretch Angle**
   instead of staying uniformly round. Site scattering (step 2) and interior clipping
   (step 5) both still operate on the real, un-warped world polygon, so **Min Site
   Spacing** and **Boundary Margin** remain true world-space distances regardless of
   Stretch Factor. At the default Stretch Factor of `1` this step is skipped entirely
   (not just a no-op mathematically, but literally bypassed) so existing scenes keep
   regenerating byte-for-byte identical to before this field existed.
4. **Delaunay triangulation**: the (possibly warped) sites are triangulated with the
   standard Bowyer-Watson incremental algorithm — build an oversized "super-triangle"
   around all the sites, insert each site one at a time (removing every triangle whose
   circumcircle contains it and re-triangulating the resulting hole), then strip away
   every triangle still touching a super-triangle vertex.
5. **Voronoi dual, interior edges only**: for every pair of triangles sharing an edge,
   a Voronoi road segment is drawn between their two circumcenters. An edge belonging to
   only *one* triangle sits on the outer hull of the triangulation — it would need to be
   extended as an unbounded ray to form a complete Voronoi cell, but this tool has no use
   for that (sub-roads must stay inset from the loop regardless), so hull edges are
   simply dropped, not extended. See "What is a Voronoi diagram?" below if this
   description doesn't mean much yet.
6. **Interior clipping**: each kept Voronoi segment is sampled at a fixed world-space
   step (**Edge Sample Step**); only the *maximal runs* of samples that stay inside the
   polygon **and** at least **Boundary Margin** from its boundary are kept. A run never
   bridges across a sample that falls outside that interior band — a segment briefly
   leaving the safe interior zone becomes a real gap, not a staircase cutting through the
   excluded margin.
7. **Main-loop overlap exclusion (hard safety net)**: separately from Boundary Margin,
   the loop polygon itself is rasterized onto the same grid — mirroring exactly how the
   spline-based Road Generator would rasterize it — to get the set of cells the main
   loop's own road occupies. Any sub-road cell that lands on one of those cells outright
   is dropped before Generate ever runs, regardless of how Boundary Margin is set.
   Boundary Margin is a continuous-distance heuristic sampled *along* each edge and only
   then rounded to a grid cell — that rounding step (plus the unchecked staircase
   bridging used to fill gaps between samples) can still let a cell coincide with the
   main loop's footprint when Margin is tight relative to Cell Size, without this step.
   Any drop is reported in the live preview, never silent — see "Live preview" below.
8. **Grid rasterization**: each interior sample converts to a grid cell with the same
   `WorldToCell` formula the spline Road Generator uses (so a `SubRoads` network sits on
   the exact same absolute lattice as `Road`), bridged into orthogonally-adjacent runs
   via `RoadGridPlacer.WalkOrthogonalBridged`. Every sub-run's cells are pooled,
   deduplicated, and sorted for reproducible ordering.
9. **Simplify Staircases (optional cleanup)**: when enabled, the pooled cell set from
   step 8 is rewritten by `RoadGridPlacer.SimplifyStaircases` before anything downstream
   sees it. Since every tile in this kit only rotates in 4 cardinal steps (there is no
   diagonal/45° piece at all), any Voronoi edge that isn't already close to horizontal or
   vertical necessarily staircases when it hits the grid — this step doesn't remove that
   fundamental limit, it cleans up its worst symptoms:
   - the graph is decomposed into "chains" — maximal runs of cells with exactly 2
     occupied neighbors, running between two *anchors* (a dead-end/T-junction/four-way
     cell, or any cell already adjacent to a main-loop cell from step 7 — anchors are
     never moved or removed, which is what guarantees step 11 below never loses a
     connection candidate to this pass);
   - a chain whose two endpoints already share a row or column, and whose interior cells
     never wander more than **Straighten Tolerance (cells)** off that shared line, is
     replaced with a single flat run — this is the classic "one stray cell wobbling an
     otherwise-straight run" artifact;
   - a chain whose endpoints don't share a row/column, but where one axis clearly
     dominates the other (roughly steeper than ~18° off either cardinal axis), is
     collapsed to a single corner — the fewest bends geometrically possible once the
     endpoints themselves can't move;
   - a chain that's instead reasonably *balanced* between both axes (closer to a genuine
     diagonal) is left exactly as rasterized: the raw staircase for an actual diagonal
     segment is already an even, deterministic Bresenham-style pattern that reads as an
     intentional diagonal street, and forcing it into one sharp elbow would look worse,
     not better;
   - every proposed replacement cell must still land inside the interior/Boundary Margin
     band and must not collide with any other cell (including the main loop's own,
     checked against `mainRoadCells` from step 7) — a chain whose replacement would
     violate either is left untouched rather than partially applied, and two chains that
     would both claim the same new cell are both left untouched rather than arbitrated,
     keeping the result a pure function of the seed with no enumeration-order dependence.

   Off by default; an existing scene keeps regenerating byte-for-byte identical until
   **Simplify Staircases** is turned on. Any count is reported in the live preview, never
   silent — see "Live preview" below.
10. **Connecting to the main road**: for every final sub-road cell, each of its four
   orthogonal neighbors is checked against the main-loop cell set from step 7. Wherever
   one matches, that's a *candidate* connection - recorded as an extra open-direction bit
   for the sub-road cell (facing the main road) and the opposite bit for the main-road
   cell (facing the sub-road). Each candidate is then looked up against the actual `Road`
   object in the scene (**Main Road Parent Name**/**Main Road Parent Container** below):
   the child instance nearest the candidate main-road cell's position (within a
   generous per-cell tolerance, since an off-center-pivot piece like `road-bend` isn't
   placed exactly at its cell center) becomes that connection's target. A candidate with
   no `Road` object found, or no instance found near it, is dropped - left completely
   unconnected on both sides, exactly as if Boundary Margin had kept it apart - rather
   than half-wiring one side only. This lookup happens as part of the same `BuildNetwork`
   call the live preview uses (not only at Generate time), which is what keeps the
   preview and Generate in agreement about exactly which connections will land - see
   "Determinism & seed" below for why that guarantee matters here specifically.
11. **Placement**: the sub-road cells (each connecting cell's mask now including its
    extra bit from step 10) are handed to `RoadGridPlacer.BuildGraph`/`PlaceCells` - the
    exact same classify → solve-rotation → correct-pivot → instantiate → undo-safe-clear
    pipeline the other two tools use (see
    [Docs/RoadGenerator.md#how-it-works](RoadGenerator.md#how-it-works) for that part in
    full detail; it isn't repeated here) - producing the `SubRoads` object. Separately,
    for each *applied* connection, the corresponding `Road` tile found in step 10 is
    destroyed and replaced with a freshly-resolved piece: its base mask (self-only
    connectivity among the step-7 main-loop cells, i.e. what Road Generator itself would
    have classified it as) OR'd with the extra bit(s) from every connection touching it,
    reclassified, rotated, and instantiated using *this tool's own* Shapes prefabs (not
    Road Generator's - a connector piece has to visually match the sub-road kit for the
    join to read as one continuous network). Both the `SubRoads` placement and every
    replaced `Road` tile are wrapped in one Undo group, so **Ctrl+Z** undoes an entire
    Generate - sub-road placement and main-road tile swaps together - in a single step.

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
Site Count, Min Site Spacing, Site Grid Bias/Spacing, Boundary Margin, the sample-step
fields, the resolved cell size, and Simplify Staircases/Straighten Tolerance), fully
determines the network: the whole pipeline — site scattering, triangulation, the Voronoi
dual, clipping, rasterization, the optional cleanup pass — is a pure function of these
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
| Site Grid Bias | `0` = fully random site scattering (default, unchanged). Higher values pull each candidate site toward an implicit alignment grid before acceptance tests run, biasing Voronoi edges toward horizontal/vertical so they rasterize with less staircasing. `1` = sites snap fully onto the grid (blocks become rigid). |
| Site Grid Spacing | Spacing of the implicit alignment grid Site Grid Bias pulls candidates toward. `0` = auto (uses Min Site Spacing). Only matters while Site Grid Bias is above `0`. |
| Boundary Margin | Minimum distance both sites and rasterized road cells must stay from the main loop's boundary. Wherever it lets a sub-road cell end up touching a main-road cell, that becomes a connection point (see "Main Road Connection" below) - lower it to create more of them, raise it to keep the network further inset. |
| Random Seed | Same seed + same settings always scatters the same sites and produces the same network. Change it to re-roll the layout. |
| Polygon Sample Step | Arc-length step used to sample the loop spline into the polygon used for site scattering and clipping. Lower = more faithful to a tightly-curved loop, at the cost of a larger sample count. |
| Edge Sample Step | World-space step used to sample each Voronoi edge when rasterizing it onto the grid. Lower = more faithful clipping at the Boundary Margin, especially where an edge grazes it at a shallow angle. |
| Stretch Angle | Direction (degrees, 0-180 — a stretch axis has no front/back) interior blocks are elongated along. Has no visible effect while Stretch Factor is `1`. |
| Stretch Factor | `1` = normal organic blocks (default, and mathematically an exact no-op regardless of Stretch Angle). Values above `1` elongate blocks along Stretch Angle — e.g. to mimic long blocks along a dominant avenue direction. Typical useful range 1-4. |

### Grid
| Field | Description |
|---|---|
| Cell Size Override | Force a grid cell size instead of auto-measuring the Straight prefab's renderer bounds along X. `0` = auto. |
| Ground Y | World Y every placed tile sits at. This is a **flat-grid generator** — there is no per-cell height or ramp support. |

### Cleanup
| Field | Description |
|---|---|
| Simplify Staircases | Post-rasterization cleanup (see step 9 in "How it works"): collapses short zigzag jogs between junctions into a cleaner straight run or a single corner, without moving any dead-end/junction/main-road connection cell. **Off by default** — existing scenes regenerate identically until enabled. |
| Straighten Tolerance (cells) | How many cells of lateral drift along an otherwise-straight run get snapped flat instead of kept as a real bend. Higher values straighten more aggressively (can also flatten intentional gentle curves). Only matters while Simplify Staircases is on. |

### Shapes and Placement
Identical fields to the other two tools — see
[Docs/RoadGenerator.md](RoadGenerator.md#shapes) (Dead-End / Straight / Bend /
T-Junction / Four-Way prefab slots) and its Placement section (Parent Container). The
one difference: **Parent Name** defaults to `SubRoads`, not `Road`, and Generate/Clear
only ever look up and touch that name for `SubRoads` itself — see "Main Road Connection"
just below for the one place Generate does read/modify the main Road.

### Main Road Connection
| Field | Description |
|---|---|
| Main Road Parent Name | Name of the already-generated main Road GameObject to connect into. Matches Road Generator's own **Parent Name** field - default `Road`. |
| Main Road Parent Container | Optional transform the main Road GameObject is nested under. Leave empty for scene root - must match whatever Road Generator was set to when `Road` was generated. |

If the named object isn't found, or a specific candidate connection has no `Road` tile
instance near it, that connection (and only that one) is silently skipped - both sides
stay unconnected, exactly as if Boundary Margin had kept them apart - and the preview/
Generate log a warning rather than failing outright. See step 10, "Connecting to the main
road," above for exactly how a connection is found.

### Live preview
Below the fields, a read-only box reports (recomputed via the same `BuildNetwork` call
`Generate` uses — see "Determinism & seed"): the sampled polygon's vertex count and loop
length; accepted vs. requested site count; Delaunay triangle count; kept vs. dropped
Voronoi edge count (broken down into hull-boundary drops and runaway-circumcenter drops);
and the final cell count broken down per shape category (already reflecting any applied
main-road connection's extra open direction and any Simplify Staircases rewrite - the
count box never shows a classification Generate wouldn't actually place), plus any
skipped-cell count. When one or more connections will be applied, an extra line reports
how many sub-road cells connect into how many main-road tiles. When Simplify Staircases
is on and simplified at least one run, an extra line reports how many.

Non-blocking warnings cover: too few sites accepted, zero interior room left by the
current Boundary Margin, zero road segments surviving clipping, one or more cells
dropped because they exactly coincided with the main loop's own road cells (see
"Main-loop overlap exclusion" above — raise Boundary Margin to stop this from
triggering), the named main Road object not found when there are candidate connections
waiting for it, one or more candidate connections with no matching Road tile nearby, and
a non-identity rotation/scale on the loop spline's transform (same warning as the other
tools, and for the same reason — it desyncs the grid axes from world X/Z). A separate
info box (not a warning — expected at tight settings, not a problem) reports whenever
Simplify Staircases left one or more runs untouched because straightening/collapsing
them would have crossed Boundary Margin, the main road, or another run.

### Scene-view preview, Reroll & Lock
Alongside the text preview box, the Scene view draws the same network live: the sampled
loop polygon (white outline), accepted sites (green dots), and — most usefully — every
grid cell as a flat, colored, correctly-rotated square matching the piece Generate would
actually place there:

| Color | Category |
|---|---|
| Red | Dead-end |
| Light gray | Straight |
| Yellow | Bend |
| Orange | T-junction |
| Cyan | Four-way |
| Magenta wireframe | Unclassifiable (skipped, same as Generate would skip it) |
| White ring | A main-road cell that will be replaced with a connector piece |

A **Show Raw Voronoi Edges** toggle additionally draws the pre-rasterization Voronoi
segments (thin, translucent blue) — the geometry the grid cells were derived from,
useful for understanding *why* a cell ended up where it did. With Simplify Staircases
on, these blue segments can now visibly diverge from the drawn grid squares wherever a
run was straightened or corner-collapsed — that's expected, not a desync bug; the blue
line is always the *pre-cleanup* geometry, the squares are always the final result.

Whenever **Stretch Factor** is above `1`, a pale line is drawn through the polygon's
centroid along **Stretch Angle**, spanning the polygon's bounding radius — a visual
protractor for calibrating the angle against the loop's actual shape while dragging the
slider, rather than guessing at a bare number.

Two controls sit next to this:
- **Reroll** (next to Random Seed) — randomizes Random Seed and re-previews, so you don't
  have to hand-type new seed values to try a different layout. Disabled while locked.
- **Lock / Unlock** — freezes the currently-previewed network (including its resolved
  cell size) so it stops recomputing from live field values. While locked:
  - **Generate always places exactly the locked network** — never a value that could have
    silently drifted from what you last saw. This is the core guarantee: what you
    previewed is what you get.
  - Ground Y, the five Shapes prefab slots, Parent Name, and Parent Container all stay
    live and fully editable — they affect *where*/*what* gets placed, not *the layout
    itself*, so locking deliberately leaves them alone.
  - If you then change a geometry-affecting field (Site Count, spacing, margin, the loop
    spline, sample steps, or a prefab swap that changes the resolved cell size), an info
    box reports the locked network is now stale relative to those fields — Generate will
    still use the locked layout regardless, until you Unlock or Reroll.
  - Unlock returns to always-live preview; Reroll is unavailable until you unlock, since
    silently re-rolling a locked, "what you see is what you get" layout would defeat the
    point of locking it.
  - The locked snapshot also freezes exactly which main-road connections were found and
    which `Road` tile instances they target, as of the moment Lock was clicked. If
    `Road` is deleted or regenerated after locking, those connections can no longer be
    applied - Generate logs a warning and skips them rather than erroring, but the fix
    is to Unlock (or Reroll) so a fresh `BuildNetwork` call re-finds the current `Road`
    tiles.

Locking is a preview cache in memory only — it does not survive a script recompile or
closing the window; both silently return you to the live preview, by design (this
avoids serializing tuple-based intermediate geometry data for what is, ultimately, just
a preview convenience).

### Buttons
- **Generate** — disabled until settings are valid (missing/open spline, missing
  required prefab, Site Count below 3, negative spacing/margin, unresolvable cell size —
  these show as a warning in the preview box instead), *unless the preview is locked*, in
  which case Generate remains available and places the locked network regardless of what
  the live fields currently say. Clears and rebuilds the named `SubRoads` parent,
  replaces every applied connection's `Road` tile (both wrapped in one Undo group), and
  logs a one-line summary to the Console including how many main-road tiles were
  replaced.
- **Clear** — deletes the `SubRoads` parent (by name) without regenerating. Never
  touches `Road`, even if a previous Generate replaced tiles in it — undo that
  particular Generate (Ctrl+Z) if you need those specific tiles back.

## Limitations

- **Connections require an already-generated `Road`** — a candidate connection can only
  be applied if the main Road object already exists in the scene at **Main Road Parent
  Name**/**Main Road Parent Container** with a tile instance near the expected cell;
  otherwise it's silently left unconnected (with a console warning). Generate the main
  Road first if you want connections to actually land.
- **Connector pieces come from this tool's own Shapes, not Road Generator's** — a
  replaced `Road` tile is instantiated from *this* window's Dead-End/Straight/Bend/
  T-Junction/Four-Way slots, on the assumption the main road and sub-road network use
  the same kit. If you deliberately used different prefab variants for `Road` and
  `SubRoads`, the replaced tile will match the sub-road kit, not the rest of `Road`.
- **Tile lookup is position-based, not identity-based** — a connecting `Road` tile is
  found by nearest position (within a generous per-cell tolerance), not by any stored
  reference to what Road Generator placed. A cell size mismatch between the two tools,
  or a `Road` sparser than the loop it was generated from, can make a candidate
  connection find no match (skipped, not misapplied elsewhere).
- Same exact-cell overlap exclusion as before still applies regardless of connections:
  any sub-road cell that would land on the main loop's own rasterized footprint is
  hard-dropped (see "Main-loop overlap exclusion" in "How it works") - connections join
  two *adjacent* cells, they never merge two into one.
- **Simplify Staircases only ever produces 0 or 1 corner per chain, by design** — this
  tool's kit has no diagonal/45° piece at all, so a genuinely diagonal Voronoi edge can
  never be represented as a smooth curve either way; the cleanup pass' job is limited to
  removing spurious noise and unnecessary fragmentation, not eliminating staircasing
  outright, which isn't possible with only 4 tile rotations.
- **Simplify Staircases is in some tension with a strongly non-cardinal Stretch Angle** —
  Stretch Angle/Factor deliberately elongates blocks along a non-cardinal direction;
  cleanup's "leave alone if balanced diagonal" rule protects an evenly-staircased result
  from being flattened, but a Stretch Angle far from a multiple of 90° combined with a
  loose Straighten Tolerance can still partially square off what Stretch Angle was
  trying to elongate. Not code-gated - a matter of taste, tune Straighten Tolerance down
  (or leave Simplify Staircases off) if you want the diagonal look preserved exactly.
- **Site Grid Bias trades organic variety for cardinal-friendliness** — a high bias
  pulls sites toward a regular lattice, which can make blocks look more rectangular/
  regimented than the fully-random default, and (like a tight Min Site Spacing) can
  increase how often Site Count is under-delivered at high bias combined with a tight
  Min Site Spacing relative to the loop's interior area.
- **A chain that loops back to its own anchor with no other anchor in between (a closed
  ring hanging off one junction) is left untouched by Simplify Staircases**, and a fully
  closed ring with *no* anchor at all anywhere on it is also left untouched - both are
  expected to be rare for this tool's tree-like Voronoi-dual output, and are a
  deliberately conservative default rather than an attempt to simplify a topology the
  algorithm wasn't designed to rewrite.
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
- `Assets/Editor/VoronoiSubRoadWindow.cs` — the tool (UI, pipeline orchestration, Scene-view preview).
- `Assets/Editor/VoronoiGeometry.cs` — the geometry/algorithm layer (polygon tests, deterministic site scattering, Bowyer-Watson Delaunay triangulation, Voronoi dual).
- `Assets/Editor/SubRoadNetworkShared.cs` — spline→polygon sampling, loop rasterization, interior-clipped segment rasterization, and the main-road connection mechanic, shared with [the BSP Sub-Road Generator](BspSubRoadGenerator.md).
- `Assets/Editor/RoadGridPlacer.cs` — shared grammar/placement engine, plus the newly-promoted `WalkOrthogonalBridged` helper (also used by the spline Road Generator) and `SimplifyStaircases` (this tool only, so far).
- `Docs/VoronoiSubRoadGenerator.md` — this document.
