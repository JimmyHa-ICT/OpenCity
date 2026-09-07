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
        public const string TJunctionDefaultPath = "Assets/Prefabs/RoadShapes/road-side.prefab";
        public const string FourWayDefaultPath = "Assets/Prefabs/RoadShapes/road-crossroad.prefab";

        private static readonly Direction[] ClockwiseOrder =
            { Direction.North, Direction.East, Direction.South, Direction.West };

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

        private static Dictionary<ShapeCategory, Direction> BuildOpenAtZeroByCategory()
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
        /// prefab's pivot to its true footprint center - the negative of its combined
        /// renderer bounds' center. A no-op for prefabs whose pivot is already centered
        /// (straight/T/4-way/dead-end); corrects road-bend, whose pivot sits at the inside
        /// corner of its L shape. Y is deliberately left untouched so a prefab whose mesh
        /// isn't Y-symmetric about its pivot (curbs, barriers) doesn't get nudged off the
        /// ground plane.
        /// </summary>
        public static Vector3 ComputePivotOffset(GameObject prefab)
        {
            Vector3 boundsCenter = MeasureRendererBounds(prefab).center;
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
