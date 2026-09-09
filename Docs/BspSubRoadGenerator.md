# BSP Sub-Road Generator

An Editor tool that generates an **interior street network** inside a closed-loop main road by
recursively splitting the loop's bounding rectangle with axis-aligned cuts (Binary Space
Partitioning), and places it with the same modular road prefabs (from `Assets/Prefabs`) and
connectivity "grammar" as [the spline-based Road Generator](RoadGenerator.md), [the Tilemap Road
Generator](TilemapRoadGenerator.md), and [the Voronoi Sub-Road Generator](VoronoiSubRoadGenerator.md).

- **Script**: `Assets/Editor/BspSubRoadWindow.cs`
- **Geometry**: `Assets/Editor/BspGeometry.cs` (recursive rectangle splitting — no Unity
  Editor/Spline/polygon dependency, pure `Vector2`/`Rect` math)
- **Namespace**: `OpenCity.EditorTools`
- **Menu**: `Tools > OpenCity > Sub-Road Generator (BSP)`
- **Requires**: `com.unity.splines` (already in `Packages/manifest.json`)

This tool shares its grammar/placement engine, `Assets/Editor/RoadGridPlacer.cs`, with all three
other road tools, and shares `Assets/Editor/SubRoadNetworkShared.cs` — spline→polygon sampling,
loop rasterization, interior-clipped segment rasterization, and the entire main-road connection
mechanic — with the Voronoi Sub-Road Generator specifically. The two sub-road tools differ
*only* in how they decide the network's geometry; everything on either side of that decision is
literally the same code.

## A key design choice: axis-aligned cuts, so nothing staircases

This is the rectilinear alternative to the Voronoi tool. Where a Voronoi edge is the perpendicular
bisector between two randomly-scattered sites — and therefore lands at an arbitrary angle, which
*must* staircase once rasterized onto a grid whose tiles only rotate in 4 cardinal steps — a BSP
cut is by construction exactly horizontal or vertical. Every street here runs along world X or
world Z, so the network needs no staircase mitigation at all: what you get is a nested grid of
rectangular city blocks of varying size, closer to a planned downtown than to an organically
grown neighborhood.

The variety comes from *where* and *whether* each cut happens (**Split Jitter**, **Split Axis
Bias**, **Stop Probability**) rather than from the angles, so blocks differ in size and proportion
while every road stays cardinal.

### Everything else matches the Voronoi tool exactly

Output is a `SubRoads` object by default — the same default name the Voronoi tool uses, so the two
are drop-in alternatives to each other (each one's Generate replaces whatever object currently has
that name; they are not meant to coexist in one scene under the same name). Generated sub-roads
are kept inset from the loop boundary by **Boundary Margin**, and wherever that margin lets a
sub-road cell end up orthogonally adjacent to a main-road cell, that's a deliberate connection
point: Generate opens the sub-road side toward it *and* replaces the actual placed `Road` tile at
that cell with a connector piece (bend/T-junction/four-way, whichever the combined connectivity
resolves to), so the join reads as one continuous network. As a hard backstop *unrelated* to
connections, the loop shape is also rasterized onto the same grid, so a sub-road cell can never
land on the exact same cell a main-road cell already occupies — connections join two adjacent
cells, they never merge two into one. `Clear` only ever removes `SubRoads` (by name), never `Road`.

## Quick start

1. Make sure you have a **closed** `Spline` forming the main loop — the same kind of object
   [the spline Road Generator](RoadGenerator.md) already uses (e.g. the scene's `Spline`
   GameObject). It doesn't need to have been used to generate an actual `Road` first; only its
   shape matters here.
2. Open `Tools > OpenCity > Sub-Road Generator (BSP)`.
3. Click **Use Selected GameObject** (with the loop spline selected in the Hierarchy), or drag the
   `SplineContainer` into the **Spline Container** field directly.
