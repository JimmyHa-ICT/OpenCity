# City Highway Generator (avenue expressways)

An extension of the [City Road Generator (BSP Tilemap)](CityBspTilemapGenerator.md) that turns the
city's **main avenues into elevated expressways**. It reads the city's road hierarchy straight out of
the BSP tree: the cut that splits the whole city is its biggest avenue, the cuts that split each half
are the next tier, and so on. The top tiers are decked over from end to end. They meet each other in
**interchanges**, come down onto their own avenue at a **free end**, and have **on/off ramps onto
cross streets** along the way, so every part of the city is served.

The highway is painted exactly the way the streets are: as tiles, with a **Highway RuleTile**, on its
**own Tilemap layer** above the street Tilemap.

- **Tool**: the **Highways** section of `Tools > OpenCity > City Road Generator (BSP Tilemap)`
- **Planner**: `Assets/Editor/HighwayPlanner.cs` (corridors, interchanges, ramps, block coverage —
  pure, no Editor or Tilemap dependency, no randomness)
- **Painter + tile asset builder**: `Assets/Editor/HighwayBuilder.cs`
- **RuleTile**: `Assets/Scripts/Tiles/HighwayRuleTile.cs` → asset `Assets/Tiles/Highway Rule Tile.asset`
- **Tile pieces**: `Assets/Prefabs/Roads/Highway/Tile Pieces/` (generated from the Highway kit)
- **Avenue ranking comes from**: the BSP tree, `CityBspLayout.Layout.Nodes` (`Assets/Editor/CityBspLayout.cs`)
- **Namespace**: `OpenCity.EditorTools` (tile: `OpenCity.Tiles`)

## Where this fits

| | |
|---|---|
| [City Road Generator (BSP Tilemap)](CityBspTilemapGenerator.md) | The street grid, painted on the `Roads` Tilemap with the road RuleTile |
| **City Highway Generator** | **The top avenues decked over as expressways, painted on the `Highways` Tilemap with the highway RuleTile** |

Both layers live in the same Grid on the same ground plane. The highway's tile pieces carry their own
height, so the `Highways` Tilemap needs no offset. Because each RuleTile only sees tiles on its own
Tilemap, a deck running over a street never changes the street's shape, and the street never
changes the deck's.

