# Tilemap Road Generator

An Editor tool that places modular road prefabs (from `Assets/Prefabs`) by reading the
occupied cells straight off a painted Unity `Tilemap`, and resolving each occupied grid
cell's piece + rotation from the same small connectivity "grammar" as
[the spline-based Road Generator](RoadGenerator.md) — the way Kenney's own
[city-kit-roads preview art](https://kenney.nl/media/pages/assets/city-kit-roads/8570bf4a2f-1787042771/preview.png)
composes a road network from square tiles that snap together edge-to-edge.

- **Script**: `Assets/Editor/TilemapRoadGeneratorWindow.cs`
- **Namespace**: `OpenCity.EditorTools`
- **Menu**: `Tools > OpenCity > Road Generator (Tilemap)`
- **Requires**: nothing beyond Unity's built-in Tilemap module (`com.unity.modules.tilemap`,
  already bundled with the editor — no package install needed)

This tool is an alternative front-end to [the spline-based Road Generator](RoadGenerator.md),
sharing its grid-graph → grammar → rotation-solve → pivot-correct → instantiate pipeline via
`Assets/Editor/RoadGridPlacer.cs`. The only thing that differs is *how the occupied cell set
is authored*: instead of drawing a `Spline` and rasterizing it onto a grid (sampling,
rounding, staircase-bridging), you paint the grid cells directly with Unity's own Tile
Palette. The painted cells *are* the occupied set — there's no rasterization step and
nothing to approximate, since a Tilemap is already grid-shaped, unlike a curve.

## Quick start

1. Open `Tools > OpenCity > Road Generator (Tilemap)`.
2. Click **Create Road Tilemap**. This creates a `Road Tilemap Grid` GameObject (a `Grid`
   with `Cell Swizzle` set to `XZY` so it lies flat on the world XZ ground plane, and cell
   size matching the road prefab footprint) with a child `Tilemap`, and assigns it to the
   window.
3. Open `Window > 2D > Tile Palette`. If you don't have a placeholder `Tile` asset yet,
   create one via `Assets > Create > 2D > Tiles > Tile` with any sprite (only whether a
   cell has *a* tile matters to this tool, not which one or what it looks like).
4. With the palette's brush selected and the created `Tilemap` as the active target, paint
   the road layout in the Scene view (top-down works best — the grid renders flat on the
   ground). Box Fill and Erase work exactly as in any 2D Tilemap workflow.
5. Back in the Road Generator (Tilemap) window, leave the **Shapes** fields on their
   defaults, or swap in cosmetic siblings (e.g. `road-straight-barrier`).
6. Click **Generate**.

A GameObject named `Road` (configurable) appears in the scene, containing one prefab
instance per painted cell, positioned and rotated by the grammar table below. Every
instance is a proper linked `PrefabUtility` instance (blue-link icon), not a disconnected
copy.

## How it works

1. **Read occupied cells**: the tool iterates the assigned `Tilemap`'s `cellBounds` and
   checks `HasTile` at each position — every cell that has *any* tile painted on it is
   occupied. This is exact, not an approximation: whatever you painted is exactly what
   gets read.
2. **Grid graph**, **grammar**, and **pivot correction** are identical to the spline
   tool's — see [Docs/RoadGenerator.md](RoadGenerator.md#how-it-works) for the full
   writeup. In short: each occupied cell's N/E/S/W occupied-neighbor mask picks a shape
   category (dead-end / straight / bend / T-junction / 4-way) and a 90° rotation is solved
   against each category's documented "open sides at rotation 0."
3. **Cell → world**: each occupied cell's placement position comes from
   `Tilemap.GetCellCenterWorld`, which already accounts for the Grid's cell size, swizzle,
   and transform — the tool never reimplements that conversion. Only the Y coordinate is
   overridden, to the **Ground Y** field.
4. Instantiates each cell's resolved prefab as a linked prefab instance under the generated
   parent, wrapped in one `Undo` group (including the implicit clear of a previous
   generation), same as the spline tool.

## Orientation convention

Identical to the spline tool: rotating a piece by `+90°` around Y advances its open
edge(s) clockwise through North → East → South → West → North, and the same five
categories map to the same default prefabs and "open sides at rotation 0":