4. Tune **Max Split Depth**, **Min Region Size**, and **Boundary Margin** to taste (the defaults
   are a reasonable starting point for a loop roughly 10–20 units across) and watch both the live
   preview box *and* the Scene view update — colored, oriented squares show exactly where each
   dead-end/straight/bend/T-junction/four-way piece will land, before you commit to Generate.
5. Don't like the layout? Click **Reroll** instead of hand-editing Random Seed. Like what you see?
   Click **Lock** to pin it — you can then freely tweak Ground Y or which prefab is assigned
   without the layout itself changing underneath you.
6. Leave the **Shapes** fields on their defaults, or swap in cosmetic siblings.
7. Click **Generate**.

A GameObject named `SubRoads` (configurable) appears in the scene, containing one prefab instance
per generated cell, matching exactly what was shown in the Scene view.

## How it works

Steps marked *(shared)* are literally the same code the Voronoi tool runs — see
`Assets/Editor/SubRoadNetworkShared.cs`.

1. **Polygon sampling** *(shared)*: the loop spline is sampled at a fixed arc-length step
   (**Polygon Sample Step**) into a continuous `List<Vector2>` polygon in world XZ, used for
   geometric tests (point-in-polygon, distance-to-boundary), not placed directly.
2. **Main-loop overlap exclusion (hard safety net)** *(shared)*: the loop polygon itself is
   rasterized onto the same grid — mirroring exactly how the spline-based Road Generator would
   rasterize it — to get the set of cells the main loop's own road occupies. Any sub-road cell
   that lands on one of those cells outright is dropped in step 6, regardless of how Boundary
   Margin is set. Any drop is reported in the live preview, never silent.
3. **Bounding rectangle**: the polygon's tight axis-aligned bounding box becomes the root region
   to partition. Note this is the *bounding box*, not the loop's actual shape — see Limitations.
4. **Recursive splitting** (`BspGeometry.BuildSplits`): each region is cut in two by a single
   axis-aligned line, and both halves recurse. Per region:
   - **Stop** if **Max Split Depth** is reached, or if neither axis can fit two halves that both
     clear **Min Region Size**, or on a **Stop Probability** roll. The root region is exempt from
     that roll, so an unlucky first draw can never leave the whole network empty.
   - **Choose the axis**: with probability **Split Axis Bias**, cut across the region's longer
     side (which keeps blocks squarish); otherwise cut across the shorter one (which produces
     elongated blocks). Only an axis that can still fit two Min Region Size halves is eligible.
   - **Choose the position**: the exact middle, offset by up to **Split Jitter** × the region's
     extent in either direction, then hard-clamped so neither child can come out under Min Region
     Size. The clamp always wins over the jitter, which is what guarantees no degenerate region
     is ever produced.
   - **Emit the cut** as a segment spanning the full extent of the region it divided, then recurse
     into both children in a fixed order.

   Because a child's cut spans only its own region, it terminates exactly on its parent's cut —
   which is why this network is connected by construction and produces a great many T-junctions.
   Only the cut lines are returned; the leaf rectangles (the blocks *between* the roads) are never
   materialized, since nothing downstream needs them — the same simplification the Voronoi tool
   makes by never constructing bounded Voronoi cell polygons.
5. **Interior clipping and rasterization** *(shared)*: each cut is sampled at a fixed world-space
   step (**Edge Sample Step**); only the *maximal runs* of samples that stay inside the polygon
   **and** at least **Boundary Margin** from its boundary are kept, and each kept run's samples are
   converted to grid cells with the same `WorldToCell` formula the spline Road Generator uses (so a
   `SubRoads` network sits on the exact same absolute lattice as `Road`). A run never bridges
   across a sample outside that interior band — a cut leaving the loop becomes a real gap, not a
   line cutting through the excluded margin.
6. **Dedupe and exclude**: cells are pooled, deduplicated, stripped of anything coinciding with a
   main-loop cell from step 2, and sorted for reproducible ordering.
