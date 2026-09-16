# City Road Generator (BSP Tilemap)

An Editor tool that lays out the **complete road network of a square city** with Binary Space
Partitioning and **paints it straight onto a Unity `Tilemap`**. Paired with the project's road
RuleTile (`Assets/Tiles/New Rule Tile.asset`), every painted cell turns into the right road prefab
(dead end / straight / bend / T-junction / crossroad) with the right rotation, so one click on
**Generate** gives you a finished, fully connected street grid made of the Kenney road kit in
`Assets/Prefabs/Roads`. The large intersections, where two major roads cross, become the kit's
3×3 `road-roundabout`. On top of that it can lay an **elevated highway network** linking the
city's far apart districts — see [City Highway Generator](CityHighwayGenerator.md).

- **Script**: `Assets/Editor/CityRoadTilemapWindow.cs`
- **Layout algorithm**: `Assets/Editor/CityBspLayout.cs` (pure integer-grid BSP, no Editor or
  Tilemap dependency)
- **Highways**: `Assets/Editor/HighwayPlanner.cs` + `Assets/Editor/HighwayBuilder.cs`, documented
  separately in [CityHighwayGenerator.md](CityHighwayGenerator.md)
- **Road tile**: `Assets/Tiles/New Rule Tile.asset`, an `OpenCity.Tiles.RoadRuleTile`
  (`Assets/Scripts/Tiles/RoadRuleTile.cs`)
- **Namespace**: `OpenCity.EditorTools`
- **Menu**: `Tools > OpenCity > City Road Generator (BSP Tilemap)`
- **Requires**: Unity's built-in Tilemap module, plus `com.unity.2d.tilemap.extras` for the
  RuleTile (both already in the project)

## Where this fits among the road tools

| Tool | Input | Output |
|---|---|---|
| [Road Generator](RoadGenerator.md) | A spline you draw | Road prefabs |
| [Road Generator (Tilemap)](TilemapRoadGenerator.md) | Cells you paint by hand | Road prefabs |
| [Sub-Road Generator (BSP)](BspSubRoadGenerator.md) / [(Voronoi)](VoronoiSubRoadGenerator.md) | A closed spline loop | Road prefabs inside the loop |
| **City Road Generator (BSP Tilemap)** | **Just a side length and a seed** | **Painted Tilemap cells**, which the RuleTile turns into road prefabs |

This tool is the only one that needs no hand-drawn input and whose output stays **editable as
tiles**: after generating, you can keep painting or erasing roads with the Tile Palette and the
RuleTile re-resolves the neighboring pieces automatically.

