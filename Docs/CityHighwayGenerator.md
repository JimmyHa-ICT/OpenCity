# City Highway Generator (BSP clusters)

An extension of the [City Road Generator (BSP Tilemap)](CityBspTilemapGenerator.md) that lays an
**elevated highway network** over the city it just generated. It reads the BSP tree back at a chosen
depth to cut the city into **clusters** (districts), decides which of them are **far enough apart to
be worth a direct route**, and routes that link as a viaduct that runs **right on top of the streets
or well clear of them** — never alongside one — coming down to street level on a ramp at each end.

The highway is painted exactly the way the streets are: as tiles, with a **Highway RuleTile**, on its
**own Tilemap layer** above the street Tilemap.

- **Tool**: the **Highways** section of `Tools > OpenCity > City Road Generator (BSP Tilemap)`
- **Planner**: `Assets/Editor/HighwayPlanner.cs` (clusters, hubs, link choice, routing — pure, no
  Editor or Tilemap dependency, no randomness)
- **Painter + tile asset builder**: `Assets/Editor/HighwayBuilder.cs`
- **RuleTile**: `Assets/Scripts/Tiles/HighwayRuleTile.cs` → asset `Assets/Tiles/Highway Rule Tile.asset`
- **Tile pieces**: `Assets/Prefabs/Roads/Highway/Tile Pieces/` (generated from the Highway kit)
- **Clusters come from**: `CityBspLayout.Districts` (`Assets/Editor/CityBspLayout.cs`)
- **Namespace**: `OpenCity.EditorTools` (tile: `OpenCity.Tiles`)

## Where this fits

| | |
|---|---|
| [City Road Generator (BSP Tilemap)](CityBspTilemapGenerator.md) | The street grid, painted on the `Roads` Tilemap with the road RuleTile |
| **City Highway Generator** | **A handful of long elevated routes, painted on the `Highways` Tilemap with the highway RuleTile** |

Both layers live in the same Grid on the same ground plane. The highway's tile pieces carry their own
height, so the `Highways` Tilemap needs no offset. Because each RuleTile only sees tiles on its own
Tilemap, a highway running over a street never changes the street's shape, and the street never
changes the highway's.