7. **Simplify Staircases (optional)** *(shared)*: the same cleanup pass the Voronoi tool documents
   in detail. It rarely finds anything here — BSP cuts are already axis-aligned, so there are no
   staircases to collapse beyond the occasional single-cell rounding jog where a cut sits almost
   exactly on a cell boundary. Off by default.
8. **Connecting to the main road** *(shared)*: for every final sub-road cell, each of its four
   orthogonal neighbors is checked against the main-loop cell set. Wherever one matches, that's a
   *candidate* connection — an extra open-direction bit for the sub-road cell and the opposite bit
   for the main-road cell. Each candidate is then looked up against the actual `Road` object in the
   scene (**Main Road Parent Name**/**Main Road Parent Container**): the child instance nearest the
   candidate cell's position (within a generous per-cell tolerance) becomes that connection's
   target. A candidate with no `Road` object found, or no instance found near it, is dropped —
   left completely unconnected on both sides — rather than half-wiring one side only. This happens
   inside the same network-build call the live preview uses, not only at Generate time, which is
   what keeps the preview and Generate in agreement about exactly which connections will land.
9. **Placement**: the cells are handed to `RoadGridPlacer.BuildGraph`/`PlaceCells` — the exact same
   classify → solve-rotation → correct-pivot → instantiate → undo-safe-clear pipeline the other
   tools use (see [Docs/RoadGenerator.md#how-it-works](RoadGenerator.md#how-it-works)). Separately,
   each applied connection's `Road` tile is destroyed and replaced with a freshly-resolved piece
   using *this tool's own* Shapes prefabs. Both are wrapped in one Undo group, so **Ctrl+Z** undoes
   an entire Generate — sub-road placement and main-road tile swaps together — in a single step.

## What is a BSP tree?

Take a rectangle and cut it in two with a single straight line. Now do the same to each half, and
to each of those halves, and so on until the pieces are small enough. That recursive halving is
**Binary Space Partitioning**, and the tree of regions it produces is a BSP tree. Draw a road along
every cut and the pieces left over between them are your city blocks.

Because each cut only spans the region it divides, a deeper cut always dead-ends into a shallower
one rather than crossing it — which is why BSP street networks are dominated by T-junctions, and
why they read as *planned* (a main avenue, side streets off it, alleys off those) rather than
organically grown. Contrast [the Voronoi tool](VoronoiSubRoadGenerator.md), which starts from
scattered points and draws the boundaries between their territories: irregular, angular, and
without any inherent hierarchy.

## Determinism & seed

**Random Seed**, together with every other field (the loop spline's current shape, Max Split Depth,
Min Region Size, Split Jitter, Split Axis Bias, Stop Probability, Boundary Margin, the sample-step
fields, the resolved cell size, and Simplify Staircases/Straighten Tolerance), fully determines the
network: the whole pipeline is a pure function of these serialized values, computed by one method
(`BuildNetwork`) that both the live preview box and the actual `Generate` call, unchanged, every
time. There is no second code path that could quietly drift from the first. Concretely:

- The live preview box always shows *exactly* what `Generate` will produce for the current field
  values — never an approximation.
- Re-running `Generate` with the same **Random Seed** and the same other settings always reproduces
  the identical cell layout.
- Changing **Random Seed** alone re-rolls the split decisions (and therefore the whole network) to
  a new, still-deterministic layout — the standard way to "try another layout" with this tool.

`BspGeometry` uses a `System.Random` seeded fresh from **Random Seed** for every call — never
Unity's global `UnityEngine.Random`, whose state is shared editor-wide and would break exactly
this guarantee.

## Window reference

### Loop
| Field | Description |
|---|---|
| Spline Container | The `SplineContainer` whose spline forms the main loop. **Must be Closed** — an open spline has no interior and is rejected. |
| Use Selected GameObject | Grabs the `SplineContainer` off the currently selected Hierarchy object. |
| Spline Index | Only shown when the container has more than one spline; picks which one to follow. |

### BSP
| Field | Default | Description |
|---|---|---|
| Max Split Depth | `5` | How many times a region may be recursively split. Higher = denser network of smaller blocks (up to 2^depth of them, before Min Region Size and Stop Probability cut it back). |
| Min Region Size | `3` | Smallest world-space width/height a block may be split down to — a region is only split if **both** resulting halves still clear this. **Keep at roughly 2× the cell size or more** (the preview warns if you don't) — see Limitations. |
| Split Jitter | `0.15` | `0` = every cut lands at the exact middle of its region (rigid, evenly-halved blocks). Higher values let the cut wander that fraction of the region's extent either side of center, for uneven block sizes. Min Region Size always wins over jitter. |
| Split Axis Bias | `0.75` | Probability of cutting across the region's longer side. `1` = always the longer side (blocks stay squarish and regular); `0.5` = coin flip (more varied, more elongated blocks). |
| Stop Probability | `0.15` | Chance a splittable region stops early anyway, leaving one big block where its neighbors subdivided — the main source of block-size variety. The first (root) split is exempt, so this can never leave the network empty. |
| Boundary Margin | `2` | Minimum distance road cells must stay from the main loop's boundary. Wherever it lets a sub-road cell end up touching a main-road cell, that becomes a connection point — lower it to create more of them, raise it to keep the network further inset. |
| Random Seed | `12345` | Same seed + same settings always produce the same network. Change it (or hit **Reroll**) to re-roll the layout. |
| Polygon Sample Step | `0.5` | Arc-length step used to sample the loop spline into the polygon used for clipping. Lower = more faithful to a tightly-curved loop, at the cost of a larger sample count. |
| Edge Sample Step | `0.5` | World-space step used to sample each cut line when rasterizing it onto the grid. Lower = more faithful clipping at the Boundary Margin. |

### Grid
| Field | Description |
|---|---|
| Cell Size Override | Force a grid cell size instead of auto-measuring the Straight prefab's renderer bounds along X. `0` = auto. |
| Ground Y | World Y every placed tile sits at. This is a **flat-grid generator** — there is no per-cell height or ramp support. |

### Cleanup
| Field | Description |
|---|---|
| Simplify Staircases | The shared post-rasterization cleanup pass (see [the Voronoi doc](VoronoiSubRoadGenerator.md) for its full rules). Rarely does anything here — BSP cuts are already axis-aligned. **Off by default.** |
| Straighten Tolerance (cells) | How many cells of lateral drift along an otherwise-straight run get snapped flat instead of kept as a real bend. Only matters while Simplify Staircases is on. |

### Shapes and Placement
Identical fields to the other tools — see [Docs/RoadGenerator.md](RoadGenerator.md#shapes)
(Dead-End / Straight / Bend / T-Junction / Four-Way prefab slots) and its Placement section
(Parent Container). **Parent Name** defaults to `SubRoads`, the same as the Voronoi tool, so the
two are drop-in alternatives; Generate/Clear only ever look up and touch that name.

Assigning the **T-Junction** prefab matters more here than with any other tool: BSP's structure
means most interior intersections are T-junctions (a child cut ending on its parent's cut), so
leaving that slot empty skips a large fraction of the network.

### Main Road Connection
| Field | Description |
|---|---|
| Main Road Parent Name | Name of the already-generated main Road GameObject to connect into. Matches Road Generator's own **Parent Name** field — default `Road`. |
| Main Road Parent Container | Optional transform the main Road GameObject is nested under. Leave empty for scene root — must match whatever Road Generator was set to when `Road` was generated. |

If the named object isn't found, or a specific candidate connection has no `Road` tile instance
near it, that connection (and only that one) is skipped — both sides stay unconnected — and the
preview/Generate log a warning rather than failing outright.

### Live preview
Below the fields, a read-only box reports (recomputed via the same `BuildNetwork` call `Generate`
uses): the sampled polygon's vertex count and loop length; the number of BSP split lines produced;
and the final cell count broken down per shape category (already reflecting any applied main-road
connection's extra open direction and any Simplify Staircases rewrite), plus any skipped-cell
count. When one or more connections will be applied, an extra line reports how many sub-road cells
connect into how many main-road tiles.

Non-blocking warnings cover: no splits produced at all (Min Region Size too large / Max Split Depth
too low for this loop); no cells surviving clipping (Boundary Margin too large); Min Region Size
under 2× the cell size; one or more cells dropped for coinciding with the main loop's own road
cells; the named main Road object not found when there are candidate connections waiting for it;
candidate connections with no matching Road tile nearby; and a non-identity rotation/scale on the
loop spline's transform (same warning as the other tools, and for the same reason — it desyncs the
grid axes from world X/Z).

### Scene-view preview, Reroll & Lock
Alongside the text preview box, the Scene view draws the same network live: the sampled loop
polygon (white outline) and every grid cell as a flat, colored, correctly-rotated square matching
the piece Generate would actually place there:

| Color | Category |
|---|---|
| Red | Dead-end |
| Light gray | Straight |
| Yellow | Bend |
| Orange | T-junction |
| Cyan | Four-way |
| Magenta wireframe | Unclassifiable (skipped, same as Generate would skip it) |
| White ring | A main-road cell that will be replaced with a connector piece |

A **Show Raw Split Lines** toggle additionally draws the pre-rasterization BSP cuts (thin,
translucent blue) — the geometry the grid cells were derived from, useful for seeing which cut
produced which street and where Boundary Margin trimmed it.

Two controls sit next to this:
- **Reroll** (next to Random Seed) — randomizes Random Seed and re-previews. Disabled while locked.
- **Lock / Unlock** — freezes the currently-previewed network (including its resolved cell size) so
  it stops recomputing from live field values. While locked:
  - **Generate always places exactly the locked network** — never a value that could have silently
    drifted from what you last saw.
  - Ground Y, the five Shapes prefab slots, Parent Name, and Parent Container all stay live and
    fully editable — they affect *where*/*what* gets placed, not *the layout itself*.
  - If you then change a geometry-affecting field, an info box reports the locked network is now
    stale relative to those fields — Generate will still use the locked layout regardless, until
    you Unlock or Reroll.
  - The locked snapshot also freezes exactly which main-road connections were found and which
    `Road` tile instances they target. If `Road` is deleted or regenerated after locking, those
    connections can no longer be applied — Generate logs a warning and skips them rather than
    erroring; the fix is to Unlock (or Reroll) so a fresh build re-finds the current `Road` tiles.

Locking is a preview cache in memory only — it does not survive a script recompile or closing the
window; both silently return you to the live preview, by design.

### Buttons
- **Generate** — disabled until settings are valid (missing/open spline, missing required prefab,
  Max Split Depth below 1, non-positive Min Region Size, negative Boundary Margin, unresolvable
  cell size — these show as a warning in the preview box instead), *unless the preview is locked*,
  in which case Generate remains available and places the locked network regardless. Clears and
  rebuilds the named `SubRoads` parent, replaces every applied connection's `Road` tile (both
  wrapped in one Undo group), and logs a one-line summary to the Console.
