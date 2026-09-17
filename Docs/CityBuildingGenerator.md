# City Building Generator

An extension of the [City Road Generator (BSP Tilemap)](CityBspTilemapGenerator.md) that **fills the
blocks with buildings** from the City Kit (Commercial) models in `Assets/Models/Building`. The
buildings are tiles, like the streets and the expressways. Each building type is a **Building Tile**
painted on a third Tilemap layer, **Buildings**, in the same Grid. You can repaint, rotate or erase
buildings with the Tile Palette like any other tile.

- **Tool**: the **Buildings** section of `Tools > OpenCity > City Road Generator (BSP Tilemap)`
- **Planner**: `Assets/Editor/BuildingPlanner.cs` (lots, land value, choice of building — pure, no
  Editor or Tilemap dependency, seeded)
- **Tile + piece builder**: `Assets/Editor/BuildingTileBuilder.cs` (also `Tools > OpenCity > Create Building Tiles`)
- **Tiles**: `Assets/Scripts/Tiles/BuildingTile.cs`, `Assets/Scripts/Tiles/BuildingCoverTile.cs` →
  assets in `Assets/Tiles/Buildings/`
- **Pieces**: `Assets/Prefabs/Buildings/Tile Pieces/` (generated from the models)
- **Namespace**: `OpenCity.EditorTools` (tiles: `OpenCity.Tiles`)

## Where this fits

| Layer | Tilemap | Tile |
|---|---|---|
| Streets | `Roads` | road RuleTile |
| Expressways | `Highways` | [highway RuleTile](CityHighwayGenerator.md) |
| **Buildings** | **`Buildings`** | **one Building Tile per building type** |

All three share one Grid (Cell Swizzle XZY, cell size 1) and one ground plane. The kit is built to the
same 1-unit grid as the road kit: a road piece is 1 × 1, a small building is about 0.9 × 1. Streets,
expressways and buildings are planned and painted in the same **Generate**, in one Undo step.

## Quick start

1. Set up the city as usual. **Create City Tilemap** now creates the `Roads`, `Highways` and
   `Buildings` Tilemaps.
2. Scroll to **Buildings** and click **Create** next to **Building Tiles**. This builds one piece and
   one tile per model, plus its size variants, into `Assets/Prefabs/Buildings/Tile Pieces/` and
   `Assets/Tiles/Buildings/`.
3. Watch the Scene view (**Show Buildings**): every building is a box coloured by tier, as tall as the
   tier, with a white line along its front.
4. Adjust **Density** and **Downtown Size**, and click **Reroll** next to **Building Seed** until you
   like the skyline. The street layout doesn't move.
5. Click **Generate**.

## How it works

The 48-cell default city, seed 0 (`#` street, `X` expressway deck, `l`/`m`/`H`/`T` low, mid, high and
tower, `f` filler, `v > ^ <` the front of a building facing south, east, north or west, `.` open):

```
################################################
#m^m^>#<^>#<^.l^>#<^>#<m^^.^>#H<^>#<^^>#<^m^l^^#
#mmmm>#<m>#<...m>#<HH#<mmffH>#HHm>#<..>#m<....>#
#<mv.m#lmm#l...mm#mH>#HHfffHHX<.mm#l..m#mm....>#
#vmm.v#lv>#.....>#.HH#H<fffHHX#####m<.>#<.....>#
###########<....l#<.>#HHfffH>X<^H>#mm.>#lmvlvvv#
   ...
#m^H^>#m^>#mmmmv>#vv>#T<fffT>X#####T<H>#mm....>#
#m<HH>#mm>############TTfffTTX<^^>#TTHH#.....m>#
#mm..>#<m>#T<T^T>#<m>#T<fffT>XH<Hv#HvTv#<..Hvmm#
   ...
```

Every street is lined with facades facing it. Towers (`T`) and high-rises (`H`) cluster downtown
along the main avenue, which carries the expressway (`X`). (This map predates frontage packing: the
`f` filler behind the street fronts is no longer built. Blocks are now filled from the street with
deeper buildings, and only blocks too deep to reach from their streets keep a courtyard.)

