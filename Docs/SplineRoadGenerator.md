# Spline Road Generator

An Editor tool that places a chain of modular road prefabs (from `Assets/Prefabs`) along
any `SplineContainer` in the scene, rotated to follow the spline's tangent at every step.
Since splines can turn in any direction, the tool isn't limited to a single straight
prefab — it detects sharp turns and swaps in a corner prefab (snapped to the nearest 90°,
oriented to the turn) so corners connect properly instead of a straight tile fanning out.

- **Script**: `Assets/Editor/SplineRoadGeneratorWindow.cs`
- **Namespace**: `OpenCity.EditorTools`
- **Menu**: `Tools > OpenCity > Spline Road Generator`
- **Requires**: `com.unity.splines` (already in `Packages/manifest.json`)

It's the reusable, spline-driven counterpart to the manual grid placement pass this
project used previously — same tile kit, same orientation convention, but driven by a
`Spline` curve instead of hand-placed grid coordinates.

See also: the [Rounded Spline Path Tool](RoundedSplinePath.md) authors splines with
smoothly rounded corners (rather than sharp ones) — round your spline with it first, then
run this generator on the result for a road that curves smoothly through corners without
needing a corner prefab at all.

## Quick start

1. Create or select a `Spline` GameObject (`GameObject > Splines > Draw Spline Tool`, or
   any existing `SplineContainer` in the scene) and shape it the way you want the road to run.
2. Open `Tools > OpenCity > Spline Road Generator`.
3. Click **Use Selected GameObject** (with the spline selected in the Hierarchy), or drag
   the `SplineContainer` into the **Spline Container** field directly.
4. Leave **Straight Tile Prefab** on its default (`Assets/Prefabs/road-straight.prefab`),
   or assign a different prefab from `Assets/Prefabs`.
5. Optionally open the **Corner Detection** foldout and confirm **Corner Prefab** is set
   (defaults to `Assets/Prefabs/road-bend.prefab`) if the spline has turns.
6. Click **Generate**.

A GameObject named `Road` (configurable) appears in the scene, containing one prefab
instance per tile — straight or corner, depending on the local curve — each positioned and
rotated to follow the spline. Every instance is a proper linked `PrefabUtility` instance
(blue-link icon), not a disconnected copy — editing the source prefab still propagates to
generated tiles that haven't been manually overridden.

## How it works

1. Reads `SplineContainer.Splines[splineIndex]` and its total arc length
   (`Spline.GetLength()`).
2. Measures the straight tile's footprint along **Forward Axis** by combining the bounds
   of every `Renderer` on the prefab (unless **Tile Length Override** is set).
3. Walks the spline in fixed arc-length steps using
   `SplineUtility.GetPointAtLinearDistance`, one step per tile (each step itself split into
   two half-distance calls, which is both more accurate at sharp turns — see "Numerical
   notes" — and gives a `tileStartT`/`tileEndT` pair per tile), starting at the spline's
   start (plus **Start Offset**).
4. For each tile, evaluates the tangent at `tileStartT` and `tileEndT`
   (`SplineUtility.Evaluate`) and measures the turn angle between them. If **Corner
   Prefab** is assigned and that angle is at or above **Corner Threshold**, the tile
   becomes a corner (step 5b); otherwise it's a straight tile (step 5a).
   - **5a. Straight**: rotates the tile with
     `Quaternion.FromToRotation(forwardAxisVector, tangentAtTileCenter)` so its forward
     axis points down the curve.
   - **5b. Corner**: tries all four 90° rotations of the corner prefab and picks the one
     whose two open sides (**Corner Open Axis A** / **B**) best align with the incoming
     (reversed) and outgoing travel directions — see `ComputeCornerRotation` in the
     script. This is what lets one tool follow a spline through turns in any direction
     instead of only ever laying straight tiles.
5. Instantiates the tile as a linked prefab instance (`PrefabUtility.InstantiatePrefab`)
   under the generated parent object.
6. If not closed, places **Start Cap** / **End Cap** the same way.
7. The whole run — including clearing a previous generation — is wrapped in one Undo
   group, so **Ctrl+Z** undoes an entire Generate in a single step.

With no **Corner Prefab** assigned, every tile is a rotated straight tile, same as before —
this still reads as a smoothly curving road for gentle curves, and is a reasonable choice
if your kit has no matching corner piece. With a corner threshold that's too high (or a
spline whose turns are all gentler than the threshold), sharp turns will show straight
tiles visibly fanning/overlapping at their inner and outer edges — lower **Corner
Threshold** or add more knots around the turn if you see that.