The two run in the same **Generate**, in one Undo step. The only tiles the highway puts on the street
layer are `RoadCoverTile`s under its ramps (see [Ramps](#4-end-ramps-flyovers-coming-down)).

## Quick start

1. Open `Tools > OpenCity > City Road Generator (BSP Tilemap)` and set the city up as usual
   (**Create City Tilemap** creates both the `Roads` and the `Highways` Tilemap).
2. Scroll to **Highways**. Next to **Highway Tile**, click **Create** once. This builds the six tile
   pieces into `Assets/Prefabs/Roads/Highway/Tile Pieces/` and writes
   `Assets/Tiles/Highway Rule Tile.asset`. (Also available as `Tools > OpenCity > Create Highway Rule Tile`.)
   Make sure the Variety section has a **Cover Tile** (click **Create** there if not).
3. Watch the Scene view: **blue cells** are the elevated deck (drawn at deck height), **pink cells**
   are the ramps, drawn at the height each cell climbs to.
4. **Avenue Levels** decides how many tiers of avenues become expressways. **Ramp Spacing** decides
   how often they come down to the streets. Tick **Show Coverage** to shade every block by how far it
   is from a ramp.
5. Click **Generate**.

If **Highway Tilemap** is empty, Generate uses a sibling Tilemap called `Highways` in the street
Tilemap's Grid, or creates one. **Ctrl+Z** undoes the streets, roundabouts and expressways together.

## How it works

The 48-cell default city, seed 12345 (`#` street, `X` deck over a street, `R` ramp):

```
################################################
#.....#...#.....R.....#.....#.....#.......#....#   <- end ramp: the level-1 avenue comes down
#.....#...#.....R.....#.....#.....#.......#....#      before it reaches the ring road
#.....#...#.....X.....#.....#.....#.......#....#
################X###############################
#...#...#...#...X.....#.......#.....#..........#
################X###############################
#.......#...#...X...#....#.......#.....#...#...#
##############RRX...#....#.......#.....#...#...#   <- access ramp onto a cross street
#...#...#...#...X...R....#.......#.....#...#...#   <- access ramp on the level-2 avenue
#...#...#...#...X...R....#.......#.....#...#...#
#...#...#...#...XXXXXXXXXXXXXXXXXXXXXXXXXXXXXRR#   <- level-2 avenue meets level 1: interchange
#...#...#...#...X......#...#...R.......#.......#
#...#...#...#...X......#...#...R.......#.......#
################X###########...#.......#.......#
   ...
```

### 1. Ranking the avenues: the BSP tree

Every street of the city is a BSP cut, and a cut's **depth** in the tree says how much of the city it
divides. The depth-0 cut runs the full width or height of the city. Each depth-1 cut runs the full
extent of one half. Deeper cuts get shorter and shorter. That makes depth a ready-made **road
hierarchy**: **Avenue Level 1** is depth 0, level 2 is depth 1, and so on.

`CityBspLayout.Layout.Nodes` records every region with its cut, depth and parent/child links (the same
tree the [BSP Tree view](CityBspTilemapGenerator.md#bsp-tree) shows), so the planner reads it
directly. Nothing is guessed from the tile grid.

### 2. Corridors: which avenues become expressways

A cut is a **candidate** if its depth is below **Avenue Levels** and it is at least **Min Avenue
Length** cells long. Candidates are taken **highest level first**, then longest, then in tree order.
One is dropped if it runs **parallel to an expressway already accepted, closer than Min Expressway
Spacing**, over a stretch where the two overlap. That stops two avenues a few blocks apart from both
getting a viaduct; the more important one wins.

An accepted corridor is decked over **straight, above its own avenue**. It never turns and never
leaves the street, so it takes no block land and needs no routing.

With **Ring Expressway** on (it needs Perimeter Ring Road), the ring is decked over first, as a loop
with bends at the four corners.

### 3. Interchanges: expressways meeting

A BSP cut always ends on a road that bounds its region: an ancestor's cut, or the ring. Corridors are
planned parents first, so by the time a corridor is planned, every expressway it could end on is
already final. If the cell just beyond a corridor's end is deck, the deck simply runs up to it. On the
Highway Tilemap that makes an elevated **T** (or a crossroad, where two corridors meet the same cell
from both sides): the interchange. The network is therefore **connected the same way the city is**.

### 4. End ramps: flyovers coming down

A corridor end that doesn't meet another expressway comes down onto its **own avenue**. Walking in
from the end, the first spot where a ramp fits gets it, so the deck comes down as close to the end
as possible. A ramp fits where all of these hold:

- its **Ramp Length** cells are street cells running the ramp's way **with no cross street** (a ramp
  can't drop onto a junction);
- the cell below its **foot** is a street that isn't elevated, for traffic to reach the ground on;
- nothing elevated sits beside any ramp cell. On the Highway Tilemap a ramp foot is recognised by
  having exactly one highway neighbour.

Each end searches at most half the corridor. A corridor whose deck would drop under 3 cells, or that
has an end with nowhere for a ramp, stays an ordinary street.

```
side view, Ramp Length 2 (road-slant-curve), deck height 0.52

                                   .-----------------------  deck over the avenue
                          _..--''''
                     _.-''
  ______________..--'
 | avenue      | foot | top  | deck ...
 ^ traffic reaches the ground here
```

A ramp lies **over a street**, so its cells get the **Cover Tile on the street layer**. The cover
tile spawns nothing, which leaves the ramp piece as the road surface there. It still counts as road
for `RoadRuleTile`, so the avenue keeps its shape either side.

### 5. Access ramps: on/off ramps onto cross streets

Walking along each corridor's deck, the planner branches a ramp off **every Ramp Spacing cells or
more**. The ramp comes down onto a **cross street**. It uses the same fit rules as an end ramp,
applied along the cross street: plain street cells, a street to land on, nothing elevated alongside.
On the Highway Tilemap the deck cell becomes an elevated T with the ramp as its third arm.

- **End ramps count** as the first and last ramps, so a short corridor with both may need no access
  ramp.
- **No two elevated junctions touch.** A deck cell next to an interchange or another branch is
  skipped.
- **One branch per deck cell.** Where a crossroad offers both sides, the first side in a fixed order
  wins.
- **Around the ring**, the corners are skipped, the gap back to the first ramp counts, and ramps
  come down onto the avenues running inward.

### 6. Roundabouts and flyovers

Big crossings, the kind that get a 3×3 roundabout, are exactly where the main avenues cross, and
those avenues now carry the deck. **Expressways are planned first.** A roundabout is placed only
where its whole 3×3 footprint is clear of deck and ramp cells. A big crossing under the deck stays a
**flyover crossing**, since the deck's columns would otherwise stand inside the roundabout.

### 7. Block coverage

How well the network serves the city is measured per block. A breadth-first search over the street
grid runs from every ramp's landing cell, and each block takes the smallest distance among the
streets bordering it. `Plan.BlockDistances` holds one value per block (in `Layout.Blocks` order),
plus the mean and the maximum. **Show Coverage** shades the blocks from green (next to a ramp) to red
(the farthest block in this city). The preview info box reports the mean and maximum.

### 8. From cells to pieces: the Highway RuleTile

The painter puts the **Highway Tile** on every deck cell and ramp foot, and the **Cover Tile** on the
top of every two-cell ramp, all on the Highway Tilemap. `HighwayRuleTile` is a `RoadRuleTile`, so the
XZ-grid rotation fix carries over, and a cover tile counts as highway. It resolves each cell from its
four neighbours; the first match wins:

| # | N, E, S, W at rotation 0 | Rotated | Piece | Built from |
|---|---|---|---|---|
| 0 | highway ×4 | no | `highway-crossroad` | `road-crossroad` on four `bridge-pillar` columns |
| 1 | highway, highway, –, highway | yes | `highway-intersection` | `road-intersection` on four columns |
| 2 | –, highway, –, highway | yes | `highway-straight` | `road-bridge` (its own legs) |
| 3 | highway, highway, –, – | yes | `highway-bend` | `road-bend` on four columns |
| 4 | –, –, –, **cover tile** | yes | `highway-ramp-long` | `road-slant-curve`, anchored half a cell toward its top |
| 5 | –, –, –, highway | yes | `highway-ramp-short` | `road-slant-high` |

- **A highway dead end is always a ramp foot.** The planner guarantees no deck cell is a dead end.
- **The ramp top's cover tile tells the two ramp lengths apart.** The foot of a two-cell ramp is the
  dead end whose neighbour is a cover tile (`HighwayRuleTile.RampTop`, neighbour code 3). Rule 4 is
  tried before rule 5 because a cover tile also counts as highway.
- **Columns stand on the cell corners**, the kerbs of the street below, so an elevated junction over
  a street junction leaves the lanes clear. They are `bridge-pillar` scaled on Y to the deck, and
  narrowed to 0.14 wide if the model is wider.
- **Every piece already stands at deck height** (`road-bridge`'s 0.52), so the Highway Tilemap sits
  on the ground plane with no offset.

The pieces are ordinary prefabs. Tweak them, or click **Rebuild** next to Highway Tile to regenerate
them and the tile in place. The GUIDs stay the same, so Tilemaps already painted pick up the change.

**Heights**, from the kit:

| | |
|---|---|
| Ground road surface | `0.01` |
| Ramp surface | `0.01` → `0.51` |
| `road-bridge` deck surface | `0.52` |
| Elevated junction: column top, and the road piece on it | `0.51`, putting its surface on `0.52` |

### 9. What ends up in the scene

- `City Road Grid/Roads`: the street tiles (and their spawned road prefabs), with cover tiles under
  every ramp.
- `City Road Grid/Highways`: the highway tiles, whose RuleTile spawns the pieces as children of the
  Tilemap, just like the streets.
- `Roads BSP Tree (x, y)`: the stored BSP tree. The regions whose cut became an expressway are marked
  (`CityBspTree.Node.expressway`), and the tree view labels them.

**Generate** (with Clear City Area First) and **Clear City Area** erase the city square on both
Tilemaps.

## Guarantees

Checked over **800 plans**: sides 32, 48, 64 and 128, ring road on and off, Ring Expressway on and
off, Ramp Length 1 and 2, 25 seeds each. The check simulated what the RuleTile sees on every elevated
cell and found zero violations of:

1. **Every elevated cell is over a street.** No block land is taken.
2. **Every deck cell has at least two highway neighbours**, so it resolves to a deck piece and never
   to a ramp.
3. **Every ramp foot has exactly one highway neighbour**, and every two-cell ramp top exactly two, so
   each ramp resolves to the right ramp piece.
4. **Every ramp joins the deck** and **no ramp cell is a street junction.**
5. **No 2×2 patch of elevated cells**: expressways never run side by side.
6. **No roundabout footprint touches deck or ramp cells.** Checked on a generated city.
7. **Same city + same settings = same expressways.** There is no randomness in the planner.

## Numbers at the default settings

Avenue Levels `2`, Min Avenue Length `0.5`, Min Expressway Spacing `0.25`, Ramp Spacing `10`, Ramp
Length `2`, over 300 seeds of an otherwise-default city (perimeter ring on):

| | 32-cell | 32-cell, Ring Expressway | 64-cell | 64-cell, Ring Expressway |
|---|---|---|---|---|
| Cities that get an expressway | 100% | 100% | 100% | 100% |
| Expressways (of candidate avenues) | 1.84 (2.02) | 2.74 (3.02) | 1.95 (2.18) | 2.84 (3.18) |
| Avenues dropped as too close | 0.18 | 0.28 | 0.23 | 0.34 |
| Avenues dropped for no ramp | 0.00 | 0.00 | 0.00 | 0.00 |
| Deck cells | 42 | 169 | 97 | 348 |
| Interchanges | 0.60 | 3.47 | 0.71 | 3.67 |
| End ramps / access ramps | 3.1 / 1.6 | 0 / 11.8 | 3.2 / 5.6 | 0 / 23.8 |
| Block → nearest ramp, mean (cells) | 5.1 | 1.1 | 9.6 | 2.3 |
| Block → nearest ramp, farthest (avg / worst) | 14.3 / 24 | 6.4 / 11 | 29.6 / 44 | 12.5 / 20 |

## Window reference

### Highways
| Field | Default | Description |
|---|---|---|
| Highways | on | Off = street grid only, and the planning is skipped. |
| Avenue Levels | `2` | How many tiers of avenues become expressways: 1 = only the avenue crossing the whole city, 2 = also those crossing each half, … up to 6. `0` = none. The label shows how many were built. |
| Min Avenue Length | `0.5` | Avenues shorter than this fraction of the side length stay streets. The label shows it in cells. |
| Min Expressway Spacing | `0.25` | An avenue closer than this fraction of the side length to a parallel expressway stays a street. `0` = no limit. |
| Ramp Spacing (cells) | `10` | Minimum distance between ramps along an expressway. Lower = better served blocks, busier deck. |
| Ramp Length (cells) | `2` | `1` = the steep `road-slant-high`. `2` = the eased `road-slant-curve`, which needs two straight street cells with no cross street. |
| Ring Expressway | off | Deck over the perimeter ring too, so the avenues end in interchanges instead of coming down. Needs Perimeter Ring Road. |
| Highway Tilemap | empty | The Tilemap layer the highway is painted on. Empty = use or create a `Highways` sibling of the street Tilemap. Must not be the street Tilemap. |
| Highway Tile / Create / Rebuild | `Assets/Tiles/Highway Rule Tile.asset` | The highway RuleTile. **Create**/**Rebuild** generates it and its pieces from the Highway kit. |

The **Cover Tile** from the Variety section is required. It goes under every ramp on the street layer,
and on each two-cell ramp's top cell on the highway layer. The **Road Tile** must be a `RoadRuleTile`,
so that cover tiles count as road.

### Scene preview
| Colour | Meaning |
|---|---|
| Blue cell, at deck height | An elevated deck cell |
| Pink cell, at the height it climbs to | A ramp cell |
| Green → red block shading | Street distance from the block to the nearest ramp (toggle with **Show Coverage**) |

The info box reports the expressways built out of the candidates, deck cells, interchanges, end and
access ramps, and the mean and farthest block distance to a ramp. Notes explain any avenue that was
left a street and why.

## Recipes

| Look | Settings |
|---|---|
| One cross-city expressway | Avenue Levels `1` |
| Expressway grid over the main avenues (default) | Avenue Levels `2`, Min Expressway Spacing `0.25` |
| Dense network, every district served | Avenue Levels `3`, Min Avenue Length `0.3`, Ramp Spacing `6` |
| Orbital city | Ring Expressway on, Avenue Levels `1`–`2` |
| Long flyovers with few exits | Ramp Spacing `30`+ |
| Steep ramps in a tight grid | Ramp Length `1` |
| Streets only | Untick **Highways**, or Avenue Levels `0` |

## Limitations

- **Expressways only follow BSP cuts.** A corridor is one straight avenue. There are no diagonal or
  curved routes, and two collinear cuts in different halves of the city stay two corridors.
- **A ramp takes the street's lanes.** The street layer is one cell wide, so where a ramp comes down
  the ramp *is* the road; there's no ground lane beside it.
- **One deck level.** Expressways meet at grade on elevated T and crossroad pieces. There are no
  stacked interchanges.
- **No slip roads.** A ramp is a full two-way connection between the deck and one street.
- **Access ramps need a plain stretch.** A cross street with a junction within Ramp Length cells of
  the deck can't take one. Min Block Size `3`+ (the default) almost always leaves room.
- **Columns over a street junction** stand on its corners, so they don't block the lanes, but they
  are on the pavement.
- **Assumes a Grid cell size of 1.** The pieces are authored to 1 unit and the heights are absolute.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| "Highways are off: assign a Highway Tile" | Click **Create** next to Highway Tile. |
| "Highways are off: every ramp lies over a street…" | Assign a Road Cover Tile (Variety section, **Create**) and use a `RoadRuleTile` as Road Tile. |
| "Highway Tile is not a Highway Rule Tile" | Another tile is assigned, so painted cells won't turn into pieces. Click **Rebuild**. |
| "Highway Tilemap is the street Tilemap" | Clear the Highway Tilemap field; Generate creates a `Highways` layer. |
| "No expressways: no avenue in the top N level(s) is … cells long" | Lower **Min Avenue Length**, or raise **Avenue Levels**. |
| "avenue(s) left as streets: closer than … to a parallel expressway" | Intended. Lower **Min Expressway Spacing** to keep them. |
| "avenue(s) left as streets: a free end had no … straight street cell(s)" | Lower **Ramp Length**, or turn on **Ring Expressway** so the avenue ends in an interchange. |
| Some blocks are far from any ramp (red in Show Coverage) | Lower **Ramp Spacing**, raise **Avenue Levels**, or turn on **Ring Expressway**. |
| A roundabout disappeared after turning highways on | Intended: that crossing is now under the deck, so it's a flyover crossing. |
| Highway cells painted but no 3D pieces | The tile's rules don't reference the pieces (e.g. the piece prefabs were deleted). Click **Rebuild**. |
| Ramps face the wrong way / a deck cell shows a ramp | Tiles were painted by hand next to generated ones, or a tile piece was edited. Clear and regenerate, and Rebuild the tile if the pieces changed. |

## Files touched by this feature

- `Assets/Editor/HighwayPlanner.cs`: corridor selection, interchanges, end and access ramps, block
  coverage. Pure and deterministic.
- `Assets/Editor/HighwayBuilder.cs`: paints a plan onto the Highway Tilemap; builds the tile pieces and
  the Highway Rule Tile asset.
- `Assets/Scripts/Tiles/HighwayRuleTile.cs`: the highway RuleTile (adds the ramp-top neighbour code).
- `Assets/Editor/CityBspLayout.cs`: records the BSP tree (`Layout.Nodes`) the avenues are ranked from.
- `Assets/Editor/RoundaboutPlanner.cs`: skips crossings under the deck.
- `Assets/Editor/CityRoadTilemapWindow.cs`: the **Highways** section, the Highway Tilemap layer, the
  Scene preview and coverage shading, and painting inside the existing Generate/Clear undo step.
- `Assets/Editor/CityBspTreePanel.cs`, `Assets/Scripts/Tiles/CityBspTree.cs`: mark and label the
  avenues that became expressways.
- `Assets/Prefabs/Roads/Highway/Tile Pieces/*`, `Assets/Tiles/Highway Rule Tile.asset`: generated.
  The original kit prefabs are used as-is.
- `Docs/CityHighwayGenerator.md`: this document.