### 1. Building Tiles: one tile per building type

A `BuildingTile` has a **piece** (the prefab it spawns), a **footprint** in cells, a **tier** and a
**weight**.

- **The anchor cell.** The tile goes on the **south-west cell** of the footprint. The other footprint
  cells get the invisible `BuildingCoverTile`, so they read as taken.
- **Rotation.** The building turns with the cell's **transform matrix**, the same thing the Tile
  Palette's rotate buttons change. Rotation 0 faces **south** (cell −Y, world −Z), and each quarter
  turn counter-clockwise faces east, north, then west.
- **Spawning.** The piece is moved to the **centre of the rotated footprint** and turned about world
  Y. On an XZY grid a turn about the cell's Z axis is a turn about world Y, so a building never ends
  up on its side.
- **Spawn timing.** The Tilemap spawns a tile's GameObject **before** the cell's rotation is set, both
  when a rotated tile is painted and when a scene loads. So the rotation `StartUp` reads is still the
  default, and three things place the piece again once the rotation is there:
  - **In the Editor**, `StartUp` queues a re-place for right after the current operation (this covers
    Generate, the Tile Palette and scene loads).
  - **On a refresh**, `GetTileData` re-places a piece that has already spawned.
  - **In Play Mode and builds**, the `BuildingTilemap` component on the layer re-places every building
    in `Start`. The generator adds it to the Buildings Tilemap; add it yourself to any other Tilemap
    you paint buildings on. After changing rotations from code at runtime, call
    `BuildingTilemap.Realign()`.

**Tiers** decide where a building may go:

| Tier | Where | Kit models (as measured) |
|---|---|---|
| Low | outskirts | `building-c`, `building-e` (≤ 1 cell tall) |
| Mid | ordinary streets | `building-a`, `b`, `d`, `f`, `g`, `h`, `i`, `k` (1–1.8 cells) |
| High | avenues, around downtown | `building-j`, `l`, `m`, `n` (1.8–3.2 cells) |
| Tower | downtown | `building-skyscraper-a` … `e` |

Heights are measured **after fitting** (see below), which is why a scaled-up `building-j` counts as high.
The `Filler` tier is no longer generated or placed. Filler tiles left from an older build are ignored and
can be deleted.

### 2. Pieces: made from the models

`BuildingTileBuilder` wraps each model in a piece prefab:

- **Root.** The piece's root is the **centre of the footprint**, and the model sits on `y = 0`.
- **Facing.** The model's **front faces −Z**. The kit models already do (entrances, awnings and shop
  windows are on that side), so no turn is needed.
- **Fitting to a footprint.** The kit's models don't come in whole cells: a skyscraper is 1.36 cells
  across, `building-i` 1.24 × 1.30. Rounding up left lots like those half empty, so the builder
  **fits** each model instead:
  1. **Try every footprint** from 1 × 1 to 3 × 3, each at the uniform scale that makes the model span
     94% of it along its tighter axis. The last 6% is the gap between neighbours.
  2. **Skip** any scale outside 0.7–1.4×. Beyond that range, windows and doors stop matching the
     buildings around them.
  3. **Score** each candidate: the share of the lot left empty, plus 0.5 × |ln scale|.
  4. **Keep the cheapest.** On a tie, the smaller footprint wins.
  5. **Stretch.** Along its looser horizontal axis, the model may stretch up to 1.15× past the uniform
     scale to fill its width. That's invisible on box-shaped buildings, and the height is never
     stretched.

  The results:

  | Model | Measured | Footprint | Scale | Lot filled |
  |---|---|---|---|---|
  | `building-a` | 0.88 × 0.94 | 1 × 1 | 1.0 | 88% |
  | `building-c` | 0.88 × 1.10 | 1 × 1 | 0.86 (width ×1.15) | 82% |
  | `building-i`, `building-m` | 1.24 × 1.30, 1.24 × 1.24 | 1 × 1 | 0.72, 0.76 | 88% |
  | `building-j` | 2.08 × 1.34 | 3 × 2 | 1.35 | 88% |
  | `building-l` | 1.37 × 1.40 | 2 × 2 | 1.34 | 88% |
  | `building-n` | 2.32 × 1.82 | 3 × 2 | 1.03 | 86% |
  | `building-skyscraper-a` … `d` | ≈ 1.36 × 1.36 | 2 × 2 | ≈ 1.36 | 88% |
  | `building-skyscraper-e` | 1.30 × 1.24 | 1 × 1 | 0.73 | 88% |
