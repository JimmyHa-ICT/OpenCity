# Road Generator

An Editor tool that places modular road prefabs (from `Assets/Prefabs`) along any
`SplineContainer` in the scene by rasterizing the spline onto a fixed-size world grid
and resolving each occupied grid cell's piece + rotation from a small connectivity
"grammar" — the way Kenney's own
[city-kit-roads preview art](https://kenney.nl/media/pages/assets/city-kit-roads/8570bf4a2f-1787042771/preview.png)
composes a road network from square tiles that snap together edge-to-edge.

- **Script**: `Assets/Editor/RoadGeneratorWindow.cs`
- **Namespace**: `OpenCity.EditorTools`
- **Menu**: `Tools > OpenCity > Road Generator`
- **Requires**: `com.unity.splines` (already in `Packages/manifest.json`)

There is also a Tilemap-based alternative front-end that skips curve authoring
entirely and reads the occupied cell set straight off a painted `Tilemap` — see
[`Docs/TilemapRoadGenerator.md`](TilemapRoadGenerator.md). Both tools share the
same grammar/placement engine, `Assets/Editor/RoadGridPlacer.cs`.

Once a closed loop like this exists, [`Docs/VoronoiSubRoadGenerator.md`](VoronoiSubRoadGenerator.md)
can generate an interior street network inside it procedurally, using a
Voronoi diagram.

This tool replaces an earlier approach that walked the spline's arc length
continuously and heuristically detected corners by tangent angle. That approach kept
producing jitter and gaps/overlaps at corners, because it fought continuous curve
math against a kit of discrete, rigid square tiles. This tool sidesteps the problem
entirely: every tile sits at an exact grid-cell position and only ever rotates in
90° steps, so gaps and jitter are structurally impossible — adjacent cells are
always exactly one cell-width apart, and every piece in the kit shares the same
footprint.

## Quick start

1. Create or select a `Spline` GameObject (`GameObject > Splines > Draw Spline Tool`,
   or any existing `SplineContainer` in the scene) and draw the road's path. Since
   the result snaps to a grid, straight/orthogonal paths (a street grid) look best —
   see "Orthogonal input" below.
2. Open `Tools > OpenCity > Road Generator`.
3. Click **Use Selected GameObject** (with the spline selected in the Hierarchy), or
   drag the `SplineContainer` into the **Spline Container** field directly.
4. Leave the **Shapes** fields on their defaults, or swap in cosmetic siblings (e.g.
   `road-straight-barrier`).
5. Click **Generate**.

A GameObject named `Road` (configurable) appears in the scene, containing one prefab
instance per grid cell, positioned and rotated by the grammar table below. Every
instance is a proper linked `PrefabUtility` instance (blue-link icon), not a
disconnected copy.

## How it works

1. **Rasterization** (spline → ordered grid cells): the spline is sampled at a fixed
   small arc-length step (`cellSize / Samples Per Cell`, so smaller cells or a higher
   Samples Per Cell sample more finely) using `SplineUtility.GetPointAtLinearDistance`
   / `SplineUtility.Evaluate`, and each world-space sample is rounded to its nearest
   grid cell (`round(x / cellSize)`, `round(z / cellSize)`). Consecutive samples that
   land in non-adjacent cells (can happen on tight curves) are bridged with an
   axis-aligned staircase walk so every consecutive pair of cells in the resulting
   ordered path is orthogonally adjacent (Manhattan distance exactly 1) — this is
   deliberately simpler than full Bresenham/DDA line-drawing between arbitrary
   endpoints, since the fixed small sample step keeps a normal bridge to a single
   diagonal step; the staircase loop only needs to do more if `Samples Per Cell` is
   set very low.
2. **Grid graph** (cells → connectivity): the set of *occupied* cells (not the walk
   order) determines, per cell, which of its four orthogonal neighbors are also
   occupied — a 4-bit North/East/South/West mask. Building this from the occupied
   set rather than the walk means a spline that crosses back over itself naturally
   produces an emergent T-junction or 4-way crossing at the crossing point, for free
   — not a feature you need to do anything to get.
3. **Grammar** (mask → prefab + rotation): each mask is classified into one of five
   shape categories by its exact bit pattern (not just bit count, since 2 bits is
   ambiguous between "opposite" = straight and "adjacent" = bend):
   - 1 bit → **dead-end**
   - 2 opposite bits (N+S or E+W) → **straight**
   - 2 adjacent bits (N+E, E+S, S+W, W+N) → **bend**
   - 3 bits → **T-junction**
   - 4 bits → **4-way**

   Each category has a configured prefab and a documented "open sides at rotation
   0" (see the table below). A generic solver tries all four 90° rotation steps and
   picks the one where rotating the shape's rotation-0 open sides exactly matches
   the cell's mask — this is why adding a new shape variant later needs no new
   rotation logic, just its own open-sides fact.
4. **Pivot correction**: most of this kit's prefabs have a pivot centered on their
   square footprint, but `road-bend` (and its barrier/sidewalk/square siblings) is
   pivoted at the inside corner of its L shape. Before placing, the tool measures
   each configured prefab's renderer-bounds union once per Generate and computes a
   horizontal-plane-only (XZ) offset from pivot to true footprint center, rotated to
   match the cell's placement rotation. This is XZ-only on purpose — correcting Y
   too would nudge prefabs whose mesh isn't Y-symmetric about its pivot (curbs,
   barriers) off the ground plane.