- **Clear** — deletes the `SubRoads` parent (by name) without regenerating. Never touches `Road`,
  even if a previous Generate replaced tiles in it — undo that particular Generate (Ctrl+Z) if you
  need those specific tiles back.

## Limitations

- **Min Region Size below ~2× the cell size produces road blobs, not blocks.** Two cuts landing
  within one cell of each other rasterize into adjacent parallel runs of cells — a 2-wide slab of
  road rather than a block boundary with something between. The output is still valid and fully
  classifiable, it just looks wrong. The preview warns when you cross this threshold; it is not
  code-gated, since the "right" ratio depends on the kit.
- **Only the bounding rectangle is partitioned, not the loop's actual shape.** For a strongly
  concave loop (an L or a crescent), part of that rectangle isn't inside the loop at all, so some
  cuts are computed and then entirely discarded by interior clipping. The result is correct — no
  cell ever lands outside the loop — but recursion is spent on splits that produce nothing, and
  blocks near a concavity can come out oddly proportioned since the split positions were chosen
  against the rectangle rather than the visible interior. Max Split Depth / Min Region Size remain
  the practical cost cap.
- **T-junction-heavy by construction.** A child's cut ends on its parent's cut rather than crossing
  it, so four-way intersections are rare (they need two sibling regions to independently cut at
  nearly the same coordinate). If the T-Junction prefab slot is empty, most interior intersections
  are skipped with a console warning.