| Category | Default prefab | Open sides at rotation 0 |
|---|---|---|
| Dead-end | `road-end` | South only |
| Straight | `road-straight` | West + East (local X through-axis) |
| Bend | `road-bend` | North + East |
| T-junction | `road-side` | West + East + North |
| 4-way | `road-crossroad` | North + East + South + West (rotation is cosmetic only) |

A painted Tilemap cell's local X maps to world X and local Y maps to world Z (via the
`XZY` cell swizzle set up by **Create Road Tilemap**) — so North/East/South/West mean the
same thing here as they do for the spline tool's world-XZ grid.

## Why `Cell Swizzle = XZY`

Unity's `Tilemap` is authored in the Grid's local X/Y axes. A `Grid`'s `Cell Swizzle`
property remaps those local axes to world axes — the standard technique for laying a
Tilemap flat on the ground plane in a 3D scene (local X/Y → world X/Z) instead of the
default 2D-game orientation (local X/Y → world X/Y, a vertical wall-facing-camera plane).
**Create Road Tilemap** sets this up for you; if you build the Grid/Tilemap by hand instead
(or reuse an existing one), set `Cell Swizzle` to `XZY` yourself, or placement will not
land where you painted. The preview box warns (non-blocking) if the assigned Tilemap's
Grid isn't set to `XZY`.

## Window reference

### Tilemap
| Field | Description |
|---|---|
| Tilemap | The painted `Tilemap` to read occupied cells from. |
| Use Selected GameObject | Grabs a `Tilemap` component off (or under) the currently selected Hierarchy object. |
| Create Road Tilemap | Convenience setup: creates a `Grid` (Cell Swizzle `XZY`, cell size auto-matched to the Straight prefab's footprint) with a child `Tilemap`, and assigns it. |

### Grid
| Field | Description |
|---|---|
| Cell Size Override | Force a cell size instead of auto-measuring the Straight prefab's renderer bounds along X. `0` = auto. Used to validate the Tilemap's own cell size (warns on mismatch) and to size a newly created Tilemap — the Tilemap's own Grid cell size is what actually governs placement. |
| Ground Y | World Y every placed tile sits at. This is a **flat-grid generator** — there is no per-cell height or ramp support. |

### Shapes and Placement
Identical fields to the spline tool — see
[Docs/RoadGenerator.md](RoadGenerator.md#shapes) (Dead-End / Straight / Bend / T-Junction /
Four-Way prefab slots, Parent Name, Parent Container).

### Live preview
Below the fields, a read-only box shows cell count broken down per shape category (plus
any skipped-cell count) before you click Generate. Warns (non-blocking) if the Tilemap's
Grid isn't `Cell Swizzle = XZY`, or if its cell size doesn't match the detected road-prefab
footprint.

### Buttons
- **Generate** — disabled until settings are valid (missing Tilemap, missing required
  prefab, etc. show as a warning in the preview box instead). Clears and rebuilds the named
  parent, selects it when done, and logs a one-line summary to the Console.
- **Clear** — deletes the generated parent (by name) without regenerating.

## Coordinate space

Placement happens on a world-space XZ grid, read through the Tilemap's own
`GetCellCenterWorld` (see "Why Cell Swizzle = XZY" above). The Tilemap's parent `Grid`
transform can be translated freely (that's how you'd offset where the whole grid sits);
rotating or scaling it desyncs the grid axes from world X/Z the same way it would for the
spline tool's `SplineContainer`.

## Safety / limitations

Shares every limitation of the spline tool (see
[Docs/RoadGenerator.md#safety--limitations](RoadGenerator.md#safety--limitations)) —
undo-safe, name-based lookup, flat grid only, 1×1-footprint pieces only, cosmetic variant
is manual, single generated root — **except** rasterization approximation, which doesn't
apply here: painted cells are read exactly as painted, with no sampling or rounding step.

The one thing this tool doesn't do for you: it never creates or manages the placeholder
`Tile` asset used for painting. Any `TileBase` works, since only cell occupancy is read —
create one via `Assets > Create > 2D > Tiles > Tile` if you don't already have one.

## Files touched by this feature
- `Assets/Editor/TilemapRoadGeneratorWindow.cs` — the tool (Tilemap-reading front-end only).
- `Assets/Editor/RoadGridPlacer.cs` — the shared grammar/placement engine, also used by
  the spline-based tool.
- `Docs/TilemapRoadGenerator.md` — this document.