5. Instantiates each cell's resolved prefab as a linked prefab instance, in path
   order (so Hierarchy names read as a sane sequence), under the generated parent.
6. The whole run — including clearing a previous generation — is wrapped in one Undo
   group, so **Ctrl+Z** undoes an entire Generate in a single step.

## Orientation convention (important)

This project's `road-*` kit was reverse-engineered empirically (place a piece,
surround it with `road-straight` probes on all 4 sides, see which ones connect
without a curb seam). Rotating a piece by `+90°` around Y (`Quaternion.Euler(0, 90,
0)`) advances its open edge(s) **clockwise** through North → East → South → West →
North.

| Category | Default prefab | Open sides at rotation 0 |
|---|---|---|
| Dead-end | `road-end` | South only |
| Straight | `road-straight` | West + East (local X through-axis) |
| Bend | `road-bend` | North + East |
| T-junction | `road-intersection-path` (the kit's real, full-width T-junction — `road-side` is a narrower driveway-style branch, and plain `road-intersection` is a different connector) | West + East + North |
| 4-way | `road-crossroad` | North + East + South + West (rotation is cosmetic only) |

## Orthogonal input

Because placement snaps to a grid with only cardinal rotations, this tool is built
for **orthogonal, street-grid-style paths** — splines that mostly move along one
axis at a time, the way you'd sketch city blocks. A freeform/organic curve still
rasterizes (nothing crashes), but it reads as a staircase of cells rather than a
smooth curve, since there is no smooth-curve placement mode in this tool. If you
want an organically curving path (a footpath, a decorative walkway) rather than a
grid-snapped road network, that's outside this tool's scope in v1.

## Window reference

### Spline
| Field | Description |
|---|---|
| Spline Container | The `SplineContainer` to follow. |
| Use Selected GameObject | Grabs the `SplineContainer` off the currently selected Hierarchy object. |
| Spline Index | Only shown when the container has more than one spline; picks which one to follow. |

### Grid
| Field | Description |
|---|---|
| Cell Size Override | Force a grid cell size instead of auto-measuring the Straight prefab's renderer bounds along X. `0` = auto (this kit measures ≈1.0 unit). |
| Samples Per Cell | How many times the spline is sampled per cell width during rasterization (default 4). Raise this if tight curves produce visibly wrong corner placement. |
| Ground Y | World Y every placed tile sits at. This is a **flat-grid generator** — there is no per-cell height or ramp support; use `road-slant`/`road-bridge` manually for elevation changes. |

### Shapes

Most prefabs that are actually usable in one of the five slots below — 1×1 grid-cell
footprint, flat, and matching one of the five open-side categories — live under
`Assets/Prefabs/RoadShapes/`, separate from the rest of the Kenney city-kit-roads set
(2×2 curve pieces, ramps/bridges, signs, the 1×2 split piece, the 0-open-side filler
square, driveway stubs, etc.) — with one exception: the T-junction default,
`road-intersection-path`, lives directly under `Assets/Prefabs/` alongside its
`road-intersection*` cosmetic siblings, not in `RoadShapes/`.

**A naming trap worth knowing about**: despite the name, `road-side` (which lives in
`RoadShapes/`, alongside its `road-side-entry`/`road-side-exit` siblings) is *not* the
kit's T-junction — it's a narrower driveway-style branch, one open side short of a
full-width road, and produces a visibly wrong-looking T when used as one. The T-junction
default was corrected to `road-intersection-path` after visual confirmation in the
Editor. `road-roundabout` is a separate, unrelated trap (see the note below).

| Field | Description |
|---|---|
| Dead-End Prefab | Placed at a path endpoint (1 open side). **Required.** Defaults to `road-end`. |
| Straight Prefab | Placed where two opposite sides are open. **Required.** Defaults to `road-straight`. |
| Bend Prefab | Placed where two adjacent sides are open. **Required.** Defaults to `road-bend`. |
| T-Junction Prefab | Placed where three sides are open. **Optional** — if left empty and a T-junction cell occurs, that cell is skipped with a console warning. Defaults to `road-intersection-path`. |
| Four-Way Prefab | Placed where all four sides are open. **Optional**, same skip-with-warning behavior. Defaults to `road-crossroad`. |

Each field is a manual cosmetic-variant slot — e.g. swap in `road-straight-barrier`,
`road-bend-sidewalk`, `road-intersection-line`, or `road-crossroad-line` for a different
look within the same shape category. The tool never auto-picks between kit siblings.
(`road-roundabout` is *not* a valid sibling despite the name — measured footprint is a
3×3 structure, not the 1×1 grid cell every shape category assumes.)

### Placement
| Field | Description |
|---|---|
| Parent Name | Name of the generated root GameObject. **Generate first deletes any existing object with this exact name** (in the same Parent Container / scene root), then rebuilds — this is how re-running Generate after editing the spline "refreshes" the road. |
| Parent Container | Optional transform to nest the generated root under. Empty = scene root of the active scene. |

There is no Start/End Offset (doesn't map onto grid rasterization) and no End Caps
foldout — a path endpoint is just a 1-bit cell, resolved through the same grammar
table as every other cell, so the Dead-End shape already covers that role natively.

### Live preview
Below the fields, a read-only box shows the computed spline length, resolved cell
size, and cell count broken down per shape category (plus any skipped-cell count)
before you click Generate. A warning appears if the Spline Container's transform has
non-identity rotation or scale (see "Coordinate space" below).

### Buttons
- **Generate** — disabled until the current settings are valid (missing spline,
  missing required prefab, zero-length spline, etc. show as a warning in the preview
  box instead). Clears and rebuilds the named parent, selects it when done, and logs
  a one-line summary to the Console (counts per shape category, cell count, spline
  length, skipped count).
- **Clear** — deletes the generated parent (by name) without regenerating. Logs to
  the Console either way (cleared, or "nothing found").

## Coordinate space

Placement happens on a world-space XZ grid. The tool assumes the `SplineContainer`'s
transform has **identity rotation and scale** — translation-only is fine (and in
fact the normal way to offset where the whole grid sits), but rotating or scaling
the container desyncs the grid axes from world X/Z and silently breaks cell
adjacency. The preview box warns (doesn't block) if this assumption is violated.

## Safety / limitations

- **Undo-safe**: the entire Generate (including the implicit clear of a prior run)
  is one Undo group. Clear is also undoable.
- **Name-based lookup**: Generate/Clear find the target root purely by GameObject
  name under **Parent Container** (or scene root). Keep **Parent Name** unique.
- **Flat grid only**: no ramp/slope integration in v1 — `road-slant*`/`road-bridge`
  stay manual placements, not part of the grammar.
- **1×1-footprint pieces only**: the 2×2 `road-curve*`/`road-curve-intersection*`
  family isn't supported by the grammar (doesn't fit a single grid cell). Swap one in
  by hand afterward if you want a wider-radius corner.
- **Cosmetic variant is manual**: the tool picks a shape *category*, never a specific
  sibling within it (barrier vs. plain vs. line-marked, etc.) — set the field to
  whichever sibling you want for the whole run.
- **Single spline, single pass**: no multi-branch authoring UI. A self-crossing
  spline still works and produces an emergent T/4-way cell at the crossing (see "How
  it works" above), but there's no way to author a branch that isn't already
  expressed as the spline crossing itself.
- **Rasterization is an approximation of the true curve**: rounding each sample to
  its nearest cell (and bridging any gap with an axis-aligned staircase) means very
  tight curves relative to cell size can rasterize slightly differently than the
  spline's literal geometry. Raise **Samples Per Cell** if a corner's placement
  looks wrong.

## Files touched by this feature
- `Assets/Editor/RoadGeneratorWindow.cs` — the tool (rasterization front-end only).
- `Assets/Editor/RoadGridPlacer.cs` — the shared grammar/placement engine (grid graph,
  classification, rotation solving, pivot correction, instantiate/undo/clear), also used
  by the Tilemap-based tool.
- `Docs/RoadGenerator.md` — this document.