- **Stop Probability and Split Axis Bias can under-deliver density** relative to what Max Split
  Depth alone suggests — a region that stops early stays one big block. This is a
  recursion-stopping phenomenon, not the Voronoi tool's rejection-sampling shortfall: nothing is
  being retried and failing, the algorithm simply chose to stop. Lower Stop Probability or Min
  Region Size if the result is sparser than you want.
- **Simplify Staircases is near-redundant here** — it exists because both sub-road tools share one
  pipeline, but with every cut already axis-aligned there are no staircases to collapse beyond rare
  single-cell rounding jogs. Leave it off unless you actually see one.
- **Connections require an already-generated `Road`** — a candidate connection can only be applied
  if the main Road object already exists in the scene at **Main Road Parent Name**/**Main Road
  Parent Container** with a tile instance near the expected cell; otherwise it's silently left
  unconnected (with a console warning). Generate the main Road first if you want connections to
  actually land.
- **Connector pieces come from this tool's own Shapes, not Road Generator's** — a replaced `Road`
  tile is instantiated from *this* window's prefab slots, on the assumption the main road and
  sub-road network use the same kit.
- **Tile lookup is position-based, not identity-based** — a connecting `Road` tile is found by
  nearest position (within a generous per-cell tolerance), not by any stored reference. A cell size
  mismatch between the two tools can make a candidate connection find no match (skipped, not
  misapplied elsewhere).
- **Exact-cell overlap exclusion still applies regardless of connections**: any sub-road cell that
  would land on the main loop's own rasterized footprint is hard-dropped — connections join two
  *adjacent* cells, they never merge two into one.
- **No self-intersection handling**: the loop polygon is assumed simple (non-self-crossing). A
  self-intersecting spline will still produce *some* result rather than crashing, but it isn't a
  validated or supported input shape.
- Shares the same underlying-engine limitations as the other tools (see
  [Docs/RoadGenerator.md#safety--limitations](RoadGenerator.md#safety--limitations)): 1×1-footprint
  pieces only, cosmetic-variant slots are manual, flat grid only (no ramp/slope integration),
  name-based Generate/Clear lookup, and the whole Generate (including the implicit clear of a prior
  run) is one undo-safe group.

## Files touched by this feature
- `Assets/Editor/BspSubRoadWindow.cs` — the tool (UI, pipeline orchestration, Scene-view preview).
- `Assets/Editor/BspGeometry.cs` — the recursive rectangle-splitting algorithm.
- `Assets/Editor/SubRoadNetworkShared.cs` — spline→polygon sampling, loop rasterization,
  interior-clipped segment rasterization, and the main-road connection mechanic, shared with the
  Voronoi Sub-Road Generator.
- `Assets/Editor/RoadGridPlacer.cs` — shared grammar/placement engine (`BuildOpenAtZeroByCategory`
  is public so connector-piece rotation can reuse it).
- `Docs/BspSubRoadGenerator.md` — this document.