- **Front alignment.** Street buildings are **pushed to the front edge** of their footprint, 0.02 short
  of it, so the facades line the street. Towers stay **centred**, which leaves them a plaza.
- **Size variants.** Every building also gets a tile for each **other footprint it can fill** (up to
  3 wide and 4 deep), saved in `Variants/` subfolders of the tile and piece folders and named
  `building-a-1x2` and so on. A footprint qualifies when the model's uniform fit scale is within
  0.7–1.4×, the model spans the lot's width within the usual 1.15× stretch, and it spans the depth
  within a **2× depth stretch** (1.15× for towers). The facade keeps its proportions; only the sides
  get longer. This is what lets a lot reach deep into the block from the street. A variant takes its
  tier and weight from its building's tile, so set a building's weight to 0 to drop its variants too.
  Variants are regenerated on every build, and ones that no longer qualify are deleted. With the kit:
  `a`, `c`, `d`, `f`, `h`, `i`, `m` → 1 × 2; `e` → 2 × 2; `k` → 3 × 2; `j` → 2 × 2, 3 × 3;
  `l` → 2 × 3, 2 × 4; `n` → 2 × 2, 2 × 3, 3 × 3, 3 × 4. The skyscrapers get none.

**Rebuild** regenerates every piece in place (same GUIDs). It **keeps** each tile's footprint, tier
and weight, so edits you make in the tile's Inspector survive, and refits the model's scale to the
footprint you set. **Refit** also chooses every footprint and tier again (weights are still kept).
New models dropped into `Assets/Models/Building` get a tile on the next Rebuild.

### 3. Land value: where the tall buildings go

Every block gets a value from 0 to 1:

| Part | Weight | Measured as |
|---|---|---|
| Centrality | 0.55 | distance from the block's centre to the city's centre |
| Avenue importance | 0.25 | the best street bounding the block, by BSP depth: 1 for the depth-0 avenue, ½ for depth 1, … (the ring counts as 0) |
| Expressway access | 0.2 | the block's street distance to the nearest [expressway ramp](CityHighwayGenerator.md#7-block-coverage) |

Without expressways, the value is 0.65 × centrality + 0.35 × avenue importance. **Downtown Size** bends
the curve: at 0.5 the value is used as is, 1 raises most of the city, and 0 keeps only the very centre
high.

Each lot rolls its tier from the block value with a little jitter: tower ≥ 0.72, high ≥ 0.5, mid ≥ 0.25,
otherwise low. Neighbouring lots therefore mostly match, with the odd taller or lower building.

### 4. Lots: every building fronts a street

For each block:

1. **Frontages.** Each block edge that faces a road along at least half its length is a frontage. They
   are processed **best street first** (lowest BSP depth), then the longer edge, so the most
   important street claims the corners.
2. **Depth share.** When the street across the block is also a frontage and hasn't been packed yet,
   this frontage may build at most **half the block's depth** (rounded up). The opposite frontage gets
   the rest, plus any cells this one didn't use.
3. **Packing a frontage.** Every cell along the edge first rolls its lot **tier** from the block value.
   On low and mid streets, a **Density** roll can make the cell an **open lot**. Downtown is always
   built. Then a dynamic program over the edge picks the **row of footprints that covers the most
   block cells**:
   - A footprint may go at a cell if it fits along the edge, is no deeper than the depth share, spans
     no open lot, and all its cells are free (corners claimed by a better street are taken).
   - It scores its **area** × how well the best building of that footprint suits the lot's tier (same
     tier 1, one lower 0.6, one taller 0.4, two or more lower 0.25, two or more taller not allowed) ×
     a 0.9–1.1 jitter, so equally good packings vary with the seed.
   - Because a building always scores more than a gap, a cell is only left empty when it is an open
     lot or when **no** footprint fits it.
