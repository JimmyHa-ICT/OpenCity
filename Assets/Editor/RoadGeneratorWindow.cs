using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Editor window that rasterizes a Spline onto a fixed-size world grid and places
    /// modular road prefabs by a connectivity "grammar": each occupied cell's set of
    /// occupied N/E/S/W neighbors picks a shape category (dead-end / straight / bend /
    /// T-junction / 4-way) and a 90-degree rotation. Grid-aligned positions and only
    /// cardinal rotations make gaps/overlaps at corners structurally impossible, unlike
    /// the previous continuous arc-length-walking approach. The grammar/placement engine
    /// itself lives in RoadGridPlacer, shared with TilemapRoadGeneratorWindow. See
    /// Docs/RoadGenerator.md.
    /// </summary>
    public class RoadGeneratorWindow : EditorWindow
    {
        private const string UndoLabel = "Generate Road";

        [SerializeField] private SplineContainer splineContainer;
        [SerializeField] private int splineIndex;

        [SerializeField] private float cellSizeOverride;
        [SerializeField] private int samplesPerCell = 4;
        [SerializeField] private float groundY;

        [SerializeField] private RoadGridPlacer.RoadShapeConfig deadEnd = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig straight = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig bend = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig tJunction = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig fourWay = new RoadGridPlacer.RoadShapeConfig();

        [SerializeField] private string parentName = "Road";
        [SerializeField] private Transform parentUnder;

        [MenuItem("Tools/OpenCity/Road Generator")]
        private static void Open()
        {
            RoadGeneratorWindow window = GetWindow<RoadGeneratorWindow>("Road Generator");
            window.minSize = new Vector2(360, 520);
        }

        private void OnEnable()
        {
            RoadGridPlacer.LoadDefaults(deadEnd, straight, bend, tJunction, fourWay);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Spline", EditorStyles.boldLabel);
            splineContainer = (SplineContainer)EditorGUILayout.ObjectField(
                "Spline Container", splineContainer, typeof(SplineContainer), true);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Use Selected GameObject", GUILayout.Width(190)))
                {
                    UseSelection();
                }
            }

            if (splineContainer != null && splineContainer.Splines.Count > 1)
            {
                string[] options = new string[splineContainer.Splines.Count];
                for (int i = 0; i < options.Length; i++)
                {
                    options[i] = $"[{i}] {splineContainer.Splines[i].Count} knots";
                }
                splineIndex = EditorGUILayout.Popup("Spline Index", splineIndex, options);
            }
            else
            {
                splineIndex = 0;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Grid", EditorStyles.boldLabel);
            cellSizeOverride = EditorGUILayout.FloatField(new GUIContent(
                "Cell Size Override",
                "0 = auto-detect from the Straight prefab's renderer bounds along X."), cellSizeOverride);
            samplesPerCell = Mathf.Max(1, EditorGUILayout.IntField(new GUIContent(
                "Samples Per Cell",
                "How many times the spline is sampled per cell width. Raise this if tight " +
                "curves produce visibly wrong corner placement."), samplesPerCell));
            groundY = EditorGUILayout.FloatField(new GUIContent(
                "Ground Y",
                "World Y every placed tile is placed at. This is a flat-grid generator - " +
                "there is no per-cell height/ramp support."), groundY);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Shapes", EditorStyles.boldLabel);
            DrawShapeField("Dead-End Prefab", deadEnd, "Placed at a path endpoint (1 open side). Required.");
            DrawShapeField("Straight Prefab", straight, "Placed where two opposite sides are open. Required.");
            DrawShapeField("Bend Prefab", bend, "Placed where two adjacent sides are open. Required.");
            DrawShapeField("T-Junction Prefab", tJunction,
                "Placed where three sides are open. Optional - if left empty and a T-junction " +
                "cell occurs, that cell is skipped (with a console warning).");
            DrawShapeField("Four-Way Prefab", fourWay,
                "Placed where all four sides are open. Optional - if left empty and a 4-way " +
                "cell occurs, that cell is skipped (with a console warning).");
            EditorGUILayout.HelpBox(
                "Each field is a manual cosmetic-variant slot - e.g. swap in road-straight-barrier " +
                "here for a barrier look. The tool never auto-picks between kit siblings.",
                MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Placement", EditorStyles.boldLabel);
            parentName = EditorGUILayout.TextField(new GUIContent(
                "Parent Name",
                "Name of the GameObject the tiles are generated under. Generate replaces any " +
                "existing object with this exact name (see Clear)."), parentName);
            parentUnder = (Transform)EditorGUILayout.ObjectField(new GUIContent(
                "Parent Container",
                "Optional. Where the generated root is nested. Leave empty for scene root."), parentUnder, typeof(Transform), true);

            EditorGUILayout.Space();
            DrawPreview();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = CanGenerate(out _);
                if (GUILayout.Button("Generate", GUILayout.Height(30)))
                {
                    Generate();
                }
                GUI.enabled = true;
                if (GUILayout.Button("Clear", GUILayout.Height(30), GUILayout.Width(80)))
                {
                    Clear();
                }
            }
        }

        private void DrawShapeField(string label, RoadGridPlacer.RoadShapeConfig config, string tooltip)
        {
            config.prefab = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent(label, tooltip), config.prefab, typeof(GameObject), false);
        }

        private void DrawPreview()
        {
            if (!CanGenerate(out string error))
            {
                if (!string.IsNullOrEmpty(error))
                {
                    EditorGUILayout.HelpBox(error, MessageType.Warning);
                }
                return;
            }

            Spline spline = splineContainer.Splines[splineIndex];
            float totalLength = spline.GetLength();
            float cellSize = ResolveCellSize();

            List<Vector2Int> orderedPath = Rasterize(spline, cellSize);
            Dictionary<Vector2Int, RoadGridPlacer.Direction> graph = RoadGridPlacer.BuildGraph(orderedPath);
            RoadGridPlacer.CountByCategory(orderedPath, graph, out int[] counts, out int skipped);

            string message =
                $"Spline length: {totalLength:F2} m\n" +
                $"Cell size: {cellSize:F3} m\n" +
                $"Cells: {orderedPath.Count}  (dead-end {counts[(int)RoadGridPlacer.ShapeCategory.DeadEnd]}, " +
                $"straight {counts[(int)RoadGridPlacer.ShapeCategory.Straight]}, bend {counts[(int)RoadGridPlacer.ShapeCategory.Bend]}, " +
                $"T-junction {counts[(int)RoadGridPlacer.ShapeCategory.TJunction]}, 4-way {counts[(int)RoadGridPlacer.ShapeCategory.FourWay]})";
            if (skipped > 0)
            {
                message += $"\n{skipped} cell(s) could not be classified and will be skipped.";
            }
            EditorGUILayout.HelpBox(message, MessageType.None);

            if (splineContainer.transform.rotation != Quaternion.identity ||
                splineContainer.transform.localScale != Vector3.one)
            {
                EditorGUILayout.HelpBox(
                    "Spline Container's transform has non-identity rotation/scale - this desyncs the " +
                    "grid axes from world X/Z. Translation-only is safe; rotating or scaling it is not.",
                    MessageType.Warning);
            }
        }

        private bool CanGenerate(out string error)
        {
            if (splineContainer == null)
            {
                error = "Assign a Spline Container.";
                return false;
            }
            if (splineIndex < 0 || splineIndex >= splineContainer.Splines.Count)
            {
                error = "Invalid spline index.";
                return false;
            }
            Spline spline = splineContainer.Splines[splineIndex];
            if (spline.Count < 2)
            {
                error = "Spline needs at least 2 knots.";
                return false;
            }
            if (spline.GetLength() <= 0.0001f)
            {
                error = "Spline length is zero.";
                return false;
            }
            if (deadEnd.prefab == null || straight.prefab == null || bend.prefab == null)
            {
                error = "Assign Dead-End, Straight, and Bend prefabs (required).";
                return false;
            }
            float cellSize = ResolveCellSize();
            if (cellSize <= 0.0001f)
            {
                error = "Could not determine cell size (no Renderer found on Straight prefab). Set Cell Size Override manually.";
                return false;
            }
            error = null;
            return true;
        }

        private void UseSelection()
        {
            GameObject go = Selection.activeGameObject;
            if (go == null)
            {
                Debug.LogWarning("[RoadGenerator] Nothing selected.");
                return;
            }
            SplineContainer sc = go.GetComponent<SplineContainer>();
            if (sc == null)
            {
                Debug.LogWarning($"[RoadGenerator] '{go.name}' has no SplineContainer component.");
                return;
            }
            splineContainer = sc;
            splineIndex = 0;
            Repaint();
        }

        private float ResolveCellSize()
        {
            return RoadGridPlacer.ResolveCellSize(cellSizeOverride, straight.prefab);
        }

        private void Generate()
        {
            if (!CanGenerate(out string error))
            {
                Debug.LogError($"[RoadGenerator] {error}");
                return;
            }

            Spline spline = splineContainer.Splines[splineIndex];
            float totalLength = spline.GetLength();
            float cellSize = ResolveCellSize();

            List<Vector2Int> orderedPath = Rasterize(spline, cellSize);
            if (orderedPath.Count < 2)
            {
                Debug.LogWarning("[RoadGenerator] Spline is shorter than one cell - nothing to generate.");
                return;
            }
            Dictionary<Vector2Int, RoadGridPlacer.Direction> graph = RoadGridPlacer.BuildGraph(orderedPath);

            Dictionary<RoadGridPlacer.ShapeCategory, RoadGridPlacer.RoadShapeConfig> configByCategory =
                RoadGridPlacer.BuildConfigMap(deadEnd, straight, bend, tJunction, fourWay);

            RoadGridPlacer.PlaceResult result = RoadGridPlacer.PlaceCells(
                orderedPath, graph, cell => CellToWorld(cell, cellSize), configByCategory,
                parentName, parentUnder, UndoLabel, "RoadGenerator");

            Debug.Log($"[RoadGenerator] Generated {result.TotalCells - result.SkippedCount} tile(s) " +
                       $"(dead-end {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.DeadEnd]}, straight {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.Straight]}, " +
                       $"bend {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.Bend]}, T-junction {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.TJunction]}, " +
                       $"4-way {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.FourWay]}, skipped {result.SkippedCount}) " +
                       $"along '{splineContainer.name}' (spline length {totalLength:F2} m, cell size {cellSize:F3} m).");
        }

        /// <summary>
        /// Samples the spline at a fixed small arc-length step and converts each sample to
        /// a grid cell, bridging any gap between consecutive samples' cells with an
        /// axis-aligned staircase walk so every consecutive pair in the result is
        /// orthogonally adjacent (Manhattan distance exactly 1). In practice, with the
        /// default Samples Per Cell, a bridge is at most a single diagonal step - the loop
        /// handles a larger gap defensively (e.g. a very low Samples Per Cell) without
        /// needing full Bresenham/DDA line-drawing between arbitrary endpoints.
        /// </summary>
        private List<Vector2Int> Rasterize(Spline spline, float cellSize)
        {
            var orderedPath = new List<Vector2Int>();
            Vector2Int? previousCell = null;

            float totalLength = spline.GetLength();
            float sampleStep = cellSize / samplesPerCell;

            float curT = 0f;
            AppendSampleCell(spline, curT, cellSize, orderedPath, ref previousCell);

            float remaining = totalLength;
            while (remaining > 0f)
            {
                float step = Mathf.Min(sampleStep, remaining);
                SplineUtility.GetPointAtLinearDistance<Spline>(spline, curT, step, out float nextT);
                curT = nextT;
                remaining -= step;
                AppendSampleCell(spline, curT, cellSize, orderedPath, ref previousCell);
            }

            return orderedPath;
        }

        private void AppendSampleCell(Spline spline, float t, float cellSize, List<Vector2Int> path, ref Vector2Int? previousCell)
        {
            SplineUtility.Evaluate<Spline>(spline, t, out float3 posLocal, out _, out _);
            Vector3 world = splineContainer.transform.TransformPoint((Vector3)posLocal);
            Vector2Int cell = WorldToCell(world, cellSize);
            RoadGridPlacer.WalkOrthogonalBridged(path, ref previousCell, cell);
        }

        private static Vector2Int WorldToCell(Vector3 world, float cellSize)
        {
            return new Vector2Int(Mathf.RoundToInt(world.x / cellSize), Mathf.RoundToInt(world.z / cellSize));
        }

        private Vector3 CellToWorld(Vector2Int cell, float cellSize)
        {
            return new Vector3(cell.x * cellSize, groundY, cell.y * cellSize);
        }

        private void Clear()
        {
            RoadGridPlacer.ClearGenerated(parentName, parentUnder, "RoadGenerator");
        }
    }
}
