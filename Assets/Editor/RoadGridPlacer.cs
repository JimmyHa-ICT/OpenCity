using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Shared grammar/placement engine for the road generator tools: given a set of
    /// occupied grid cells (however they were authored - rasterized from a Spline, or
    /// painted onto a Tilemap), builds the 4-neighbor connectivity graph, classifies each
    /// cell into a shape category, solves the 90-degree rotation that matches a prefab's
    /// canonical open-sides mask, corrects off-center pivots, and instantiates linked
    /// prefab instances under an undo-safe, name-based generated root. See
    /// Docs/RoadGenerator.md and Docs/TilemapRoadGenerator.md.
    /// </summary>
    internal static class RoadGridPlacer
    {
        [Flags]
        public enum Direction { None = 0, North = 1, East = 2, South = 4, West = 8 }

        public enum ShapeCategory { DeadEnd, Straight, Bend, TJunction, FourWay }

        [Serializable]
        public class RoadShapeConfig
        {
            public GameObject prefab;
        }

        public const string DeadEndDefaultPath = "Assets/Prefabs/RoadShapes/road-end.prefab";
        public const string StraightDefaultPath = "Assets/Prefabs/RoadShapes/road-straight.prefab";
        public const string BendDefaultPath = "Assets/Prefabs/RoadShapes/road-bend.prefab";
        public const string TJunctionDefaultPath = "Assets/Prefabs/road-intersection-path.prefab";
        public const string FourWayDefaultPath = "Assets/Prefabs/RoadShapes/road-crossroad.prefab";

        private static readonly Direction[] ClockwiseOrder =
            { Direction.North, Direction.East, Direction.South, Direction.West };

        /// <summary>
        /// Single-direction-bit -&gt; grid-cell delta, matching the neighbor offsets BuildGraph
        /// checks (North = +Y grid = +Z world). Shared by anything that needs to walk from a
        /// cell to a specific neighbor rather than just testing occupancy, e.g. cross-network
        /// connection detection.
        /// </summary>
        public static readonly Dictionary<Direction, Vector2Int> DirectionDeltas = new Dictionary<Direction, Vector2Int>
        {
            { Direction.North, new Vector2Int(0, 1) },
            { Direction.East, new Vector2Int(1, 0) },
            { Direction.South, new Vector2Int(0, -1) },
            { Direction.West, new Vector2Int(-1, 0) },
        };

        public static Direction Opposite(Direction dir)
        {
            switch (dir)
            {
                case Direction.North: return Direction.South;
                case Direction.East: return Direction.West;
                case Direction.South: return Direction.North;
                case Direction.West: return Direction.East;
                default: return Direction.None;
            }
        }

        public static readonly Direction DeadEndOpenAtZero = Direction.South;
        public static readonly Direction StraightOpenAtZero = Direction.West | Direction.East;
        public static readonly Direction BendOpenAtZero = Direction.North | Direction.East;
        public static readonly Direction TJunctionOpenAtZero = Direction.West | Direction.East | Direction.North;
        public static readonly Direction FourWayOpenAtZero = Direction.North | Direction.East | Direction.South | Direction.West;

        public readonly struct PlaceResult
        {
            public readonly GameObject Root;
            public readonly int[] PlacedCounts;
            public readonly int SkippedCount;
            public readonly int TotalCells;

            public PlaceResult(GameObject root, int[] placedCounts, int skippedCount, int totalCells)
            {
                Root = root;
                PlacedCounts = placedCounts;
                SkippedCount = skippedCount;
                TotalCells = totalCells;
            }
        }

        public static void LoadDefaults(RoadShapeConfig deadEnd, RoadShapeConfig straight, RoadShapeConfig bend,
            RoadShapeConfig tJunction, RoadShapeConfig fourWay)
        {
            if (deadEnd.prefab == null) deadEnd.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(DeadEndDefaultPath);
            if (straight.prefab == null) straight.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(StraightDefaultPath);
            if (bend.prefab == null) bend.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BendDefaultPath);
            if (tJunction.prefab == null) tJunction.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TJunctionDefaultPath);
            if (fourWay.prefab == null) fourWay.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FourWayDefaultPath);
        }

        public static Dictionary<ShapeCategory, RoadShapeConfig> BuildConfigMap(
            RoadShapeConfig deadEnd, RoadShapeConfig straight, RoadShapeConfig bend,
            RoadShapeConfig tJunction, RoadShapeConfig fourWay)
        {
            return new Dictionary<ShapeCategory, RoadShapeConfig>
            {
                { ShapeCategory.DeadEnd, deadEnd },
                { ShapeCategory.Straight, straight },
                { ShapeCategory.Bend, bend },
                { ShapeCategory.TJunction, tJunction },
                { ShapeCategory.FourWay, fourWay },
            };
        }

        /// <summary>
        /// The canonical (unrotated) open-sides mask per shape category. Public so anything
        /// resolving a rotation outside PlaceCells - e.g. the sub-road tools swapping a main-road
        /// tile for a connector piece - can reuse it instead of hand-copying the same table.
        /// </summary>
        public static Dictionary<ShapeCategory, Direction> BuildOpenAtZeroByCategory()
        {
            return new Dictionary<ShapeCategory, Direction>
            {
                { ShapeCategory.DeadEnd, DeadEndOpenAtZero },
                { ShapeCategory.Straight, StraightOpenAtZero },
                { ShapeCategory.Bend, BendOpenAtZero },
                { ShapeCategory.TJunction, TJunctionOpenAtZero },
                { ShapeCategory.FourWay, FourWayOpenAtZero },
            };
        }

        /// <summary>
        /// 0 = auto-detect from the Straight prefab's renderer bounds along X.
        /// </summary>
        public static float ResolveCellSize(float cellSizeOverride, GameObject straightPrefab)
        {
            if (cellSizeOverride > 0f)
            {
                return cellSizeOverride;
            }
            if (straightPrefab == null)
            {
                return 0f;
            }
            return MeasureRendererBounds(straightPrefab).size.x;
        }

        /// <summary>
        /// Appends <paramref name="target"/> to <paramref name="path"/>, bridging any gap
        /// from <paramref name="previousCell"/> with a greedy axis-aligned staircase walk
        /// (each step moves 1 cell toward the target along whichever axis has the larger
        /// remaining delta) so every consecutive pair in <paramref name="path"/> ends up
        /// orthogonally adjacent (Manhattan distance exactly 1). Used to turn a sequence of
        /// arbitrary samples (spline arc-length samples, Voronoi edge samples, ...) into a
        /// cell path with no diagonal jumps.
        /// </summary>
        public static void WalkOrthogonalBridged(List<Vector2Int> path, ref Vector2Int? previousCell, Vector2Int target)
        {
            if (previousCell == null)
            {
                path.Add(target);
                previousCell = target;
                return;
            }

            Vector2Int current = previousCell.Value;
            while (current != target)
            {
                int dx = target.x - current.x;
                int dy = target.y - current.y;
                if (Mathf.Abs(dx) >= Mathf.Abs(dy) && dx != 0)
                {
                    current = new Vector2Int(current.x + (dx > 0 ? 1 : -1), current.y);
                }
                else
                {
                    current = new Vector2Int(current.x, current.y + (dy > 0 ? 1 : -1));
                }
                path.Add(current);
            }
            previousCell = target;
        }

        public static Dictionary<Vector2Int, Direction> BuildGraph(IEnumerable<Vector2Int> occupiedCells)
        {
            var occupied = new HashSet<Vector2Int>(occupiedCells);
            var graph = new Dictionary<Vector2Int, Direction>();
            foreach (Vector2Int cell in occupied)
            {
                Direction mask = Direction.None;
                if (occupied.Contains(cell + new Vector2Int(0, 1))) mask |= Direction.North;
                if (occupied.Contains(cell + new Vector2Int(1, 0))) mask |= Direction.East;
                if (occupied.Contains(cell + new Vector2Int(0, -1))) mask |= Direction.South;
                if (occupied.Contains(cell + new Vector2Int(-1, 0))) mask |= Direction.West;
                graph[cell] = mask;
            }
            return graph;
        }

        /// <summary>
        /// Above this ratio of dominant-axis span to minor-axis span, a chain with
        /// non-aligned endpoints is left as its raw rasterized staircase instead of being
        /// corner-collapsed - see SimplifyStaircases. 3 means roughly "steeper than ~18
        /// degrees off either cardinal axis": anything shallower than that already reads as
        /// an intentional, evenly-spaced diagonal street once rasterized, and squashing it
        /// into one sharp elbow would look worse, not better.
        /// </summary>
        private const int DiagonalPreserveRatio = 3;

        private readonly struct StaircaseChain
        {
            public readonly Vector2Int Start;
            public readonly Vector2Int End;
            public readonly List<Vector2Int> Interior;

            public StaircaseChain(Vector2Int start, Vector2Int end, List<Vector2Int> interior)
            {
                Start = start;
                End = end;
                Interior = interior;
            }
        }

        /// <summary>
        /// Post-rasterization cleanup: collapses short zigzag jogs and unnecessarily
        /// fragmented staircases between junctions into either a single straight run or a
        /// single corner, without ever moving a dead-end/junction cell or a cell already
        /// adjacent to <paramref name="mainRoadCells"/> - so a caller's downstream
        /// main-road-connection detection never loses a candidate connection to this pass.
        /// See Docs/VoronoiSubRoadGenerator.md, "Simplify Staircases".
        ///
        /// A "chain" is a maximal run of degree-2 cells between two such anchors. Each
        /// chain is independently classified:
        /// - endpoints share an axis and every interior cell stays within
        ///   <paramref name="straightenToleranceCells"/> of it -&gt; straightened to a flat
        ///   run (the classic "one stray cell wobbling an otherwise flat run" artifact);
        /// - endpoints share an axis but interior cells wander further than that -&gt; left
        ///   alone (likely genuine Voronoi geometry, not rasterization noise, despite the
        ///   coincidence that the endpoints align);
        /// - endpoints differ on both axes and the chain is reasonably balanced between
        ///   them (see DiagonalPreserveRatio) -&gt; left alone, since the raw rasterizer
        ///   already produces an evenly-spaced, intentional-looking diagonal staircase for
        ///   a genuinely diagonal segment - forcing every such chain into one sharp elbow
        ///   would make near-45-degree stretches look worse, not better;
        /// - otherwise (one axis clearly dominates but endpoints don't exactly align) -&gt;
        ///   corner-collapsed into a single bend, the fewest bends geometrically possible
        ///   once the anchors themselves aren't allowed to move.
        ///
        /// Every replacement cell must pass <paramref name="isCellAllowed"/> and must not
        /// land on an existing cell outside its own chain or on any cell in
        /// <paramref name="mainRoadCells"/> (checked against the *original*, un-mutated
        /// cell set - not incrementally); two chains whose replacements would land on the
        /// same new cell are both rejected rather than arbitrated, keeping the result a
        /// pure function of the input with no enumeration-order dependence. A rejected (or
        /// left-alone) chain is left exactly as originally rasterized.
        /// </summary>
        public static List<Vector2Int> SimplifyStaircases(
            IReadOnlyCollection<Vector2Int> cells,
            HashSet<Vector2Int> mainRoadCells,
            int straightenToleranceCells,
            Func<Vector2Int, bool> isCellAllowed,
            out int straightenedCount,
            out int blockedCount)
        {
            straightenedCount = 0;
            blockedCount = 0;

            var originalSet = new HashSet<Vector2Int>(cells);
            Dictionary<Vector2Int, Direction> graph = BuildGraph(originalSet);

            var anchors = new HashSet<Vector2Int>();
            foreach (Vector2Int cell in originalSet)
            {
                if (Degree(graph[cell]) != 2 || IsAdjacentToAny(cell, mainRoadCells))
                {
                    anchors.Add(cell);
                }
            }

            var sortedAnchors = new List<Vector2Int>(anchors);
            sortedAnchors.Sort((p, q) => p.y != q.y ? p.y.CompareTo(q.y) : p.x.CompareTo(q.x));

            var consumed = new HashSet<Vector2Int>();
            var chains = new List<StaircaseChain>();

            foreach (Vector2Int anchor in sortedAnchors)
            {
                foreach (Direction dir in ClockwiseOrder)
                {
                    if ((graph[anchor] & dir) == 0)
                    {
                        continue;
                    }
                    Vector2Int first = anchor + DirectionDeltas[dir];
                    if (anchors.Contains(first) || consumed.Contains(first))
                    {
                        continue; // no interior cells to simplify, or already claimed from the other end
                    }

                    var interior = new List<Vector2Int>();
                    Vector2Int prev = anchor;
                    Vector2Int cur = first;
                    while (!anchors.Contains(cur))
                    {
                        interior.Add(cur);
                        consumed.Add(cur);
                        Vector2Int next = GetOtherNeighbor(cur, graph[cur], prev);
                        prev = cur;
                        cur = next;
                    }
                    chains.Add(new StaircaseChain(anchor, cur, interior));
                }
            }

            // Per-chain candidate rewrite - see the three-way classification in the doc
            // comment above. `newInterior` excludes the chain's own (unmoved) Start/End.
            var candidateChains = new List<StaircaseChain>();
            var candidateNewInteriors = new List<List<Vector2Int>>();

            foreach (StaircaseChain chain in chains)
            {
                if (chain.Interior.Count == 0)
                {
                    continue; // already minimal
                }

                int dx = chain.End.x - chain.Start.x;
                int dy = chain.End.y - chain.Start.y;
                if (dx == 0 && dy == 0)
                {
                    continue; // degenerate ring off a single anchor - leave as rasterized
                }

                List<Vector2Int> newPath = null;

                if (dx == 0 || dy == 0)
                {
                    bool withinTolerance = true;
                    foreach (Vector2Int c in chain.Interior)
                    {
                        int deviation = dy == 0 ? Mathf.Abs(c.y - chain.Start.y) : Mathf.Abs(c.x - chain.Start.x);
                        if (deviation > straightenToleranceCells)
                        {
                            withinTolerance = false;
                            break;
                        }
                    }
                    if (withinTolerance)
                    {
                        newPath = WalkPath(chain.Start, chain.End);
                    }
                    // else: endpoints coincide but the interior wanders too far to trust as
                    // noise - left alone.
                }
                else
                {
                    int absDx = Mathf.Abs(dx);
                    int absDy = Mathf.Abs(dy);
                    bool balancedDiagonal = Mathf.Min(absDx, absDy) * DiagonalPreserveRatio >= Mathf.Max(absDx, absDy);
                    if (!balancedDiagonal)
                    {
                        Vector2Int corner = absDx >= absDy
                            ? new Vector2Int(chain.End.x, chain.Start.y)
                            : new Vector2Int(chain.Start.x, chain.End.y);
                        List<Vector2Int> firstLeg = WalkPath(chain.Start, corner);
                        List<Vector2Int> secondLeg = WalkPath(corner, chain.End);
                        newPath = new List<Vector2Int>(firstLeg.Count + secondLeg.Count - 1);
                        newPath.AddRange(firstLeg);
                        for (int i = 1; i < secondLeg.Count; i++)
                        {
                            newPath.Add(secondLeg[i]);
                        }
                    }
                    // else: balanced diagonal, already reads as an intentional staircase -
                    // left alone.
                }

                if (newPath == null)
                {
                    continue;
                }
                List<Vector2Int> newInterior = newPath.Count > 2
                    ? newPath.GetRange(1, newPath.Count - 2)
                    : new List<Vector2Int>();
                candidateChains.Add(chain);
                candidateNewInteriors.Add(newInterior);
            }

            // Validate every candidate against the original, unmutated set - not
            // incrementally - then reject (both/all) chains whose new cells collide with
            // each other, so the result never depends on dictionary/set enumeration order.
            var valid = new bool[candidateChains.Count];
            for (int i = 0; i < valid.Length; i++)
            {
                valid[i] = true;
            }

            var claimedBy = new Dictionary<Vector2Int, List<int>>();
            for (int i = 0; i < candidateChains.Count; i++)
            {
                var ownInterior = new HashSet<Vector2Int>(candidateChains[i].Interior);
                foreach (Vector2Int cell in candidateNewInteriors[i])
                {
                    bool blockedByMainRoad = mainRoadCells != null && mainRoadCells.Contains(cell);
                    bool blockedByBoundary = !isCellAllowed(cell);
                    bool blockedByCollision = originalSet.Contains(cell) && !ownInterior.Contains(cell);
                    if (blockedByMainRoad || blockedByBoundary || blockedByCollision)
                    {
                        valid[i] = false;
                        break;
                    }
                }
                if (!valid[i])
                {
                    continue;
                }
                foreach (Vector2Int cell in candidateNewInteriors[i])
                {
                    if (!claimedBy.TryGetValue(cell, out List<int> claimants))
                    {
                        claimants = new List<int>();
                        claimedBy[cell] = claimants;
                    }
                    claimants.Add(i);
                }
            }
            foreach (List<int> claimants in claimedBy.Values)
            {
                if (claimants.Count > 1)
                {
                    foreach (int i in claimants)
                    {
                        valid[i] = false;
                    }
                }
            }

            var excludedOriginal = new HashSet<Vector2Int>();
            var addedNew = new List<Vector2Int>();
            for (int i = 0; i < candidateChains.Count; i++)
            {
                if (!valid[i])
                {
                    blockedCount++;
                    continue;
                }
                foreach (Vector2Int cell in candidateChains[i].Interior)
                {
                    excludedOriginal.Add(cell);
                }
                addedNew.AddRange(candidateNewInteriors[i]);
                straightenedCount++;
            }

            var result = new List<Vector2Int>(originalSet.Count + addedNew.Count);
            foreach (Vector2Int cell in originalSet)
            {
                if (!excludedOriginal.Contains(cell))
                {
                    result.Add(cell);
                }
            }
            result.AddRange(addedNew);
            return result;
        }

        private static int Degree(Direction mask)
        {
            int count = 0;
            if ((mask & Direction.North) != 0) count++;
            if ((mask & Direction.East) != 0) count++;
            if ((mask & Direction.South) != 0) count++;
            if ((mask & Direction.West) != 0) count++;
            return count;
        }

        private static bool IsAdjacentToAny(Vector2Int cell, HashSet<Vector2Int> others)
        {
            if (others == null || others.Count == 0)
            {
                return false;
            }
            foreach (KeyValuePair<Direction, Vector2Int> dir in DirectionDeltas)
            {
                if (others.Contains(cell + dir.Value))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Given a degree-2 cell's mask and one of its two neighbors, returns the other one.</summary>
        private static Vector2Int GetOtherNeighbor(Vector2Int cell, Direction mask, Vector2Int exclude)
        {
            foreach (KeyValuePair<Direction, Vector2Int> dir in DirectionDeltas)
            {
                if ((mask & dir.Key) == 0)
                {
                    continue;
                }
                Vector2Int neighbor = cell + dir.Value;
                if (neighbor != exclude)
                {
                    return neighbor;
                }
            }
            return exclude; // degenerate guard - shouldn't happen for a true degree-2 cell
        }

        /// <summary>Cell path from <paramref name="from"/> to <paramref name="to"/> (inclusive of both), via WalkOrthogonalBridged.</summary>
        private static List<Vector2Int> WalkPath(Vector2Int from, Vector2Int to)
        {
            var path = new List<Vector2Int>();
            Vector2Int? prev = null;
            WalkOrthogonalBridged(path, ref prev, from);
            WalkOrthogonalBridged(path, ref prev, to);
            return path;
        }

        public static bool TryClassify(Direction mask, out ShapeCategory category)
        {
            switch (mask)
            {
                case Direction.North | Direction.South:
                case Direction.East | Direction.West:
                    category = ShapeCategory.Straight;
                    return true;

                case Direction.North | Direction.East:
                case Direction.East | Direction.South:
                case Direction.South | Direction.West:
                case Direction.West | Direction.North:
                    category = ShapeCategory.Bend;
                    return true;

                case Direction.North:
                case Direction.East:
                case Direction.South:
                case Direction.West:
                    category = ShapeCategory.DeadEnd;
                    return true;

                case Direction.North | Direction.East | Direction.South:
                case Direction.East | Direction.South | Direction.West:
                case Direction.South | Direction.West | Direction.North:
                case Direction.West | Direction.North | Direction.East:
                    category = ShapeCategory.TJunction;
                    return true;

                case Direction.North | Direction.East | Direction.South | Direction.West:
                    category = ShapeCategory.FourWay;
                    return true;

                default:
                    category = default;
                    return false;
            }
        }

        public static bool TryFindRotationSteps(Direction defaultOpen, Direction target, out int steps)
        {
            for (steps = 0; steps < 4; steps++)
            {
                if (RotateClockwise(defaultOpen, steps) == target)
                {
                    return true;
                }
            }
            steps = 0;
            return false;
        }

        private static Direction RotateClockwise(Direction dirs, int steps)
        {
            Direction result = Direction.None;
            for (int i = 0; i < 4; i++)
            {
                if ((dirs & ClockwiseOrder[i]) != 0)
                {
                    result |= ClockwiseOrder[(i + steps) % 4];
                }
            }
            return result;
        }

        /// <summary>
        /// Horizontal-plane-only (XZ) offset, in prefab-local rotation-0 space, from the
        /// prefab's pivot to its true footprint center: the negative of where its combined
        /// renderer bounds sit *relative to its own root transform*. A no-op for prefabs
        /// whose mesh is already centered on the root (straight/bend/T/4-way/dead-end);
        /// corrects road-side, road-side-entry and road-side-exit, whose meshes sit against
        /// one edge of their cell. Y is deliberately left untouched so a prefab whose mesh
        /// isn't Y-symmetric about its pivot (curbs, barriers) doesn't get nudged off the
        /// ground plane.
        ///
        /// Subtracting the root's own local position matters: a prefab authored as a variant
        /// of an FBX can carry an offset on the root transform itself (road-bridge and
        /// road-slant-flat-curve in Assets/Prefabs/Roads/Highway both do). That offset is
        /// overwritten the instant an instance is positioned, so it is not a pivot error to
        /// correct - measuring it as one would shift those pieces off their cell by exactly
        /// the amount the root was offset by.
        /// </summary>
        public static Vector3 ComputePivotOffset(GameObject prefab)
        {
            Vector3 boundsCenter = MeasureRendererBounds(prefab).center - prefab.transform.localPosition;
            return new Vector3(-boundsCenter.x, 0f, -boundsCenter.z);
        }

        /// <summary>
        /// Union of every Renderer's bounds on the prefab asset. Assumes the prefab asset
        /// root is at local (0,0,0) with identity rotation/scale, so the asset's own
        /// (unparented) Renderer.bounds already read as prefab-local space - no
        /// instantiation needed.
        /// </summary>
        public static Bounds MeasureRendererBounds(GameObject prefab)
        {
            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return new Bounds(Vector3.zero, Vector3.zero);
            }
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }
            return bounds;
        }

        public static void CountByCategory(IEnumerable<Vector2Int> cells, Dictionary<Vector2Int, Direction> graph,
            out int[] counts, out int skipped)
        {
            counts = new int[5];
            skipped = 0;
            foreach (Vector2Int cell in cells)
            {
                if (TryClassify(graph[cell], out ShapeCategory category))
                {
                    counts[(int)category]++;
                }
                else
                {
                    skipped++;
                }
            }
        }

        public static GameObject FindGeneratedRoot(string parentName, Transform parentUnder)
        {
            if (string.IsNullOrEmpty(parentName))
            {
                return null;
            }
            if (parentUnder != null)
            {
                Transform found = parentUnder.Find(parentName);
                return found != null ? found.gameObject : null;
            }
            Scene scene = SceneManager.GetActiveScene();
            foreach (GameObject go in scene.GetRootGameObjects())
            {
                if (go.name == parentName)
                {
                    return go;
                }
            }
            return null;
        }

        public static void ClearGenerated(string parentName, Transform parentUnder, string logPrefix)
        {
            GameObject root = FindGeneratedRoot(parentName, parentUnder);
            if (root == null)
            {
                Debug.Log($"[{logPrefix}] No object named '{parentName}' found to clear.");
                return;
            }
            Undo.DestroyObjectImmediate(root);
            Debug.Log($"[{logPrefix}] Cleared '{parentName}'.");
        }

        private static void ClearGeneratedSilent(string parentName, Transform parentUnder)
        {
            GameObject root = FindGeneratedRoot(parentName, parentUnder);
            if (root != null)
            {
                Undo.DestroyObjectImmediate(root);
            }
        }

        /// <summary>
        /// Builds the connectivity graph's cells into placed prefab instances: classify -&gt;
        /// solve rotation -&gt; correct pivot -&gt; instantiate -&gt; parent, wrapped in one Undo
        /// group that also covers clearing any prior generation under the same name.
        /// <paramref name="orderedCells"/> drives placement order (and may contain
        /// duplicates, e.g. a rasterized path that revisits a cell) while
        /// <paramref name="cellToWorld"/> lets each front-end supply its own cell-to-world
        /// conversion (grid-multiply for the spline tool, <c>Tilemap.GetCellCenterWorld</c>
        /// for the tilemap tool).
        /// </summary>
        public static PlaceResult PlaceCells(
            IReadOnlyList<Vector2Int> orderedCells,
            Dictionary<Vector2Int, Direction> graph,
            Func<Vector2Int, Vector3> cellToWorld,
            Dictionary<ShapeCategory, RoadShapeConfig> configByCategory,
            string parentName,
            Transform parentUnder,
            string undoLabel,
            string logPrefix)
        {
            Dictionary<ShapeCategory, Direction> openAtZeroByCategory = BuildOpenAtZeroByCategory();
            var pivotOffsetByCategory = new Dictionary<ShapeCategory, Vector3>();
            foreach (KeyValuePair<ShapeCategory, RoadShapeConfig> entry in configByCategory)
            {
                pivotOffsetByCategory[entry.Key] = entry.Value.prefab != null
                    ? ComputePivotOffset(entry.Value.prefab)
                    : Vector3.zero;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoLabel);

            ClearGeneratedSilent(parentName, parentUnder);

            var root = new GameObject(parentName);
            Undo.RegisterCreatedObjectUndo(root, undoLabel);
            root.transform.SetParent(parentUnder, false);

            int[] placedCounts = new int[5];
            int skippedCount = 0;

            for (int i = 0; i < orderedCells.Count; i++)
            {
                Vector2Int cell = orderedCells[i];
                Direction mask = graph[cell];

                if (!TryClassify(mask, out ShapeCategory category))
                {
                    Debug.LogWarning($"[{logPrefix}] Cell {cell} has an unrecognized connectivity mask ({mask}) - skipped.");
                    skippedCount++;
                    continue;
                }

                RoadShapeConfig config = configByCategory[category];
                if (config.prefab == null)
                {
                    Debug.LogWarning($"[{logPrefix}] Cell {cell} needs a {category} prefab, which is not assigned - skipped.");
                    skippedCount++;
                    continue;
                }

                if (!TryFindRotationSteps(openAtZeroByCategory[category], mask, out int steps))
                {
                    Debug.LogWarning($"[{logPrefix}] Cell {cell} ({category}, mask {mask}) has no matching rotation - skipped.");
                    skippedCount++;
                    continue;
                }

                Quaternion rot = Quaternion.Euler(0f, steps * 90f, 0f);
                Vector3 worldPos = cellToWorld(cell) + rot * pivotOffsetByCategory[category];

                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(config.prefab, root.transform);
                instance.transform.SetPositionAndRotation(worldPos, rot);
                instance.name = $"{config.prefab.name}_{i:00}";
                Undo.RegisterCreatedObjectUndo(instance, undoLabel);
                placedCounts[(int)category]++;
            }

            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeGameObject = root;

            return new PlaceResult(root, placedCounts, skippedCount, orderedCells.Count);
        }
    }
}