4. **Choosing the building.** Each chosen footprint gets one of the tiles with that footprint, picked
   by weight × tier fit. The building just placed on this street gets ×0.1 (little repetition). The
   building is turned to face the street, flush with the block edge.
5. **Nothing in the middle.** Every building touches its street. Cells no street-front building can
   reach (the middle of a block deeper than two of the deepest lots) stay an **open courtyard**. No
   building is ever placed there without a way in.

**Roundabout clearance.** The corners of a roundabout's 3 × 3 footprint are block cells. They are
reserved, so nothing is built into the roundabout.

**Determinism.** Every block draws from its own random stream, seeded from the **Building Seed** and
the block's index. The same city with the same seed always gets the same buildings, and changing the
Building Seed never moves a street.

## Guarantees

Checked in the Editor on a generated 32-cell city (301 buildings):

- **Every spawned building** sits on the centre of its rotated footprint with the matching rotation.
  That held right after Generate, after saving and reopening the scene, and for a 2 × 1 building
  painted then rotated the way the Tile Palette does it.
- **Street-level screenshots:** the entrances, awnings and shop windows face the street.

Checked headless over **240 cities** (before fitting and interior packing were added) (sides 32, 48, 64 and 128, ring road on and off, 30 seeds each,
expressways on), with a catalog matching the kit's measured footprints and tiers. There were zero
violations of:

1. **No building overlaps another**, or a road, or leaves its block.
2. **Every building's footprint matches its tile** turned by its rotation.
3. **Every street-front building has a road directly in front of its front edge.**
4. **Every street-front cell was filled** (0 unfilled frontage) or deliberately left an open lot.

## How full the blocks are

On a test city (625 block cells, Density `0.85`), frontage packing with size
variants assigns **91%** of block cells to a building (206 buildings, 39 open lots, 0 unfilled
street-front cells), with every building fronting a street. What's left is the open lots and the
courtyards of blocks too deep to reach from their streets.

## Window reference

