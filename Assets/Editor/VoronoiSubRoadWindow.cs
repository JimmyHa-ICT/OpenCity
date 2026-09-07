using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Editor window that generates an interior street network inside a closed-loop main
    /// road by scattering Voronoi sites and rasterizing the Voronoi diagram's edges onto
    /// the grid, placed by the same connectivity "grammar" as RoadGeneratorWindow and
    /// TilemapRoadGeneratorWindow. Sub-roads are kept strictly inside the loop (inset by
    /// Boundary Margin) and never connect to it - this generates a fully independent
    /// "SubRoads" object with its own Clear/Generate that never reads or unions whatever
    /// the main Road Generator already placed. The geometry (Delaunay triangulation,
    /// Voronoi dual, polygon tests) lives in VoronoiGeometry; the grammar/placement engine
    /// is RoadGridPlacer, shared with the other two tools. See
    /// Docs/VoronoiSubRoadGenerator.md.
    /// </summary>
    public class VoronoiSubRoadWindow : EditorWindow
    {
        private const string UndoLabel = "Generate Sub-Roads (Voronoi)";
        private const float RunawayMultiplier = 3f;

        private struct NetworkResult
        {
            public bool Success;
            public string Error;
            public List<Vector2> Polygon;
            public float PolygonLength;
            public int RequestedSiteCount;
            public List<Vector2> AcceptedSites;
            public int TriangleCount;
            public int KeptEdgeCount;
            public int DroppedHullEdgeCount;
            public int DroppedRunawayEdgeCount;
            public List<Vector2Int> Cells;
            public float CellSize;
        }

        [SerializeField] private SplineContainer loopSpline;
        [SerializeField] private int splineIndex;

        [SerializeField] private int siteCount = 40;
        [SerializeField] private float minSiteSpacing = 4f;
        [SerializeField] private float boundaryMargin = 2f;
        [SerializeField] private int randomSeed = 12345;
        [SerializeField] private float polygonSampleStep = 0.5f;
        [SerializeField] private float edgeSampleStep = 0.5f;

        [SerializeField] private float cellSizeOverride;
        [SerializeField] private float groundY;

        [SerializeField] private RoadGridPlacer.RoadShapeConfig deadEnd = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig straight = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig bend = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig tJunction = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig fourWay = new RoadGridPlacer.RoadShapeConfig();

        [SerializeField] private string parentName = "SubRoads";
        [SerializeField] private Transform parentUnder;

        [MenuItem("Tools/OpenCity/Sub-Road Generator (Voronoi)")]
        private static void Open()
        {
            VoronoiSubRoadWindow window = GetWindow<VoronoiSubRoadWindow>("Sub-Road Generator (Voronoi)");
            window.minSize = new Vector2(360, 620);
        }

        private void OnEnable()
        {
            RoadGridPlacer.LoadDefaults(deadEnd, straight, bend, tJunction, fourWay);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Loop", EditorStyles.boldLabel);
            loopSpline = (SplineContainer)EditorGUILayout.ObjectField(
                "Spline Container", loopSpline, typeof(SplineContainer), true);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Use Selected GameObject", GUILayout.Width(190)))
                {
                    UseSelection();
                }
            }

            if (loopSpline != null && loopSpline.Splines.Count > 1)
            {
                string[] options = new string[loopSpline.Splines.Count];
                for (int i = 0; i < options.Length; i++)
                {
                    options[i] = $"[{i}] {loopSpline.Splines[i].Count} knots";
                }
                splineIndex = EditorGUILayout.Popup("Spline Index", splineIndex, options);
            }
            else
            {
                splineIndex = 0;
            }
            EditorGUILayout.HelpBox(
                "The referenced spline must be Closed (a loop) - it defines the interior area " +
                "the sub-road network is generated inside of.", MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Voronoi", EditorStyles.boldLabel);
            siteCount = Mathf.Max(3, EditorGUILayout.IntField(new GUIContent(
                "Site Count",
                "How many Voronoi sites to scatter inside the loop. More sites make a denser street network."), siteCount));
            minSiteSpacing = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent(
                "Min Site Spacing",
                "Minimum world-space distance between accepted sites - keeps blocks from being tiny."), minSiteSpacing));
            boundaryMargin = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent(
                "Boundary Margin",
                "Minimum distance sites and road cells must stay from the main loop's boundary. " +
                "Sub-roads never connect to the main loop - they always stay at least this far inside it."), boundaryMargin));
            randomSeed = EditorGUILayout.IntField(new GUIContent(
                "Random Seed",
                "Same seed + same settings always scatters the same sites and produces the same network."), randomSeed);
            polygonSampleStep = Mathf.Max(0.05f, EditorGUILayout.FloatField(new GUIContent(
                "Polygon Sample Step",
                "Arc-length step used to sample the loop spline into a polygon for site scattering and clipping."), polygonSampleStep));
            edgeSampleStep = Mathf.Max(0.05f, EditorGUILayout.FloatField(new GUIContent(
                "Edge Sample Step",
                "World-space step used to sample each Voronoi edge when rasterizing it onto the grid."), edgeSampleStep));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Grid", EditorStyles.boldLabel);
            cellSizeOverride = EditorGUILayout.FloatField(new GUIContent(
                "Cell Size Override",
                "0 = auto-detect from the Straight prefab's renderer bounds along X."), cellSizeOverride);
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
                "existing object with this exact name (see Clear). Independent from the main " +
                "Road Generator's output - never reads or overwrites it."), parentName);
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

            NetworkResult net = BuildNetwork();
            if (!net.Success)
            {
                EditorGUILayout.HelpBox(net.Error, MessageType.Warning);
                return;
            }

            RoadGridPlacer.CountByCategory(net.Cells, RoadGridPlacer.BuildGraph(net.Cells), out int[] counts, out int skipped);

            string message =
                $"Polygon: {net.Polygon.Count} vertices (loop length {net.PolygonLength:F2} m)\n" +
                $"Sites: accepted {net.AcceptedSites.Count} / requested {net.RequestedSiteCount}\n" +
                $"Delaunay triangles: {net.TriangleCount}\n" +
                $"Voronoi edges: kept {net.KeptEdgeCount}, dropped {net.DroppedHullEdgeCount} (hull), {net.DroppedRunawayEdgeCount} (runaway)\n" +
                $"Cells: {net.Cells.Count}  (dead-end {counts[(int)RoadGridPlacer.ShapeCategory.DeadEnd]}, " +
                $"straight {counts[(int)RoadGridPlacer.ShapeCategory.Straight]}, bend {counts[(int)RoadGridPlacer.ShapeCategory.Bend]}, " +
                $"T-junction {counts[(int)RoadGridPlacer.ShapeCategory.TJunction]}, 4-way {counts[(int)RoadGridPlacer.ShapeCategory.FourWay]})";
            if (skipped > 0)
            {
                message += $"\n{skipped} cell(s) could not be classified and will be skipped.";
            }
            EditorGUILayout.HelpBox(message, MessageType.None);

            if (net.AcceptedSites.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Boundary Margin leaves no interior room inside the loop - reduce Boundary Margin or Min Site Spacing.",
                    MessageType.Warning);
            }
            else if (net.AcceptedSites.Count < net.RequestedSiteCount)
            {
                EditorGUILayout.HelpBox(
                    "Accepted fewer sites than requested - lower Min Site Spacing/Boundary Margin, or reduce Site Count.",
                    MessageType.Warning);
            }
            if (net.AcceptedSites.Count >= 3 && net.KeptEdgeCount == 0)
            {
                EditorGUILayout.HelpBox(
                    "No interior road segments survived clipping - network will be empty.", MessageType.Warning);
            }

            if (loopSpline.transform.rotation != Quaternion.identity ||
                loopSpline.transform.localScale != Vector3.one)
            {
                EditorGUILayout.HelpBox(
                    "Spline Container's transform has non-identity rotation/scale - this desyncs the " +
                    "grid axes from world X/Z. Translation-only is safe; rotating or scaling it is not.",
                    MessageType.Warning);
            }
        }

        private bool CanGenerate(out string error)
        {
            if (loopSpline == null)
            {
                error = "Assign a Spline Container for the main loop.";
                return false;
            }
            if (splineIndex < 0 || splineIndex >= loopSpline.Splines.Count)
            {
                error = "Invalid spline index.";
                return false;
            }
            Spline spline = loopSpline.Splines[splineIndex];
            if (!spline.Closed)
            {
                error = "The referenced spline must be Closed (loop) to define an interior area.";
                return false;
            }
            if (spline.Count < 3)
            {
                error = "Spline needs at least 3 knots to form a loop area.";
                return false;
            }
            if (siteCount < 3)
            {
                error = "Site Count must be at least 3 to form a Voronoi network.";
                return false;
            }
            if (minSiteSpacing < 0f || boundaryMargin < 0f)
            {
                error = "Min Site Spacing and Boundary Margin must be non-negative.";
                return false;
            }
            if (deadEnd.prefab == null || straight.prefab == null || bend.prefab == null)
            {
                error = "Assign Dead-End, Straight, and Bend prefabs (required).";
                return false;
            }
            float cellSize = RoadGridPlacer.ResolveCellSize(cellSizeOverride, straight.prefab);
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
                Debug.LogWarning("[VoronoiSubRoads] Nothing selected.");
                return;
            }
            SplineContainer sc = go.GetComponent<SplineContainer>();
            if (sc == null)
            {
                Debug.LogWarning($"[VoronoiSubRoads] '{go.name}' has no SplineContainer component.");
                return;
            }
            loopSpline = sc;
            splineIndex = 0;
            Repaint();
        }

        /// <summary>
        /// Runs the whole pipeline (polygon sampling -&gt; site scattering -&gt; triangulation
        /// -&gt; Voronoi dual -&gt; interior clipping -&gt; grid rasterization) purely from the
        /// current serialized field values. This is the single code path both DrawPreview
        /// and Generate call - there is no second, separately-maintained implementation -
        /// which is what actually guarantees the preview and the real output can never
        /// drift apart.
        /// </summary>
        private NetworkResult BuildNetwork()
        {
            var result = new NetworkResult { RequestedSiteCount = siteCount };

            if (!CanGenerate(out string error))
            {
                result.Success = false;
                result.Error = error;
                return result;
            }

            Spline spline = loopSpline.Splines[splineIndex];
            float polyStep = Mathf.Max(0.05f, polygonSampleStep);
            List<Vector2> polygon = SamplePolygon(spline, polyStep);
            result.Polygon = polygon;
            result.PolygonLength = spline.GetLength();

            if (polygon.Count < 3)
            {
                result.Success = false;
                result.Error = "Polygon Sample Step is too coarse for this spline's length - reduce it.";
                return result;
            }

            float cellSize = RoadGridPlacer.ResolveCellSize(cellSizeOverride, straight.prefab);
            result.CellSize = cellSize;

            List<Vector2> sites = VoronoiGeometry.ScatterSites(
                polygon, siteCount, minSiteSpacing, boundaryMargin, randomSeed, out _);
            result.AcceptedSites = sites;
            result.Success = true;

            if (sites.Count < 3)
            {
                result.TriangleCount = 0;
                result.KeptEdgeCount = 0;
                result.Cells = new List<Vector2Int>();
                return result;
            }

            List<VoronoiGeometry.Triangle> triangles = VoronoiGeometry.Triangulate(sites);
            result.TriangleCount = triangles.Count;

            Dictionary<VoronoiGeometry.EdgeKey, List<int>> edgeMap = VoronoiGeometry.BuildEdgeToTriangleMap(triangles);
            Vector2 centroid = VoronoiGeometry.PolygonCentroid(polygon);
            float boundingRadius = VoronoiGeometry.PolygonBoundingRadius(polygon, centroid);

            VoronoiGeometry.BuildVoronoiSegments(
                triangles, edgeMap, centroid, boundingRadius, RunawayMultiplier,
                out List<(Vector2 a, Vector2 b)> segments, out int droppedHull, out int droppedRunaway);
            result.KeptEdgeCount = segments.Count;
            result.DroppedHullEdgeCount = droppedHull;
            result.DroppedRunawayEdgeCount = droppedRunaway;

            float edgeStep = Mathf.Max(0.05f, edgeSampleStep);
            var allCells = new List<Vector2Int>();
            foreach ((Vector2 a, Vector2 b) in segments)
            {
                RasterizeInteriorSegment(a, b, polygon, cellSize, edgeStep, allCells);
            }

            var distinctCells = new HashSet<Vector2Int>(allCells);
            var sortedCells = new List<Vector2Int>(distinctCells);
            sortedCells.Sort((p, q) => p.y != q.y ? p.y.CompareTo(q.y) : p.x.CompareTo(q.x));
            result.Cells = sortedCells;

            return result;
        }

        /// <summary>
        /// Samples a segment at fixed world-space steps and rasterizes only the maximal
        /// sub-runs of samples that stay inside the loop polygon by at least Boundary
        /// Margin. A run never bridges across a sample that falls outside that interior
        /// band - a real gap (the segment briefly leaving the safe zone) must not be
        /// silently stitched over with a staircase that would cut through the excluded
        /// margin band.
        /// </summary>
        private void RasterizeInteriorSegment(Vector2 p0, Vector2 p1, List<Vector2> polygon, float cellSize, float sampleStep, List<Vector2Int> outCells)
        {
            float length = Vector2.Distance(p0, p1);
            int sampleCount = Mathf.Max(1, Mathf.CeilToInt(length / sampleStep));

            List<Vector2Int> currentRun = null;
            Vector2Int? previousCell = null;

            for (int i = 0; i <= sampleCount; i++)
            {
                float t = (float)i / sampleCount;
                Vector2 sample = Vector2.Lerp(p0, p1, t);
                bool interior = VoronoiGeometry.PointInPolygon(sample, polygon) &&
                                 VoronoiGeometry.DistanceToPolygonBoundary(sample, polygon) >= boundaryMargin;

                if (interior)
                {
                    if (currentRun == null)
                    {
                        currentRun = new List<Vector2Int>();
                        previousCell = null;
                    }
                    var cell = new Vector2Int(Mathf.RoundToInt(sample.x / cellSize), Mathf.RoundToInt(sample.y / cellSize));
                    RoadGridPlacer.WalkOrthogonalBridged(currentRun, ref previousCell, cell);
                }
                else if (currentRun != null)
                {
                    outCells.AddRange(currentRun);
                    currentRun = null;
                }
            }

            if (currentRun != null)
            {
                outCells.AddRange(currentRun);
            }
        }

        private List<Vector2> SamplePolygon(Spline spline, float step)
        {
            var polygon = new List<Vector2>();
            float totalLength = spline.GetLength();
            if (totalLength <= 0.0001f)
            {
                return polygon;
            }

            float curT = 0f;
            AppendPolygonSample(spline, curT, polygon);

            float remaining = totalLength;
            while (remaining > step)
            {
                SplineUtility.GetPointAtLinearDistance<Spline>(spline, curT, step, out float nextT);
                curT = nextT;
                remaining -= step;
                AppendPolygonSample(spline, curT, polygon);
            }

            return polygon;
        }

        private void AppendPolygonSample(Spline spline, float t, List<Vector2> polygon)
        {
            SplineUtility.Evaluate<Spline>(spline, t, out float3 posLocal, out _, out _);
            Vector3 world = loopSpline.transform.TransformPoint((Vector3)posLocal);
            polygon.Add(new Vector2(world.x, world.z));
        }

        private Vector3 CellToWorld(Vector2Int cell, float cellSize)
        {
            return new Vector3(cell.x * cellSize, groundY, cell.y * cellSize);
        }

        private void Generate()
        {
            if (!CanGenerate(out string error))
            {
                Debug.LogError($"[VoronoiSubRoads] {error}");
                return;
            }

            NetworkResult net = BuildNetwork();
            if (!net.Success)
            {
                Debug.LogError($"[VoronoiSubRoads] {net.Error}");
                return;
            }
            if (net.Cells.Count == 0)
            {
                Debug.LogWarning("[VoronoiSubRoads] Network is empty - nothing to generate.");
                return;
            }

            Dictionary<Vector2Int, RoadGridPlacer.Direction> graph = RoadGridPlacer.BuildGraph(net.Cells);
            Dictionary<RoadGridPlacer.ShapeCategory, RoadGridPlacer.RoadShapeConfig> configByCategory =
                RoadGridPlacer.BuildConfigMap(deadEnd, straight, bend, tJunction, fourWay);

            float cellSize = net.CellSize;
            RoadGridPlacer.PlaceResult result = RoadGridPlacer.PlaceCells(
                net.Cells, graph, cell => CellToWorld(cell, cellSize), configByCategory,
                parentName, parentUnder, UndoLabel, "VoronoiSubRoads");

            Debug.Log($"[VoronoiSubRoads] Generated {result.TotalCells - result.SkippedCount} tile(s) " +
                       $"(dead-end {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.DeadEnd]}, straight {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.Straight]}, " +
                       $"bend {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.Bend]}, T-junction {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.TJunction]}, " +
                       $"4-way {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.FourWay]}, skipped {result.SkippedCount}) " +
                       $"inside loop '{loopSpline.name}' ({net.AcceptedSites.Count} sites, seed {randomSeed}, cell size {cellSize:F3} m).");
        }

        private void Clear()
        {
            RoadGridPlacer.ClearGenerated(parentName, parentUnder, "VoronoiSubRoads");
        }
    }
}