## Orientation convention (important)

This project's `road-*` kit was reverse-engineered empirically (place a piece, surround
it with `road-straight` probes on all 4 sides, see which ones connect without a curb
seam). Rotating a piece by `+90°` around Y (`Quaternion.Euler(0, 90, 0)`) advances its
open edge(s) **clockwise** through North → East → South → West → North.

| Prefab | At rotation 0 |
|---|---|
| `road-straight`, `road-straight-half` | Open **West/East**, i.e. through-axis is **local X** |
| `road-crossing` | Same axis convention as `road-straight`, plus a crosswalk stripe |
| `road-bend`, `road-curve` | Open **North + East** |
| `road-end` | Open **South only** (other 3 sides curbed) |
| `road-side` | Through **East/West**, plus a branch open **North**; South curbed (this is the kit's actual T-junction — `road-intersection` is not) |
| `road-crossroad`, `road-intersection`, `road-square`, `road-split`, `road-roundabout` | Functionally 4-way (open all sides); rotation only changes cosmetic marking |
| `road-slant`, `road-slant-high` (+ `-flat`/`-flat-high` variants), `road-bridge` | Self-contained ramps, rise 0.27 (slant) / 0.52 (slant-high, bridge); at rotation 0 the high end faces **East** — chain them at `y = 0`, no manual Y-offset needed |

The tool's **Forward Axis** default is **+X**, matching `road-straight`'s local through-
axis above (which is what every straight/bend/curve/crossing piece shares). Don't change
it unless you're pointing the tool at a different prefab kit with a different forward
axis — you can sanity-check a new kit by seeing which local axis is shortened on a "half"
variant, if one exists, the way `road-straight-half` is shortened in X here.

## Window reference

### Spline
| Field | Description |
|---|---|
| Spline Container | The `SplineContainer` to follow. |
| Use Selected GameObject | Grabs the `SplineContainer` off the currently selected Hierarchy object. |
| Spline Index | Only shown when the container has more than one spline; picks which one to follow. |

### Straight Tile
| Field | Description |
|---|---|
| Straight Tile Prefab | The prefab repeated along straight/gently-curving stretches. Defaults to `Assets/Prefabs/road-straight.prefab`. |
| Forward Axis | Which local axis of the straight prefab points down the road. Default `+X` (see above). |
| Tile Length Override | Force a tile length instead of auto-measuring renderer bounds. `0` = auto. Use this if the prefab has no `Renderer` (e.g. it's an empty parent with only nested prefabs) or you want tighter/looser spacing. |
| Flatten Elevation | Zeroes the tangent's Y component so tiles stay level even if the spline dips or climbs. Turn this **off** for ramps/hills where you want tiles to bank with the slope. |
| Stretch To Fit | Rounds to the nearest whole tile count and rescales each **straight** tile's local scale along Forward Axis so the run exactly covers the usable spline length with no end gap. Never applied to corner tiles (rescaling would distort the curved mesh). Off by default (tiles keep their exact authored size; a remainder shorter than one tile is left uncovered at the end). |

### Corner Detection (optional foldout)
| Field | Description |
|---|---|
| Corner Prefab | A 1×1-footprint corner piece (e.g. `road-bend`) substituted for a straight tile wherever the spline turns sharply within that tile's span. Defaults to `Assets/Prefabs/road-bend.prefab`. Leave empty to disable — every tile will be a straight tile. |
| Corner Threshold (deg) | Turn angle within one tile's span, above which that tile becomes a corner instead of a straight tile. Default `45°`. Lower it to catch gentler turns; raise it if straight tiles on your normal curves are being replaced unnecessarily. |
| Corner Open Axis A / B | The corner prefab's two open sides **at rotation 0**, as local axes. Defaults `+X` / `+Z`, matching this kit's `road-bend`/`road-curve` (open North+East at rotation 0, see the orientation table above). Change these if you assign a corner prefab from a different kit with a different rest orientation. |

`road-curve` (the wider, 2×2-footprint corner) is **not** supported by Corner Detection —
it doesn't fit the tool's one-tile-per-arc-length-step accounting. Only use a 1×1 corner
piece like `road-bend` here; swap in `road-curve` by hand afterward if you want a
wider-radius turn.

### Placement
| Field | Description |
|---|---|
| Parent Name | Name of the generated root GameObject. **Generate first deletes any existing object with this exact name** (in the same Parent Container / scene root), then rebuilds — this is how re-running Generate after editing the spline "refreshes" the road. Don't reuse this name for unrelated objects. |
| Parent Container | Optional transform to nest the generated root under. Empty = scene root of the active scene. |
| Start Offset (m) | Arc-length distance to skip before the first tile (e.g. to leave room for a hand-placed intersection piece). |
| End Offset (m) | Arc-length distance to leave uncovered at the end, same idea. |

### End Caps (optional, foldout)
| Field | Description |
|---|---|
| Start Cap Prefab / End Cap Prefab | Optional prefabs (e.g. `road-end`) placed exactly at the spline's start/end point instead of a plain tile gap. Only applied when the spline is **not closed**. |
| Flip Start Cap / Flip End Cap | Rotates that cap 180° around world Y. |

The tool can't tell which side of a cap mesh is the "closed" end from geometry alone —
if the closed side faces the wrong way after Generate, toggle the corresponding Flip
box and Generate again.

### Live preview
Below the fields, a read-only box shows the computed spline length, tile length, tile
count, and covered/remainder length before you click Generate — use it to sanity-check
**Stretch To Fit** and offsets without committing.

### Buttons
- **Generate** — disabled until the current settings are valid (missing spline/prefab,
  zero-length spline, etc. show as a warning in the preview box instead). Clears and
  rebuilds the named parent, selects it when done, and logs a one-line summary to the
  Console (straight tile count, corner tile count, spline length).
- **Clear** — deletes the generated parent (by name) without regenerating. Logs to the
  Console either way (cleared, or "nothing found").

## Regenerating after editing the spline

Just click **Generate** again. It deletes the previous `Road` (or whatever **Parent
Name** is set to) and rebuilds from the spline's current shape — this is the intended
iteration loop: adjust knots in the Spline Tool, flip back to this window, Generate,
repeat.

## Safety / limitations

- **Undo-safe**: the entire Generate (including the implicit clear of a prior run) is one
  Undo group. Clear is also undoable.
- **Name-based lookup**: Generate/Clear find the target root purely by GameObject name
  under **Parent Container** (or scene root). If you rename the generated root or create
  an unrelated object with the same name, Generate will delete that object on its next
  run. Keep **Parent Name** unique.
- **Corner detection is angle-based, not shape-based**: it only knows the tangent turned
  more than the threshold within one tile's span — it can't tell an actual 90° corner from,
  say, a tight S-curve compressed into one tile. For splines with knots roughly on a grid
  (the common case for street layouts) this reads correctly; for organic/freeform curves,
  raise the threshold or add more knots so no single tile's span crosses it unintentionally.
- **Only one corner shape**: no attempt to pick between multiple corner variants (tight vs.
  wide radius, left vs. right bias) — every corner tile uses the same **Corner Prefab**,
  rotated. `road-curve` (2×2) isn't supported at all, see above.
- **Numerical note on sharp, discontinuous-tangent knots**: Unity's
  `SplineUtility.GetPointAtLinearDistance` internally does an iterative search, and its
  accuracy can dip right at a knot where the tangent *direction* breaks abruptly (not just
  curves) — e.g. a perfectly square, hand-authored 90° corner with non-continuous tangent
  handles. In testing this showed up as a fixed per-call position error of a similar order
  to one tile length when jumping a full tile-length distance across such a knot in one
  `GetPointAtLinearDistance` call; the tool's actual half-step-per-tile walk (see "How it
  works") avoided this in every case tested. Splines drawn normally with the Spline Tool
  (continuous tangents, even at sharp-looking turns) aren't expected to hit this. If a
  generated road looks offset right at one specific corner, try adding an extra knot on
  either side of it to soften the tangent break.
- **Closed splines**: supported for tiling (the arc-length walk loops correctly), but end
  caps are skipped since there is no start/end.
- **Multi-scene**: the scene-root search (`Clear`/regenerate lookup when Parent Container
  is empty) only looks in the currently *active* scene.

## Files touched by this feature
- `Assets/Editor/SplineRoadGeneratorWindow.cs` — the tool.
- `Docs/SplineRoadGenerator.md` — this document.