The two run in the same **Generate**, in one Undo step. The only tile the highway puts on the street
layer is one `RoadCoverTile` at the bottom of each ramp, which turns the street it lands on into the
interchange (see [Ramps and interchanges](#4-ramps-and-interchanges)).

## Quick start

1. Open `Tools > OpenCity > City Road Generator (BSP Tilemap)` and set the city up as usual
   (**Create City Tilemap** now creates both the `Roads` and the `Highways` Tilemap).
2. Scroll to **Highways**. Next to **Highway Tile**, click **Create** once. This builds the six tile
   pieces into `Assets/Prefabs/Roads/Highway/Tile Pieces/` and writes
   `Assets/Tiles/Highway Rule Tile.asset`. (Also available as `Tools > OpenCity > Create Highway Rule Tile`.)
3. Watch the Scene view: **blue outlines** are the clusters, **blue cells** are the elevated deck
   (drawn at deck height), **pink cells** are the ramps, drawn at the height each one climbs to.
4. Turn **Cluster Depth** up for more, smaller districts and so more highways; turn **Min Link Span**
   up to keep only the really long routes.
5. Click **Generate**.

If **Highway Tilemap** is empty, Generate uses a sibling Tilemap called `Highways` in the street
Tilemap's Grid, or creates one. **Ctrl+Z** undoes the streets, the roundabouts and the highways together.

## How it works

### 1. Clusters: the BSP tree, read back at a depth

The city's streets *are* a BSP tree — every street is a cut, and every block is a leaf. So the city
already knows how it is grouped; the generator only has to stop reading the tree early.

`CityBspLayout` records every region it visits (`Layout.Nodes`: rect, depth, leaf or not).
`CityBspLayout.Districts(layout, depth)` then returns the tree's **frontier** at that depth: every
region that was cut exactly `depth` times, plus every leaf that stopped splitting before it got that
far. Those regions **tile the city exactly once** — no gaps, no overlaps — and their borders are
real streets, because that is what a BSP cut is.

```
Cluster Depth 0   Cluster Depth 1   Cluster Depth 2   Cluster Depth 3
+-------------+   +------+------+   +------+--+--+    +---+--+--+--+
|             |   |      |      |   |      |     |    |   |  |     |
|             |   |      |      |   +------+--+--+    +---+--+--+--+
|      1      |   |  1   |  2   |   |      |     |    |      |  |  |
|             |   |      |      |   |      |     |    +------+--+--+
+-------------+   +------+------+   +------+--+--+    +------+--+--+
    1 cluster        2 clusters        up to 4           up to 8
```

### 2. Hubs: where a cluster meets the highway

Each cluster gets at most one **hub** — the street cell nearest its centre that a ramp can climb away
from. A candidate `(street cell, direction)` has to satisfy all of:

- the next **Ramp Length** cells in that direction are **block** cells (not street, not roundabout)
  and inside the city;
- **none of those ramp cells touches a road** other than the hub itself;
- the cell after the ramp, where the deck starts, is either a street (the deck rides straight onto
  it) or has **no street running alongside it** (rule 1 below);
- the hub is **not already beside a junction**, so the new interchange can't break the street
  generator's [intersection spacing rule](CityBspTilemapGenerator.md#intersection-spacing);
- the hub, the ramp and the deck start **don't touch another cluster's ramp**. On the highway Tilemap
  a ramp is recognised by being a dead end; a second ramp beside it would make both read as deck.

The cluster's rect is grown by one cell before the search, so the streets *bounding* the cluster
count as candidates. A cluster with no valid hub simply isn't on the highway.

### 3. Which clusters get linked: far pairs, then a spanning forest

Two clusters are **far apart** when their hubs are at least **Min Link Span** cells apart (Manhattan).
Only far pairs are candidates. Over those, **Kruskal, shortest first** keeps a link only when it joins
two parts of the network that were not connected yet — a **minimum spanning forest**, capped at
**Max Links**. There is no randomness anywhere: change the city's seed and the highways move because
the *streets* moved.

### 4. Ramps and interchanges

A ramp is `Ramp Length` cells climbing from ground beside the hub up to deck height.

```
side view, Ramp Length 2 (road-slant-curve), deck height 0.52

                                 .-------------------------  deck, 0.52
                        _..--''''
                   _.-''                                     eased 2-cell ramp
  ____________..--'
 |   street   | foot | top  | first deck cell   |
 |   (hub)    |
   ^ gains a fourth arm and becomes a T-junction
```

On the **street** Tilemap the ramp's **bottom cell** gets a `RoadCoverTile`: it spawns nothing, but
`RoadRuleTile` counts it as road, so the hub street re-resolves into a T-junction pointing at the ramp.

On the **highway** Tilemap the foot gets the Highway Tile and, for a two-cell ramp, the top gets the
Cover Tile. See [section 6](#6-from-cells-to-pieces-the-highway-ruletile) for why.

### 5. Routing: A\* over (cell, heading)

Each link is routed from the top of one ramp to the top of the other. The search state is
**(cell, the direction we entered it by)**, which is what lets a cell be legal to pass through but
illegal to turn on, and lets a turn be priced.

| | Cost |
|---|---|
| Step onto a fresh block cell | 1 |
| Step onto a street cell (with **Follow Streets** on) | ½ |
| Step onto a cell an earlier link already decked | ½ |
| Change of direction | **Turn Cost** cells |

The half-price reuse makes later links merge into an existing trunk; the half-price street step makes
the highway ride above the avenues, the way urban expressways are usually built.

The rules that make it look real:

1. **Right on top of a street, or well clear of one.** A deck cell over a street is always fine —
   following the street or flying straight across it. A deck cell *off* the street grid may not have a
   street on either side of its direction of travel. A street straight ahead or behind is fine (the
   deck is approaching or leaving a crossing); a street beside it would be a viaduct running parallel
   one cell off the road, which is what looked wrong.
2. **Turns over a street follow the street.** On a street cell the deck may only turn if it came in
   along the street or leaves along it — it turns onto, off or along the road, never kinks across the
   middle of it.
3. **Turns off the street grid are far from it.** Off the streets, the deck turns only on a cell no
   street touches at all (this falls out of rule 1 for both headings).
4. **Stay inside the city square**, and **never enter a roundabout's 3×3 footprint**.
5. **Stay clear of every ramp and its hub** except the one cell where the deck continues from its top.

A route that doubles back through a cell it had already decked is dropped, and the link with it.

```
 Allowed                                    Not allowed
 ======= street =======                     ======= street =======
 ##########  deck on top of the street       ##########  deck one cell off the street,
                                                          running alongside it
        #   deck crossing, straight
 =======#=============
        #
        ####  turn two+ cells from any street
```

### 6. From cells to pieces: the Highway RuleTile

The painter puts the **Highway Tile** on every deck cell and ramp foot, and the **Cover Tile** on the
top of every two-cell ramp, all on the Highway Tilemap. `HighwayRuleTile` (a `RoadRuleTile`, so the
XZ-grid rotation fix and "a cover tile counts as highway" both carry over) resolves each cell from its
four neighbours, first match wins:

| # | N, E, S, W at rotation 0 | Rotated | Piece | Built from |
|---|---|---|---|---|
| 0 | highway ×4 | no | `highway-crossroad` | `road-crossroad` on four `bridge-pillar` columns |
| 1 | highway, highway, –, highway | yes | `highway-intersection` | `road-intersection` on four columns |
| 2 | –, highway, –, highway | yes | `highway-straight` | `road-bridge` (its own legs) |
| 3 | highway, highway, –, – | yes | `highway-bend` | `road-bend` on four columns |
| 4 | –, –, –, **cover tile** | yes | `highway-ramp-long` | `road-slant-curve`, anchored half a cell toward its top |
| 5 | –, –, –, highway | yes | `highway-ramp-short` | `road-slant-high` |

- **A highway dead end is always a ramp foot.** The planner guarantees no deck cell is a dead end.
- **The ramp top's cover tile is how the tile tells the two ramp lengths apart**: the foot of a
  two-cell ramp is the dead end whose neighbour is a cover tile (`HighwayRuleTile.RampTop`, neighbour
  code 3). Rule 4 is tried before rule 5 because a cover tile also counts as highway.
- **Columns stand on the cell corners** — the kerbs of a street below — so an elevated turn or
  junction may stand over a street junction as well as over a block. They are `bridge-pillar` scaled
  on Y to the deck, and narrowed to 0.14 wide if the model is wider.
- **Every piece already stands at deck height** (`road-bridge`'s 0.52), so the Highway Tilemap sits
  on the ground plane with no offset.

The pieces are ordinary prefabs; tweak them, or click **Rebuild** next to Highway Tile to regenerate
them and the tile in place (same GUIDs, so already painted Tilemaps pick the change up).

**Heights**, from the kit:

| | |
|---|---|
| Ground road surface | `0.01` |
| Ramp surface | `0.01` → `0.51` |
| `road-bridge` deck surface | `0.52` |
| Elevated turn: column top, and the road piece on it | `0.51`, putting its surface on `0.52` |

### 7. What ends up in the scene

- `City Road Grid/Roads` — the street tiles (and their spawned road prefabs), the ramp-foot cover tiles.
- `City Road Grid/Highways` — the highway tiles, whose RuleTile spawns the pieces as children of the
  Tilemap, just like the streets.

**Generate** (with Clear City Area First) and **Clear City Area** erase the city square on both
Tilemaps. Highway prefab instances left by the older prefab-based version of this tool are still
removed too, since they carry a `MultiCellRoadPiece`.

## Guarantees

Checked headless over **1,500 random cities** (sides 16–80, ring road on and off, BSP settings
randomised, Cluster Depth 1–5, Min Link Span 0.4–1.4 × side, Max Links 1–7, Ramp Length 1–2, Turn
Cost 0–9, Follow Streets on and off) — 3,105 links and 101,403 deck cells — with the RuleTile's
matching simulated on every painted cell. Zero violations of:

1. **No off-street deck cell has a street alongside its direction of travel**, on the way in or out.
2. **No deck cell turns across the middle of a street** — every turn over a street has an arm along it.
3. **No ramp sits on a street**, and no deck cell is on a roundabout.
4. **Every deck cell resolves to a deck piece** (rules 0–3), never a ramp and never nothing.
5. **Every ramp foot resolves to the right ramp piece** (rule 4 for length 2, rule 5 for length 1),
   facing its hub.
6. **No two ramps overlap or touch**, and no ramp cell is also deck.
7. **Every link is an unbroken orthogonal chain**, ramp to ramp.
8. **Same city + same settings = same highways.** No randomness in the planner.

## Numbers at the default settings

Cluster Depth 3, Min Link Span 0.75, Max Links 4, Ramp Length 2, Turn Cost 6, over 300 seeds of an
otherwise-default city:

| | 32-cell, Follow Streets on | 32-cell, off | 64-cell, on | 64-cell, off |
|---|---|---|---|---|
| Cities that get a highway | 100% | 100% | 100% | 100% |
| Links | 3.99 | 3.99 | 4.00 | 4.00 |
| Deck cells | 97 | 89 | 187 | 162 |
| …of those, on top of a street | 58% | 40% | 62% | 32% |
| Ramps | 6.2 | 6.2 | 6.0 | 6.0 |
| Links dropped as unroutable | 0.01 | 0.01 | 0.00 | 0.00 |
| Clusters with no hub | 0.00 | 0.00 | 0.00 | 0.00 |

## Window reference

### Highways
| Field | Default | Description |
|---|---|---|
| Highways | on | Off = street grid only, and the planning is skipped entirely. |
| Cluster Depth | `3` | How deep into the BSP tree the city is cut into clusters — up to 2^depth of them. The label shows the resulting count. |
| Min Link Span | `0.75` | How far apart two clusters' hubs must be, as a fraction of the side length, in cells travelled. The label shows it in cells. Higher = fewer, longer highways. |
| Max Links | `4` | Upper bound on the number of links, taken shortest first. |
| Ramp Length (cells) | `2` | `1` = the steep `road-slant-high`. `2` = the eased `road-slant-curve` (needs a Road Cover Tile for its top cell). |
| Turn Cost (cells) | `6` | What one change of direction costs the router. High = long straight viaducts; `0` = shortest path. |
| Follow Streets | on | Street cells cost the router half a step, so the highway rides on top of the avenues where it can. Off = shortest legal corridor, over streets or across blocks. Either way it never runs alongside a street. |
| Highway Tilemap | empty | The Tilemap layer the highway is painted on. Empty = use or create a `Highways` sibling of the street Tilemap. Must not be the street Tilemap. |
| Highway Tile / Create / Rebuild | `Assets/Tiles/Highway Rule Tile.asset` | The highway RuleTile. **Create**/**Rebuild** generates it and its pieces from the Highway kit. |

The **Cover Tile** from the Variety section is shared: it is painted at each ramp foot on the street
layer and at each two-cell ramp's top on the highway layer.

### Scene preview
| Colour | Meaning |
|---|---|
| Blue outline | A cluster (toggle with **Show Clusters**) |
| Blue cell, at deck height | An elevated deck cell |
| Pink cell, at the height it climbs to | A ramp cell |

The info box reports the cluster count, links, deck cells, how many of those are on top of a street,
and the ramp count.

## Recipes

| Look | Settings |
|---|---|
| Expressways built over the main avenues (default) | Follow Streets on, Turn Cost `6` |
| Viaducts cutting their own corridor across the blocks | Follow Streets off, Turn Cost `9`+ |
| One cross-city motorway | Cluster Depth `2`, Min Link Span `0.4`, Max Links `1` |
| Dense urban expressway network | Depth `4`, Span `0.5`, Max Links `12`, Turn Cost `3` |
| Steep city ramps in tight blocks | Ramp Length `1` |
| Streets only | Untick **Highways** |

Highways want room: blocks of 3+ cells (Min Block Size `3`, the default) leave cells in the middle of
a block that are clear of every street, which is where off-street turns and ramps go.

## Limitations

- **The highway still takes block cells** where it isn't on top of a street. A building placer should
  subtract `Plan.DeckCells` and `Plan.RampCells` that aren't in `Layout.RoadCells`.
- **One deck level.** Two highways that cross do so at grade, on a `highway-crossroad`.
- **No slip roads.** A ramp is a full two-way connection between the deck and one street.
- **Adjacency is connection.** The RuleTile connects any two neighbouring highway cells, so two links
  passing side by side merge into a wider junction patch. Raising **Turn Cost** makes it rarer.
- **Turns are 90° and one cell.** There is no horizontal elevated curve in the kit.
- **Columns over a street junction** stand on its corners, so they don't block the lanes, but they
  are on the pavement.
- **Assumes a Grid cell size of 1**; the pieces are authored to 1 unit and the heights are absolute.
- **Cost is in the A\*.** Routing is a search over `side × side × 4` states per link. Cities up to
  ~128 are instant.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| "Highways are off: assign a Highway Tile" | Click **Create** next to Highway Tile. |
| "Highway Tile is not a Highway Rule Tile" | Another tile is assigned; painted cells won't turn into pieces. Click **Rebuild**. |
| "a two-cell ramp's top cell is painted with the Cover Tile" | Assign a Road Cover Tile (Variety section, **Create**), or set Ramp Length to 1. |
| "Highway Tilemap is the street Tilemap" | Clear the Highway Tilemap field; Generate creates a `Highways` layer. |
| Highway cells painted but no 3D pieces | The tile's rules don't reference the pieces (e.g. the piece prefabs were deleted). Click **Rebuild**. |
| Ramps face the wrong way / a deck cell shows a ramp | Tiles were painted by hand next to generated ones, or a tile piece was edited. Clear and regenerate; Rebuild the tile if the pieces changed. |
| Old highway prefabs (with piers) remain | They came from the previous version. **Clear City Area** removes them. |
| "Clusters have nowhere to put a ramp" | No street in them has `Ramp Length` clear block cells beside it, away from other roads, junctions and other ramps, with a legal deck start. Drop Ramp Length to 1, or raise **Min Block Size**. |
| "Links had no legal corridor and were dropped" | Every route would have had to run alongside a street. Reroll the seed, lower Turn Cost, or turn Follow Streets on. |
| Too many highways | Lower **Max Links** or **Cluster Depth**, or raise **Min Link Span**. |

## Files touched by this feature

- `Assets/Scripts/Tiles/HighwayRuleTile.cs`: the highway RuleTile (adds the ramp-top neighbour code).
- `Assets/Editor/HighwayPlanner.cs`: clusters, hubs, link selection and the router. Pure, deterministic.
- `Assets/Editor/HighwayBuilder.cs`: paints a plan onto the Highway Tilemap; builds the tile pieces and
  the Highway Rule Tile asset.
- `Assets/Editor/CityBspLayout.cs`: records the BSP tree (`Layout.Nodes`) and reads its frontier back
  (`Districts`).
- `Assets/Editor/CityRoadTilemapWindow.cs`: the **Highways** section, the Highway Tilemap layer, the
  Scene preview, and painting inside the existing Generate/Clear undo step.
- `Assets/Editor/RoadGridPlacer.cs`: `ComputePivotOffset` measures relative to the prefab's own root.
- `Assets/Prefabs/Roads/Highway/Tile Pieces/*`, `Assets/Tiles/Highway Rule Tile.asset`: generated.
  The original kit prefabs are used as-is.
- `Docs/CityHighwayGenerator.md`: this document.