It shares its BSP idea with the [BSP Sub-Road Generator](BspSubRoadGenerator.md) but not its code.
That tool splits a *world-space* rectangle and rasterizes the cut lines afterwards, so two cuts
landing close together can merge into a two-cell-wide blob of road. This tool splits the grid
itself, cell by cell (see [Guarantees](#guarantees)), so that can't happen.

## Quick start

1. Open `Tools > OpenCity > City Road Generator (BSP Tilemap)`.
2. Click **Create City Tilemap**. This creates a `City Road Grid` (a `Grid` with **Cell Swizzle =
   XZY**, so it lies flat on the ground, and cell size **1**, matching the road kit) with a child
   `Roads` Tilemap, and assigns it to the window. Or pick an existing Tilemap and click **Use
   Selected GameObject**.
3. **Road Tile** is pre-filled with `Assets/Tiles/New Rule Tile.asset`. Leave it.
4. Set **Side Length (cells)**, for example `32`. The white square in the Scene view is the city.
5. Adjust the **BSP** settings while watching the Scene view. Colored squares show every road
   cell by piece type and green outlines show the blocks. Click **Reroll** until you like the layout.
6. Optional: scroll to **Highways** (on by default) to add elevated routes between the city's far
   apart districts — see [CityHighwayGenerator.md](CityHighwayGenerator.md).
7. Click **Generate**. The road cells are painted and the RuleTile spawns a road prefab on each one.
8. Optional: open `Window > 2D > Tile Palette`, choose **New Tile Palette**, and paint or erase
   roads by hand. Neighboring pieces update themselves.

**Ctrl+Z** undoes a whole Generate or Clear in one step.

## How it works

### 1. The city square

The city is a square of **Side Length × Side Length** cells whose south-west (bottom-left) corner
sits at **Origin Cell** on the Tilemap. Inside the algorithm everything is in *city-local* cells
from `(0, 0)` to `(N-1, N-1)`: +X is east and +Y is north (world +Z, thanks to the XZY swizzle).
**Origin Cell** is only added when painting.

### 2. The perimeter ring (optional)

With **Perimeter Ring Road** on, the outermost ring of cells becomes a road: the city's ring road,
with four bends at the corners. BSP then partitions only the interior, `(1, 1)` to `(N-2, N-2)`.
Every interior street ends on the ring, so the network has **no dead ends**.

With it off, BSP partitions the whole square. Streets that reach the city edge stop there as dead
ends, ready to be joined to roads outside the city.

### 3. Recursive splitting

Starting from the interior region, each region is either kept as a **block** or cut in two by a
**one-cell-wide road**, and both halves are processed the same way:

1. **Can it be split?** A cut uses one column (or row) of cells and must leave at least **Min Block
   Size** cells on both sides. So a region can be cut vertically only if it is at least
   `2 × Min Block Size + 1` cells wide, and horizontally only if it is that tall. A cut must also
   *touch a road* at one of its ends (see [Connectivity](#connectivity-without-a-ring-road)
   below). If no axis qualifies, the region becomes a block.
2. **Must it be split?** If **Max Block Size** is set (not `0`) and the region is wider or taller
   than that, and that side is long enough to cut (`2 × Min Block Size + 1`), the split is
   **forced**.
3. **Should it stop anyway?** A region stops and becomes a block if it has reached **Max Split
   Depth**, or on a **Stop Probability** roll. The first (root) region is exempt from the roll.
   Forced splits (step 2) ignore both reasons. The preview reports how many splits were forced.
4. **Which axis?** With probability **Split Axis Bias**, the cut goes across the region's longer
   side, which keeps blocks squarish. Otherwise it goes across the shorter side, giving longer
   blocks. A forced split always cuts an axis that is too long. If the chosen axis can't be cut,
   the other one is used.
5. **Where?** Aim for the middle cell, moved by up to **Split Jitter** × the region's size in
   either direction, then clamped so both halves keep at least Min Block Size cells. From there
   the tool takes the **nearest position that doesn't put two intersections side by side** (see
   [Intersection spacing](#intersection-spacing)). If no position on that axis works, it tries
   the other axis. If neither works, the region stays a block.
6. **Emit the cut.** Every cell of the cut column/row becomes a road, spanning exactly the region
   being split. The two halves are processed in a fixed order (west/south half first).

Because a cut spans only its own region, it ends on the roads that bound that region (its parent
cut, an earlier cut, or the perimeter ring). That's why the network is connected and full of
T-junctions.

A real layout, as `CityBspLayout.Build` produces it for Side Length `15`, perimeter on, Max Split
Depth `6`, Min Block Size `2`, Max Block Size `6`, Jitter `0.3`, Axis Bias `0.75`, Stop `0.2`,
seed `24` (north at the top, `#` = road, `.` = block):

```
      x: 0 1 2 3 4 5 6 7 8 9 10 ...   14
y=14     # # # # # # # # # # # # # # #    <- perimeter ring
y=13     # . . # . . . # . . # . . . #
y=12     # . . # . . . # . . # . . . #
y=11     # # # # # # # # . . # . . . #    <- crossroad at (3,11)
y=10     # . . # . . . # . . # . . . #
y=9      # . . # . . . # # # # # # # #    <- crossroad at (10,9)
y=8      # # # # . . . # . . # . . . #
y=7      # . . # . . . # . . # . . . #
y=6      # . . # . . . # . . # # # # #
y=5      # . . # . . . # . . # . . . #
y=4      # # # # # # # # # # # . . . #    <- T-junctions at x=3, 5, 7: two cells apart, never one
y=3      # . . . . # . . . . # . . . #
y=2      # . . . . # . . . . # . . . #
y=1      # . . . . # . . . . # . . . #
y=0      # # # # # # # # # # # # # # #
                               ^
                               the first (root) cut, x = 10, from ring to ring
```

It took 11 cuts, in this order:
1. x=10 (the root, vertical).
2. West half: y=4, x=5 (south of it), x=7 (north of it), y=11, x=3, y=8, x=3 again north of y=11,
   y=9 between x=7 and x=10.
3. East half: y=6, then y=9.

That makes 12 blocks with sides from 2 to 6 cells. There are two crossroads, both where cuts from
opposite sides of a road landed on the same cell: the two x=3 cuts on y=11, and the two y=9 cuts
on x=10. Every other intersection is a T-junction, and none of them is next to another.

### Connectivity without a ring road

With the perimeter on, every region is enclosed by roads, so any cut automatically ends on a road.
Without it, a region can touch the bare city edge, and a cut running from city edge to city edge
would touch no other road at all: a disconnected street. To prevent this the tool tracks which
sides of each region are roads:

- a **vertical** cut is only allowed if the region has a road to its **south or north**;
- a **horizontal** cut is only allowed if the region has a road to its **west or east**;
- the very first cut of a ring-less city is exempt, since no roads exist yet and it becomes the
  road everything else attaches to.

For example, after a vertical root cut, each half's only road is the root cut to its side. Its
next cut must therefore be horizontal (ending on the root cut), and only after that can vertical
cuts follow. When **Max Block Size** forces a split along a blocked axis, the tool cuts the other
axis first and fixes the oversized side one level down.

### Intersection spacing

Two intersections must never sit in neighboring cells. With the road kit, two T-junction pieces
side by side (or a T next to a crossroad) read as a broken, jogging road rather than two streets.
It happens when two cuts reach the same road **from opposite sides, exactly one cell apart**:

```
before the fix          after: lined up         after: moved one more cell

. . # . . .             . . # . . .             . . # . . .
# # T T # #   <- road   # # + # # #             # # T # T #
. . . # . .             . . # . . .             . . . . # .

T = T-junction, + = crossroad, # = other road. The street from the north stays put;
the street from the south is the one being placed.
```

Cuts from the *same* side can't do this, since Min Block Size already keeps them apart, and cuts
landing on exactly the same cell make a crossroad, which is fine. So before placing a cut, the
tool looks at each road the cut will end on and checks the cells **diagonally beyond** the cut's
end, on the far side of that road. A road there means another street already meets this road
one cell away, so that position is rejected. The cut takes the nearest acceptable position to
where it wanted to go (checking one cell closer and one cell further out, then two, and so on,
lower position first on a tie). Usually that either lines up with the other street as a
crossroad or moves one cell further away.

Because the check looks across the road at what's already there, it doesn't matter which of the
two streets was cut first: whichever comes second moves.

If every position on the chosen axis is rejected, the tool tries the other axis. If that fails
too, the region stays one block. The preview reports how many regions ended that way. This is
also the only way a block can end up larger than **Max Block Size** (see [Guarantees](#guarantees)).

### 4. Painting

**Generate** runs these steps as a single Undo action:

1. If **Clear City Area First** is on, erase every tile inside the city square, on both the street
   Tilemap and the Highway Tilemap (and only there; tiles outside it are never touched).
2. Paint **Road Tile** on every road cell in one `Tilemap.SetTiles` batch, at
   `Origin Cell + city-local cell`. The road cells under a roundabout get the **Cover Tile**
   instead (see [section 6](#6-roundabouts-33-pieces)).
3. Paint the **Cover Tile** on the bottom cell of every highway ramp, which is what turns the street
   there into an interchange.
4. Place the roundabout pieces, if any.
5. Paint the highway, if any, with the **Highway Tile** on its own **Highway Tilemap** layer.

Turning the rest of the cells into 3D roads is the Road Tile's job (next section).

### 5. From painted cells to road prefabs: the Road RuleTile

`Assets/Tiles/New Rule Tile.asset` is an `OpenCity.Tiles.RoadRuleTile`. Each painted cell looks at
its four side neighbors (north, east, south, west) and picks a prefab with the first matching rule:

| Neighbors that are road | Prefab | Rotation |
|---|---|---|
| all 4 | `road-crossroad` | none needed |
| 3 | `road-intersection` (T-junction) | automatic |
| 2, opposite | `road-straight` | automatic |
| 2, adjacent | `road-bend` | automatic |
| 1 | `road-end` | automatic |
| 0 | the tile's **Default GameObject** (currently `road-straight`) | none |

Generated cities never contain a cell with zero road neighbors, so the last row only matters for
stray cells you paint by hand. Change the tile's **Default GameObject** in the Inspector (or clear
it) if you'd rather those show something else.

Why a subclass and not the stock `RuleTile`: the stock one rotates spawned GameObjects around Z,
which is right for 2D sprites but stands a flat 3D road piece up on its edge. `RoadRuleTile`
turns that Z rotation into the same clockwise turn around Y, which is correct for a Grid with
Cell Swizzle XZY.

The RuleTile only reads cells **on the same Tilemap**. A city painted next to other road tiles
connects to them automatically; roads on a different Tilemap are ignored.

**Using a plain tile instead.** Any `TileBase` works as Road Tile. With a non-RuleTile placeholder,
the painted cells are just a map of where the roads go. Run
[Road Generator (Tilemap)](TilemapRoadGenerator.md) on the same Tilemap to place prefabs from it.
Don't combine that with the RuleTile, or every cell gets two road pieces.

### 6. Roundabouts (3×3 pieces)

The city's large intersections, where two major roads cross, get the kit's `road-roundabout`
(`Assets/Prefabs/Roads/road-roundabout.prefab`) instead of a plain crossroad. Unlike every other
piece it covers **3 × 3 cells**, and a RuleTile only fills one cell per tile, so the tool places
these pieces itself.

**The piece.** It is centered on its pivot and has one exit in the middle of each side (north,
east, south and west). The four corner cells hold the outside of the ring and pavement, with no
exit. It looks the same after a quarter turn, so it is never rotated or mirrored.

```
. # .      # = exit cell (road)      C = the crossroad it replaces
# C #      . = corner cell (block)
. # .
```

**Which crossroads get one.** A crossroad `C` gets a roundabout when:
- it is a **large intersection**: the unbroken straight road through `C` is at least **Min
  Crossing Road** cells long in *both* directions (default: half the city's side length);
- no bigger roundabout is within **Min Roundabout Spacing** cells of it, measured along either
  axis (default: a third of the side length). Each part of the city therefore gets at most one
  roundabout, at its biggest crossing;
- each of its four arms (`C` + north, east, south, west) is a straight road leading away from
  it, so a road continues beyond every exit;
- its four diagonal cells are blocks, not road;
- none of its nine cells is already under another roundabout.

**Biggest first, no randomness.** The tool ranks the large intersections by their shorter crossing
road, then their longer one, then position. It walks down that list and places a roundabout
wherever the piece fits and it is far enough from the ones already placed. Nothing is rolled, so
a given layout always gets the same roundabouts, and neither setting changes the roads
themselves.

**Why the spacing.** BSP cuts often line up, so the straight road through a crossroad can run the
whole city even when it's made of short side streets meeting end to end. Road length alone marked
about half of all crossroads as large; one 32-cell city got 11 roundabouts out of 15 crossroads,
packed into a grid. Measured over 200 seeds at default settings, a 32-cell city now gets about 2
roundabouts (never more than 4), and a 64-cell city about 1.6 (never more than 4). About 6% of
cities have no large intersection at all and get none.

**What gets painted.**
- The five road cells under a roundabout (the center and its four arms) get the **Cover Tile**
  (`Assets/Tiles/Road Cover Tile.asset`, a `RoadCoverTile`). It shows and spawns nothing, but
  `RoadRuleTile` treats it as road, so the road beyond each exit still connects straight into it.
- The four corner cells stay empty, like every other block cell.
- The piece itself is a normal prefab instance under `<Tilemap name> Multi-Cell Pieces`, a sibling
  of the Tilemap inside its Grid. It carries a `MultiCellRoadPiece` component that records its
  Tilemap and the nine cells it covers.
- **Generate** (with Clear City Area First) and **Clear City Area** remove every such piece that
  overlaps the city square, along with the tiles. Both are single undo steps that include the
  pieces.

Roundabouts need **Road Tile** to be a `RoadRuleTile`. Any other tile doesn't treat the cover
tile as road, and the roads leading into each roundabout would stop short of it. The preview says
so, and the feature switches itself off.

## Guarantees

These follow from the algorithm itself, so every seed and every combination of settings meets them:

1. **Every road is exactly one cell wide.** Two parallel roads always have at least **Min Block
   Size** block cells between them. There are never 2×2 squares of road.
2. **No two intersections are side by side.** A T-junction or crossroad never has another
   T-junction or crossroad in a neighboring cell (see [Intersection spacing](#intersection-spacing)).
3. **The network is connected**, with or without the perimeter ring. Every cut after the first
   ends on a road that already exists (see
   [Connectivity](#connectivity-without-a-ring-road)).
4. **With Perimeter Ring Road on, there are no dead ends.** The only bends are the four ring
   corners; everything else is a straight, T-junction or crossroad.
5. **Every road cell matches one of the five pieces**, so the RuleTile never leaves a cell blank.
   The only exception is cells under a roundabout, which get their model from that piece instead.
6. **Blocks are at least Min Block Size on each side**, except when the whole interior is smaller
   than that (then it's a single block).
7. **Same settings + same seed = same city.** See [Determinism](#determinism--seed).

These were checked over 3,000 random combinations of every setting (sizes 3–127, ring road on and
off), plus the Tilemap result of a default 32-cell city.

**Max Block Size is a strong target, not a hard guarantee.** It needs Max Block Size ≥ 2 × Min
Block Size to be reachable at all. Even then, intersection spacing (guarantee 2) takes priority:
if every position for the cut an oversized region needs would put two intersections side by side,
the region stays whole and larger than the maximum. The preview reports these regions. In testing
this never happened at default settings (0 of 1,000 cities at sizes 32 and 64). Across the 3,000
random combinations it affected 5% of cities, almost all with a small Max Block Size (4–8), where
there are very few cut positions to choose from.

## Determinism & seed

The layout depends only on **Side Length**, **Perimeter Ring Road**, the six BSP numbers and
**Random Seed**. `CityBspLayout.Build` is a pure function of those values and uses its own
`System.Random` seeded from **Random Seed**, never Unity's shared `UnityEngine.Random`. As a result:

- The Scene preview shows exactly what Generate will paint. Both read the same cached layout
  through one method (`GetLayout`).
- Regenerating with the same values reproduces the same roads.
- **Reroll** (or typing a new seed) gives a new, equally reproducible layout.
- **Origin Cell**, the Tilemap, the Road Tile and **Clear City Area First** don't affect the
  layout, only where and how it's painted.
- Roundabouts involve no randomness at all: they follow from the layout, **Min Crossing Road** and
  **Min Roundabout Spacing**.

## Window reference

### Target
| Field | Default | Description |
|---|---|---|
| Tilemap | – | The Tilemap to paint on. Its Grid should have **Cell Swizzle = XZY** (the preview warns otherwise). |
| Use Selected GameObject | – | Takes the Tilemap on (or under) the selected Hierarchy object. |
| Create City Tilemap | – | Creates `City Road Grid` (Grid, Cell Swizzle XZY) with a child `Roads` Tilemap and a child `Highways` Tilemap, and assigns both. Undoable. |
| New Tilemap Cell Size | `1` | Cell size used by **Create City Tilemap** only. Use your road prefab footprint (1 for this kit). |
| Road Tile | `New Rule Tile` | Tile painted on every road cell. |

### City
| Field | Default | Description |
|---|---|---|
| Side Length (cells) | `32` | Cells per side of the square city, 3–512. Meters per side = this × the Grid's cell size (shown in the preview). |
| Origin Cell | `(0, 0)` | Tilemap cell of the city's south-west corner. The city covers `Origin` to `Origin + Side Length − 1` on both axes. Move it to put several cities on one Tilemap. |
| Perimeter Ring Road | on | On: a ring road around the city, no dead ends. Off: streets stop at the city edge as dead ends. |

### BSP
| Field | Default | Description |
|---|---|---|
| Max Split Depth | `6` | How many levels of splitting are allowed. Allows up to 2^depth blocks before the other settings cut it back. `0` = no cuts unless Max Block Size forces them. |
| Min Block Size (cells) | `3` | Smallest block width/height, and so the smallest gap between parallel roads. A region is only cut if both halves keep this many cells. |
| Max Block Size (cells) | `10` | `0` = off. Regions larger than this are split, overriding depth and stop chance. Only reachable when ≥ 2 × Min Block Size (the preview says so otherwise). Intersection spacing takes priority, so a region can occasionally stay larger; see [Guarantees](#guarantees). |
| Split Jitter | `0.2` | `0` = cut at the exact middle (evenly halved blocks). Up to `0.5` = the cut can move that fraction of the region either side of center. Min Block Size always wins. |
| Split Axis Bias | `0.75` | Chance of cutting across the longer side. `1` = always (squarish blocks), `0.5` = coin flip (more long, thin blocks). |
| Stop Probability | `0.2` | Chance a region stops early and stays a big block, the main source of size variety. The root split is exempt; Max Block Size overrides it. |
| Random Seed / Reroll | `12345` | Seed for all random choices. **Reroll** picks a random new one. |

### Variety
| Field | Default | Description |
|---|---|---|
| Roundabouts | on | Put a 3×3 roundabout on every large intersection where it fits. Off = plain crossroads everywhere. See [Roundabouts](#6-roundabouts-33-pieces). |
| Min Crossing Road | `0.5` | What counts as a large intersection: both straight roads through the crossroad must be at least this fraction of the side length. The label shows the result in cells (16 for a 32-cell city). Higher = fewer roundabouts, only where the main avenues cross. |
| Min Roundabout Spacing | `0.33` | How far apart roundabouts must be, as a fraction of the side length, measured along either axis. The label shows cells (11 for a 32-cell city). Higher = fewer, more spread out. Never less than 3 cells, so pieces can't overlap. |
| Roundabout Prefab | `Assets/Prefabs/Roads/road-roundabout.prefab` | The 3×3 piece, centered on its pivot, with an exit in the middle of each side. |
| Cover Tile / Create | `Assets/Tiles/Road Cover Tile.asset` | Painted on the road cells under each roundabout, on the bottom cell of every highway ramp, and (on the Highway Tilemap) on the top cell of every two-cell ramp. **Create** makes the asset at that path if it's missing. |

### Highways
Six settings, a Highway Tilemap and a Highway Tile that lay an elevated highway network over the
finished street grid, on its own Tilemap layer, linking the city's far apart BSP clusters. Documented in full, with its own guarantees and
troubleshooting, in [CityHighwayGenerator.md](CityHighwayGenerator.md#window-reference).

### Scene preview
**Show Preview** draws the layout in the Scene view before you paint, using the Tilemap's own
`CellToWorld`, so it lines up with the grid whatever its cell size or position:

| Color | Meaning |
|---|---|
| White outline | City boundary |
| Green outline / fill | A block (toggle with **Show Blocks**) |
| Light gray | Straight |
| Yellow | Bend |
| Orange | T-junction |
| Cyan | Crossroad (4-way) |
| Red | Dead end (only with the perimeter off) |
| Purple 3×3 | A roundabout |
| Blue outline | A highway cluster (toggle with **Show Clusters**) |
| Blue, at deck height | A highway deck cell |
| Pink, at ramp height | A highway ramp cell |
| Magenta | No matching piece (should never appear) |

Cities with more than 10,000 road cells skip the per-cell squares in the preview to stay fast.
The boundary and blocks still draw.

The info box underneath reports the city size in cells and meters, the covered cell range, road
cell count and share of the city, number of cuts and blocks, the smallest and largest block side,
a count of each piece type, how many splits Max Block Size forced, and how many regions were left
whole for intersection spacing. Warnings appear for: no interior roads, Max Block Size that can't
be reached (under 2 × Min Block Size), a Tilemap whose Grid isn't XZY, and a Road Tile that isn't a
RuleTile.

### Action bar
Pinned to the bottom of the window, outside the scrolling settings, so it is always visible:

| Element | Description |
|---|---|
| Summary line | What Generate will paint: city size and origin, street cells, roundabouts, highway links (deck cells and ramps). Replaced by a warning saying what's missing when Generate can't run. |
| Clear City Area First | Default on. Erase every tile on both the street and Highway Tilemaps, and every roundabout, inside the city square before painting, so regenerating replaces the old city. Its label shows what will be replaced. Off = paint over what's there. |
| **Generate** / **Regenerate** | Enabled once a Tilemap and Road Tile are set (the tooltip says why not otherwise). Reads **Regenerate** when Clear City Area First is on and the square already has content. Paints streets, roundabouts, ramp interchanges and highways, logs a summary and flashes a notification. One Undo step. |
| **Clear City Area (n)** | Shows how many tiles and pieces are in the square, and is disabled when there are none. Asks for confirmation, listing exactly what goes (the dialog can be turned off for the session), then erases the square on both Tilemaps and removes its roundabouts and multi-cell pieces. Uses the current Origin Cell and Side Length; anything outside the square is never touched. One Undo step. |

The counts are cached and refreshed only when the scene, either Tilemap, or the square changes, so a
large city doesn't pay for them on every repaint.

### Undo / Redo
Both buttons record the Tilemap before changing it, so **Ctrl+Z** / **Ctrl+Y** step back and
forth through them. Unity's undo restores the Tilemap's *tile data* but not the road prefabs the
RuleTile spawned. Left alone, undoing a Clear would bring the tiles back with no 3D roads on them.
The tool therefore hooks `Undo.undoRedoEvent`: whenever the step being undone or redone is one of
its own ("Generate City Roads (BSP Tilemap)" or "Clear City Roads (BSP Tilemap)"), it calls
`Tilemap.RefreshAllTiles()` on the loaded Tilemaps, which respawns exactly the right pieces. The
hook is registered when scripts load, so it works even with the window closed, and it ignores
every other undo step. The roundabouts are ordinary GameObjects, which Unity's undo restores or
removes by itself, in the same step as the tiles.

## Recipes

| Look | Settings |
|---|---|
| Regular downtown grid | Split Jitter `0`, Split Axis Bias `1`, Stop Probability `0`, Max Block Size = 2 × Min Block Size |
| Varied "planned city" (default) | Jitter `0.2`, Axis Bias `0.75`, Stop `0.2`, Max Block Size `10` |
| Big blocks with a few avenues | Min Block Size `6`+, Max Split Depth `3`, Max Block Size `0` |
| Long, thin blocks | Axis Bias `0.5`, Jitter `0.4` |
| Dense old town | Min Block Size `1`–`2`, Max Split Depth `8`, Stop `0.1` |
| Several districts | Generate once per district on the same Tilemap, with Perimeter on and a different seed each time. Space the **Origin Cell**s **Side Length − 1** apart, so neighboring districts *share* one ring road. The RuleTile joins them. (A full Side Length apart would put two ring roads side by side as a two-cell-wide band.) Each district spaces only its own streets, so check the shared road for intersections one cell apart. |

`Min Block Size 1` is valid (one-cell blocks between roads), but leaves no room for buildings.
Around `3`+ reads better with the Kenney kit.

## Coordinate space

- City-local cell `(x, y)` is painted at Tilemap cell `(Origin.x + x, Origin.y + y, 0)`.
- With Cell Swizzle XZY, Tilemap +X is world +X (east) and Tilemap +Y is world +Z (north).
- Road prefabs spawn at the cell center (Tilemap tile anchor `(0.5, 0.5)`), on the Grid's own Y.
  Move the `City Road Grid` object to raise, lower or shift the whole city.
- Don't rotate or scale the Grid: the road pieces are built for a flat, axis-aligned, 1-unit grid.

## Performance

Building the layout is roughly linear in the number of cells, and it is cached: it's only rebuilt
when a layout setting changes, not on every Scene repaint. Painting is one batched `SetTiles` call.
The slow part of a large city is Unity creating one prefab instance per road cell through the
RuleTile. At Side Length 512 that's many thousands of objects, so expect a pause on Generate and
consider several smaller cities instead.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Road pieces stand on their edge | The Road Tile is a stock `RuleTile`, not a `RoadRuleTile`. Use `New Rule Tile`, or change its script to `RoadRuleTile` in the Inspector's debug mode. |
| Roads float, sink or sit sideways, not where the preview shows | The Grid's Cell Swizzle isn't `XZY` (the preview warns). Use **Create City Tilemap** or fix the Grid. |
| Roads overlap or leave gaps | The Grid cell size doesn't match the prefab footprint (1 for this kit). |
| A lone straight piece where no road should be | A hand-painted cell with no road neighbors matches no rule, so it spawns the tile's Default GameObject (`road-straight`). Generated cities never have these. Erase the stray tile. |
| Old roads remain after regenerating | **Clear City Area First** is off, or the city moved or shrank so the old roads are outside the current square. Clear them with the old Origin/Side Length, or erase them in the Tile Palette. |
| "No interior roads" warning | The interior is narrower than `2 × Min Block Size + 1` cells, or Max Split Depth is 0. |
| Two road pieces on every cell | The RuleTile *and* Road Generator (Tilemap) are both placing prefabs. Use one or the other. |
| Two intersections right next to each other | A single Generate never produces this. It comes from hand-painted tiles, or from two districts generated separately that share a ring road: each Generate only spaces its *own* streets, so streets from two districts can meet the shared road one cell apart. Erase or move one of them in the Tile Palette, or Reroll one district. |
| A block bigger than Max Block Size | Either Max Block Size is under 2 × Min Block Size, or every cut position in that region would have put two intersections side by side (the preview counts these as "regions left whole"). Reroll, or raise Max Block Size slightly. |
| No roundabouts appear | The preview's "Roundabouts are off" note says why: Roundabouts is unticked, the prefab or Cover Tile is missing (click **Create**), or Road Tile isn't a `RoadRuleTile`. Otherwise the city has no large intersection: no crossroad where both roads reach Min Crossing Road. Lower Min Crossing Road, or Reroll for a layout where two avenues cross. |
| Too many roundabouts | Raise **Min Roundabout Spacing** (fewer, further apart) or **Min Crossing Road** (only the longest avenues count). |
| Roads stop short of a roundabout | The cells under the piece aren't the Cover Tile, or Road Tile isn't a `RoadRuleTile`. Regenerate with the window's defaults. |
| A roundabout left floating after erasing tiles by hand | The Tile Palette only edits tiles, not the roundabouts. Delete the piece from `<Tilemap> Multi-Cell Pieces`, or run Clear City Area. |

## Limitations

- **Square cities only.** For a city bounded by a curved road, use the
  [BSP Sub-Road Generator](BspSubRoadGenerator.md) inside a spline loop.
- **All streets are one cell wide and run straight north–south or east–west.** There are no
  avenues of different widths, no diagonals and no curves. That's all the 1×1 road kit and the
  four-rotation RuleTile can express.
- **Intersection spacing makes layouts a little more grid-like.** When a cut has to move off a
  spot beside another street, it lines up with that street (a crossroad) about as often as it
  moves away. Measured over 500 seeds at default settings, a 32-cell city averages about 40
  T-junctions and 9 crossroads, and a 64-cell city about 112 and 19. Pure BSP would be almost all
  T-junctions.
- **Blocks are not stored in the scene.** `CityBspLayout` computes them (they're what the green
  preview outlines show), but Generate paints only roads. Placing buildings on blocks would be a
  follow-up tool built on `CityBspLayout.Layout.Blocks`.
- **Connection to roads outside the city depends on the Tilemap.** Neighbors on the same Tilemap
  connect automatically; roads made by the prefab-placing tools (`Road`, `SubRoads` objects) are
  unaware of the city.
- **Flat ground only for the streets.** They have no height, ramps or bridges — that is what the
  [highway layer](CityHighwayGenerator.md) adds, above them.
- **Roundabouts live outside the Tilemap.** They are GameObjects placed next to the Tilemap, not
  tiles, so hand-editing with the Tile Palette doesn't move or remove them. The Tilemap Road
  Generator reads only tiles and knows nothing about them. The piece also assumes a Grid cell
  size of 1 (it is 3 × 3 units); the preview warns otherwise.
- **Roundabouts only replace crossroads.** The piece has four exits, so it never replaces a
  T-junction: its fourth exit would lead into a block.

## Files touched by this feature
- `Assets/Editor/CityRoadTilemapWindow.cs`: the tool (UI, Scene preview, Tilemap painting, undo).
- `Assets/Editor/CityBspLayout.cs`: the integer-grid BSP layout algorithm.
- `Assets/Editor/RoadGridPlacer.cs`: existing; used to classify cells by piece type for the
  preview colors and counts, and by the highway layer to resolve its own pieces.
- `Assets/Editor/RoundaboutPlanner.cs`: picks which crossroads become roundabouts.
- `Assets/Scripts/Tiles/RoadRuleTile.cs` and `Assets/Tiles/New Rule Tile.asset`: the road tile that
  turns painted cells into prefabs (and treats the cover tile as road).
- `Assets/Scripts/Tiles/RoadCoverTile.cs` and `Assets/Tiles/Road Cover Tile.asset`: the invisible
  tile under each roundabout.
- `Assets/Scripts/Tiles/MultiCellRoadPiece.cs`: the marker on each roundabout, used to find and
  replace it.
- `Assets/Editor/HighwayPlanner.cs`, `Assets/Editor/HighwayBuilder.cs`: the elevated highway layer.
- `Docs/CityBspTilemapGenerator.md`: this document.
- `Docs/CityHighwayGenerator.md`: the highway layer.
