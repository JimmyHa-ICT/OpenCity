# Rounded Spline Path Tool

An Editor tool for authoring paths that are straight segments joined by smoothly rounded
corners at a single radius — either by rounding a path already drawn with Unity's own
Spline Tool, or by clicking waypoints directly in the Scene view. Pairs naturally with the
[Spline Road Generator](SplineRoadGenerator.md): a rounded corner spreads its turn across
several tiles, so the road generator's default behavior (rotate a straight tile to follow
the tangent) reads as a smoothly curving road with no corner-prefab substitution needed.

- **Script**: `Assets/Editor/RoundedSplinePathWindow.cs` (window) +
  `Assets/Editor/SplineFilletUtility.cs` (geometry core, no `UnityEditor` dependency)
- **Namespace**: `OpenCity.EditorTools`
- **Menu**: `Tools > OpenCity > Rounded Spline Path Tool`
- **Requires**: `com.unity.splines` (already in `Packages/manifest.json`)

## Quick start

**Rounding a spline you already drew:**
1. Draw a path with Unity's Spline Tool the normal way — straight segments meeting at
   sharp corners (any tangent mode; only knot *positions* are read, so it doesn't matter
   how you drew it).
2. Open `Tools > OpenCity > Rounded Spline Path Tool`, leave it on **Round Existing
   Spline**.
3. Click **Use Selected GameObject** (with the spline selected), or drag it into **Spline
   Container**.
4. Set **Corner Radius**, click **Round Corners**.

**Drawing a new path by clicking waypoints:**
1. Open the tool, switch to **Draw New Waypoints**.
2. Set **Corner Radius** and a **Target Name**.
3. Click **Start Placing**, then left-click in the Scene view to lay down waypoints — a
   live preview (yellow points, cyan curve) updates as you go. **Undo Last Point** fixes a
   stray click; **Alt**+drag still orbits/pans normally.
4. **Finish Placing** (or press Escape) when done, then **Build Spline**.

Both actions are a single Undo step (**Ctrl+Z** reverts the whole operation).

## How it works

Both modes funnel into one shared function, `SplineFilletUtility.BuildRoundedSpline`, so
the fillet math exists exactly once and the on-screen preview can never disagree with what
Build/Round actually produces:

1. **Classify each waypoint.** The first/last waypoint of an open path are always plain
   endpoints. An interior waypoint whose deflection angle (the angle between the incoming
   and outgoing straight directions) is under 0.5° is treated as straight-through (no
   fillet — avoids an imperceptible, degenerate arc). Everything else is a real corner.
2. **Place the fillet.** For a corner with deflection angle `φ` and the requested radius
   `R`, the fillet touches each straight segment at distance `t = R * tan(φ/2)` from the
   corner (the standard civil-engineering horizontal-curve formula) — this replaces the one
   sharp knot with two knots, joined by a single Bezier curve approximating the circular
   arc between them (handle length `R * 4/3 * tan(φ/4)`, the standard single-cubic-Bezier
   circle approximation).
3. **Clamp to fit.** `t` never exceeds half the length of either adjacent straight
   segment, so two corners sharing a short segment can never overlap. If the radius had to
   shrink to fit, the arc's handle length is recomputed from the *effective* (clamped)
   radius, not the requested one — otherwise the arc would visibly overshoot the space it
   was actually given.