### Buildings
| Field | Default | Description |
|---|---|---|
| Buildings | on | Off = no buildings, and the planning is skipped. |
| Building Tiles / Create / Rebuild / Refit | — | The tiles in `Assets/Tiles/Buildings`, counted by tier. **Create**/**Rebuild** generates the pieces and tiles from the models, keeping edited footprints, tiers and weights. **Refit** chooses every footprint, fitted scale and tier again. |
| Density | `0.85` | The chance a lot on an ordinary street is built. The rest stay open. Downtown is always built. |
| Downtown Size | `0.5` | How much of the city counts as downtown. Higher = a taller skyline over more of the city. |
| Building Seed / Reroll | `777` | Reshuffles the buildings without moving a street. |
| Building Tilemap | empty | The Tilemap layer the buildings are painted on. Empty = use or create a `Buildings` sibling of the street Tilemap. Must not be the street or highway Tilemap. |

### Scene preview
**Show Buildings** draws every building as a box, coloured and sized by its tier:

| Colour | Tier | Box height |
|---|---|---|
| Green | Low | 0.8 |
| Yellow | Mid | 1.3 |
| Orange | High | 2.2 |
| Red | Tower | 4 |

A white line marks each building's front. The info box reports the building count by tier, the share
of block land built and the open lots.

## Painting by hand

Building Tiles are ordinary tiles: add them to a Tile Palette and paint on the `Buildings` Tilemap.

- **Rotation.** Use the palette's **rotate** buttons (`[` and `]`) to face a building. Keep a
  `BuildingTilemap` component on the Tilemap so rotated buildings stay put in Play Mode.
- **Placement.** Paint a multi-cell building on the **south-west cell** of the space it should occupy,
  after rotating. The tile doesn't paint its own cover tiles, so leave the other cells empty.
- **Erasing.** Erase the anchor cell to remove a building. A multi-cell building's leftover cover
  tiles are invisible, and the next Clear City Area removes them.

## Recipes

| Look | Settings |
|---|---|
| Dense downtown core, low outskirts (default) | Density `0.85`, Downtown Size `0.5` |
| Big-city skyline everywhere | Downtown Size `0.9`, Density `1` |
| Solid, fully built blocks | Density `1`, Max Block Size `6`–`8` (blocks no deeper than two deep lots) |
| Small town | Downtown Size `0.1`, Density `0.6` |
| Courtyard blocks | Min Block Size `8`+ |
| Fewer towers | Lower the towers' **weight** in their tiles, or Downtown Size `0.3` |
| Only certain models | Set the unwanted tiles' **weight** to `0` |

## Limitations

- **One building per footprint.** There are no attached corner buildings or L-shaped lots, and a
  corner building faces one street only.
- **Fitted models are rescaled.** Scales run from 0.7× to 1.4×, so a scaled building's windows are
  slightly larger or smaller than its neighbours'. Set a tile's footprint by hand and Rebuild to change
  its scale.
- **Open lots and courtyards are bare ground.** Nothing is painted there yet: no parks, parking,
  pavement or props (the kit's `detail-*` awnings and parasols are unused).
- **Deep variants are stretched.** A size variant's sides are up to 2× longer than the model's; the
  facade isn't. Lower `BuildingTileBuilder.MaxDepthStretch` for fewer, less stretched variants.
- **Very deep blocks keep a courtyard.** The deepest mid-rise lot is 2 cells, the deepest high-rise lot
  4, so a block deeper than twice that has an empty middle. Lower Max Block Size to avoid it.
- **The stored BSP tree doesn't record buildings.** Blocks are stored, buildings are tiles; read the
  `Buildings` Tilemap for what was built.
- **Assumes a Grid cell size of 1**, like the road and highway kits.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| "Buildings are off: there are no Building Tiles yet" | Click **Create** next to Building Tiles. |
| "Building Tilemap is the street or highway Tilemap" | Clear the field; Generate creates a `Buildings` layer. |
| Buildings face away from their street | A model's front isn't on its −Z side (the kit's are). Change `BuildingTileBuilder.ModelFrontYaw` and click **Rebuild**. |
| Rotated or multi-cell buildings face south and sit off their lot in Play Mode | The Tilemap has no `BuildingTilemap` component. Add it (the generator does this for its Buildings layer). |
| A building overlaps its neighbour or the street | Its tile's footprint was edited smaller than the fit allows (scales stop at 0.7×). Enlarge it and Rebuild, or click **Refit**. |
| Blocks still look hollow | The blocks are deeper than their streets' lots reach. Click **Rebuild** so the size variants exist, raise Density, or lower Max Block Size. |
| A building looks too big or too small next to its neighbours | Its fitted scale is near 0.7× or 1.4×. Give its tile a different footprint and Rebuild, or lower its weight. |
| "street-front cell(s) have no building that fits" | No tile of any tier fits that gap or block depth. Keep a few 1 × 1 tiles at weight > 0. |
| Tile edits don't show in the preview | The preview reloads tiles when the project changes; save the tile asset (Ctrl+S). |

## Files touched by this feature

- `Assets/Scripts/Tiles/BuildingTile.cs`: the building tile (footprint, tier, weight; spawns and turns its piece).
- `Assets/Scripts/Tiles/BuildingCoverTile.cs`: marks the other cells of a multi-cell footprint.
- `Assets/Scripts/Tiles/BuildingTilemap.cs`: re-places spawned buildings once a scene has loaded.
- `Assets/Editor/BuildingPlanner.cs`: land value, frontages, lots and choice of building.
- `Assets/Editor/BuildingTileBuilder.cs`: builds pieces and tiles from the models.
- `Assets/Editor/CityRoadTilemapWindow.cs`: the **Buildings** section, the Buildings Tilemap, preview,
  and painting inside Generate/Clear.
- `Assets/Prefabs/Buildings/Tile Pieces/*`, `Assets/Tiles/Buildings/*`: generated.
- `Docs/CityBuildingGenerator.md`: this document.