4. **Keep the straight segments straight — and numerically well-behaved.** The tangent on
   the "straight-facing" side of every knot (including plain pass-through waypoints) is set
   to point at its neighbor with magnitude = distance/3 (the standard 1/3-rule for a
   Bezier segment that's geometrically a straight line), rather than using
   `TangentMode.Linear`'s literal-zero convention. This is a deliberate choice: testing this
   session found that a hard, discontinuous-tangent knot with literal-zero tangents can
   introduce a noticeable position error in `SplineUtility.GetPointAtLinearDistance` when a
   single call jumps a large distance across it — the small non-zero 1/3-rule tangent
   avoids that edge case entirely while remaining geometrically dead straight.
5. **Materialize.** The target spline is cleared and rebuilt from the resulting knot list,
   every knot using `TangentMode.Broken` (the only mode Unity's Spline never silently
   overwrites, and the package's own internal default).

## Window reference

### Rounding
| Field | Description |
|---|---|
| Corner Radius (m) | Single radius applied to every corner, shared by both modes. Clamped down per-corner where segments are too short to fit it (see "How it works"). |

### Round Existing Spline
| Field | Description |
|---|---|
| Spline Container | The `SplineContainer` to round in place. |
| Use Selected GameObject | Grabs the `SplineContainer` off the currently selected Hierarchy object. |
| Spline Index | Only shown when the container has more than one spline. |
| **Round Corners** | Rebuilds that spline's knots in place (same knot count in, `waypoints + 2×corners` out). Undo-safe (`Ctrl+Z` restores the original knots exactly). Re-clickable after nudging Corner Radius. |

### Draw New Waypoints
| Field | Description |
|---|---|
| Target Name | Name of the GameObject the spline is built into. An existing object with this exact name (under Parent Container, or scene root) is reused on rebuild; otherwise one is created. |
| Parent Container | Optional transform to nest the target under. Empty = scene root. |
| Target Container (optional) | Build directly into an existing `SplineContainer` instead of auto-creating/reusing one by name. |
| Closed Path | Loop the path from the last waypoint back to the first. |
| Ground Plane Y | Fallback height for a click that doesn't hit any Scene geometry. |
| Start Placing / Finish Placing | Toggle the Scene-view click session. While placing, left-click adds a waypoint; Escape has the same effect as Finish Placing (keeps the points). |
| Undo Last Point | Removes the most recently placed waypoint. Works whether or not you're currently placing. |
| Clear Points | Discards all placed waypoints and stops placing. |
| **Build Spline** | Builds/rebuilds the target container's spline from the current waypoints. Re-clickable — does not clear the waypoints, so you can nudge points or the radius and rebuild repeatedly. |

### Live preview (both modes)
A read-only box shows waypoint count, corner count (and how many were radius-clamped), and
the resulting knot count, computed by the same `ComputePreview` call the action buttons
use — so it can't drift from what actually gets built. In **Draw New Waypoints**, the Scene
view also shows a live cyan curve preview of the rounded result as you place points.

## Safety / limitations

- **Undo-safe**: both "Round Corners" and "Build Spline" wrap their whole operation
  (including reusing/mutating an existing object) in one Undo group.
- **Name-based lookup**: like the Spline Road Generator, "Draw New Waypoints" finds its own
  previously-built target purely by exact GameObject name under Parent Container (or scene
  root) — don't reuse **Target Name** for an unrelated object.
- **Single-arc corners only**: each corner is approximated by one cubic Bezier segment,
  which is visually accurate up to roughly a 90-120° turn. Beyond that the arc starts to
  look faceted/bulgy rather than circular; add an extra waypoint to split a very sharp turn
  into two gentler ones rather than expecting one corner to handle it.
- **No synthetic Scene-view input for testing**: the click-to-place interaction itself
  (mouse-down handling, ground-plane/geometry raycast, the live Handles preview) can only
  be exercised by a human in the Editor — it was verified this way, but isn't (and can't
  be) covered by an automated test the way the geometry core and the Undo/build paths were.
- **Scene View click priority**: while placing, the tool claims scene-view clicks via
  `HandleUtility.AddDefaultControl` so a click doesn't also select/deselect other objects —
  `Alt`-held clicks (orbit/pan) are explicitly passed through untouched.

## Files touched by this feature
- `Assets/Editor/SplineFilletUtility.cs` — the geometry core.
- `Assets/Editor/RoundedSplinePathWindow.cs` — the tool.
- `Docs/RoundedSplinePath.md` — this document.
