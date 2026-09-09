using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Editor window that generates an interior street network inside a closed-loop main road
    /// by recursively splitting the loop's bounding rectangle with axis-aligned cuts (Binary
    /// Space Partitioning) and rasterizing those cut lines onto the grid, placed by the same
    /// connectivity "grammar" as RoadGeneratorWindow, TilemapRoadGeneratorWindow and
    /// VoronoiSubRoadWindow. This is the rectilinear alternative to the Voronoi sub-road tool:
    /// same job, same prefab kit, same main-road connection mechanic, but every street runs
    /// exactly along world X or Z, so nothing staircases. The split algorithm lives in
    /// BspGeometry; the spline-sampling/rasterization/connection plumbing is
    /// SubRoadNetworkShared (shared with the Voronoi tool); the grammar/placement engine is
    /// RoadGridPlacer (shared with all of them). See Docs/BspSubRoadGenerator.md.
    /// </summary>
    public class BspSubRoadWindow : EditorWindow
    {
        private const string UndoLabel = "Generate Sub-Roads (BSP)";
        private const string LogPrefix = "BspSubRoads";

        private struct NetworkResult
        {
            public bool Success;
            public string Error;
            public List<Vector2> Polygon;
            public float PolygonLength;
            public List<Vector2Int> Cells;
            public float CellSize;
            /// <summary>The raw BSP cut lines, before interior clipping/rasterization.</summary>
            public List<(Vector2 a, Vector2 b)> Segments;
            public int SplitSegmentCount;
            public int DroppedMainRoadOverlapCount;
            public int StraightenedRunCount;
            public int StraightenedBlockedCount;

            // Main-road connection data - see SubRoadNetworkShared.FindMainRoadConnections.
            public HashSet<Vector2Int> MainRoadCells;
            public GameObject MainRoadRoot;
            public int CandidateConnectionCount;
            public Dictionary<Vector2Int, RoadGridPlacer.Direction> SubConnectionExtra;
            public Dictionary<Vector2Int, RoadGridPlacer.Direction> RoadConnectionExtra;
            public Dictionary<Vector2Int, GameObject> RoadConnectionTarget;
        }

        private static readonly Dictionary<RoadGridPlacer.ShapeCategory, RoadGridPlacer.Direction> OpenAtZeroByCategory =
            RoadGridPlacer.BuildOpenAtZeroByCategory();

        private static readonly Dictionary<RoadGridPlacer.ShapeCategory, Color> CategoryColors =
            new Dictionary<RoadGridPlacer.ShapeCategory, Color>
            {
                { RoadGridPlacer.ShapeCategory.DeadEnd, Color.red },
                { RoadGridPlacer.ShapeCategory.Straight, new Color(0.85f, 0.85f, 0.85f) },
                { RoadGridPlacer.ShapeCategory.Bend, Color.yellow },
                { RoadGridPlacer.ShapeCategory.TJunction, new Color(1f, 0.55f, 0f) },
                { RoadGridPlacer.ShapeCategory.FourWay, Color.cyan },
            };

        private static readonly Color UnclassifiedCellColor = Color.magenta;

        [SerializeField] private SplineContainer loopSpline;
        [SerializeField] private int splineIndex;

        [SerializeField] private int maxSplitDepth = 5;
        [SerializeField] private float minRegionSize = 3f;
        [SerializeField] private float splitJitter = 0.15f;
        [SerializeField] private float splitAxisBias = 0.75f;
        [SerializeField] private float stopProbability = 0.15f;
        [SerializeField] private int randomSeed = 12345;
        [SerializeField] private float boundaryMargin = 2f;
        [SerializeField] private float polygonSampleStep = 0.5f;
        [SerializeField] private float edgeSampleStep = 0.5f;

        [SerializeField] private float cellSizeOverride;
        [SerializeField] private float groundY;

        [SerializeField] private bool simplifyStaircases;
        [SerializeField] private int straightenToleranceCells = 1;

        [SerializeField] private RoadGridPlacer.RoadShapeConfig deadEnd = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig straight = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig bend = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig tJunction = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig fourWay = new RoadGridPlacer.RoadShapeConfig();

        [SerializeField] private string parentName = "SubRoads";
        [SerializeField] private Transform parentUnder;

        [SerializeField] private string mainRoadParentName = "Road";
        [SerializeField] private Transform mainRoadParentUnder;

        [SerializeField] private bool showRawSplitLines;
        [SerializeField] private bool previewVisible = true;

        private bool previewLocked;
        private NetworkResult lockedNetwork;
        private Vector2 scrollPosition;

        [MenuItem("Tools/OpenCity/Sub-Road Generator (BSP)")]
        private static void Open()
        {
            BspSubRoadWindow window = GetWindow<BspSubRoadWindow>("Sub-Road Generator (BSP)");
            // Low enough to dock in a short panel - the field list scrolls (see OnGUI), so the
            // minimum no longer has to fit every field at once.
            window.minSize = new Vector2(360, 320);
        }

        private void OnEnable()
        {
            RoadGridPlacer.LoadDefaults(deadEnd, straight, bend, tJunction, fourWay);
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        private void OnDestroy()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        private void OnGUI()
        {
            // Begin/End rather than a ScrollViewScope: the scrolled region is everything except
            // the Generate/Clear row below, which stays pinned to the bottom of the window so it
            // is reachable without scrolling no matter how short the window is docked.
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

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
            EditorGUILayout.LabelField("BSP", EditorStyles.boldLabel);
            maxSplitDepth = Mathf.Max(1, EditorGUILayout.IntField(new GUIContent(
                "Max Split Depth",
                "How many times a region may be recursively split. Higher = denser network of " +
                "smaller blocks (up to 2^depth of them, before Min Region Size and Stop Probability " +
                "cut it back)."), maxSplitDepth));
            minRegionSize = Mathf.Max(0.01f, EditorGUILayout.FloatField(new GUIContent(
                "Min Region Size",
                "Smallest world-space width/height a block may be split down to - a region is only " +
                "split if BOTH resulting halves still clear this. Keep it at roughly 2x the cell size " +
                "or more: two cuts landing within one cell of each other rasterize into a 2-wide road " +
                "blob instead of a block boundary."), minRegionSize));
            splitJitter = EditorGUILayout.Slider(new GUIContent(
                "Split Jitter",
                "0 = every cut lands at the exact middle of its region (rigid, evenly-halved blocks). " +
                "Higher values let the cut wander that fraction of the region's extent either side of " +
                "center, for uneven block sizes. Min Region Size always wins over jitter."),
                splitJitter, 0f, 0.5f);
            splitAxisBias = EditorGUILayout.Slider(new GUIContent(
                "Split Axis Bias",
                "Probability of cutting across the region's longer side. 1 = always the longer side " +
                "(blocks stay squarish and regular), 0.5 = coin flip (more varied, more elongated " +
                "blocks). Only axes that can still fit two Min Region Size halves are eligible."),
                splitAxisBias, 0.5f, 1f);
            stopProbability = EditorGUILayout.Slider(new GUIContent(
                "Stop Probability",
                "Chance a splittable region stops early anyway, leaving one big block where its " +
                "neighbors subdivided - the main source of block-size variety. The first (root) split " +
                "is exempt, so this can never leave the network empty."),
                stopProbability, 0f, 0.9f);
            boundaryMargin = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent(
                "Boundary Margin",
                "Minimum distance road cells must stay from the main loop's boundary. Wherever it " +
                "lets a sub-road cell end up touching a main-road cell, that becomes a connection " +
                "point (see Main Road Connection below) - lower this to create more of them."), boundaryMargin));
            using (new EditorGUILayout.HorizontalScope())
            {
                randomSeed = EditorGUILayout.IntField(new GUIContent(
                    "Random Seed",
                    "Same seed + same settings always produce the same network."), randomSeed);
                GUI.enabled = !previewLocked;
                if (GUILayout.Button("Reroll", GUILayout.Width(70)))
                {
                    Reroll();
                }
                GUI.enabled = true;
            }
            polygonSampleStep = Mathf.Max(0.05f, EditorGUILayout.FloatField(new GUIContent(
                "Polygon Sample Step",
                "Arc-length step used to sample the loop spline into a polygon for clipping."), polygonSampleStep));
            edgeSampleStep = Mathf.Max(0.05f, EditorGUILayout.FloatField(new GUIContent(
                "Edge Sample Step",
                "World-space step used to sample each BSP cut line when rasterizing it onto the grid."), edgeSampleStep));

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
            EditorGUILayout.LabelField("Cleanup", EditorStyles.boldLabel);
            simplifyStaircases = EditorGUILayout.ToggleLeft(new GUIContent(
                "Simplify Staircases",
                "Post-rasterization cleanup shared with the Voronoi tool. Rarely does anything here - " +
                "BSP cut lines are already axis-aligned, so there are no staircases to collapse beyond " +
                "the occasional rounding jog. Off by default."),
                simplifyStaircases);
            using (new EditorGUI.DisabledScope(!simplifyStaircases))
            {
                straightenToleranceCells = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent(
                    "Straighten Tolerance (cells)",
                    "How many cells of lateral drift along an otherwise-straight run get snapped flat " +
                    "instead of kept as a real bend. Only matters while Simplify Staircases is on."),
                    straightenToleranceCells));
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Shapes", EditorStyles.boldLabel);
            DrawShapeField("Dead-End Prefab", deadEnd, "Placed at a path endpoint (1 open side). Required.");
            DrawShapeField("Straight Prefab", straight, "Placed where two opposite sides are open. Required.");
            DrawShapeField("Bend Prefab", bend, "Placed where two adjacent sides are open. Required.");
            DrawShapeField("T-Junction Prefab", tJunction,
                "Placed where three sides are open. Optional - if left empty and a T-junction " +
                "cell occurs, that cell is skipped (with a console warning). BSP produces a lot " +
                "of these, since every child cut ends on its parent's cut.");
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
                "existing object with this exact name (see Clear). Defaults to the same 'SubRoads' " +
                "name the Voronoi tool uses, so the two are drop-in alternatives to each other."), parentName);
            parentUnder = (Transform)EditorGUILayout.ObjectField(new GUIContent(
                "Parent Container",
                "Optional. Where the generated root is nested. Leave empty for scene root."), parentUnder, typeof(Transform), true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Main Road Connection", EditorStyles.boldLabel);
            mainRoadParentName = EditorGUILayout.TextField(new GUIContent(
                "Main Road Parent Name",
                "Name of the already-generated main Road GameObject to connect into (matches Road " +
                "Generator's own Parent Name field, default 'Road')."), mainRoadParentName);
            mainRoadParentUnder = (Transform)EditorGUILayout.ObjectField(new GUIContent(
                "Main Road Parent Container",
                "Optional. Where the main Road GameObject is nested. Leave empty for scene root - " +
                "must match whatever Road Generator was set to when Road was generated."), mainRoadParentUnder, typeof(Transform), true);
            EditorGUILayout.HelpBox(
                "Wherever a sub-road cell ends up adjacent to the main loop's own road cells, Generate " +
                "replaces that Road tile with a connector piece (from this tool's Shapes above) and opens " +
                "the sub-road side to match - see the preview below for how many connections that produces. " +
                "Requires the main Road already generated in the scene at this name/container; if it isn't " +
                "found, or a candidate connection has no Road tile nearby, that connection is skipped " +
                "(with a console warning) rather than failing the whole Generate.", MessageType.None);

            EditorGUILayout.Space();
            DrawPreviewControls();
            DrawPreview();

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = previewLocked || CanGenerate(out _);
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

        private void DrawPreviewControls()
        {
            EditorGUILayout.LabelField("Scene Preview", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                showRawSplitLines = EditorGUILayout.ToggleLeft(new GUIContent(
                    "Show Raw Split Lines",
                    "Scene view only: also draws the pre-rasterization BSP cut lines, secondary to the grid cells."),
                    showRawSplitLines, GUILayout.Width(210));
                bool newPreviewVisible = EditorGUILayout.ToggleLeft(new GUIContent(
                    "Show Preview",
                    "Scene view preview gizmos. Automatically turned off after Generate - toggle back on manually."),
                    previewVisible, GUILayout.Width(100));
                if (newPreviewVisible != previewVisible)
                {
                    previewVisible = newPreviewVisible;
                    SceneView.RepaintAll();
                }
                GUILayout.FlexibleSpace();
                if (previewLocked)
                {
                    EditorGUILayout.LabelField("LOCKED", EditorStyles.boldLabel, GUILayout.Width(60));
                }
                if (GUILayout.Button(previewLocked ? "Unlock" : "Lock", GUILayout.Width(70)))
                {
                    ToggleLock();
                }
            }
            EditorGUILayout.HelpBox(
                "Scene view: red = dead-end, gray = straight, yellow = bend, orange = T-junction, cyan = 4-way, " +
                "magenta wire = unclassifiable (skipped), white ring = main-road tile that will be replaced for " +
                "a connection.", MessageType.None);
        }

        private void DrawPreview()
        {
            if (!previewLocked && !CanGenerate(out string error))
            {
                if (!string.IsNullOrEmpty(error))
                {
                    EditorGUILayout.HelpBox(error, MessageType.Warning);
                }
                return;
            }

            NetworkResult net = GetActiveNetwork();
            if (!net.Success)
            {
                EditorGUILayout.HelpBox(net.Error, MessageType.Warning);
                return;
            }

            RoadGridPlacer.CountByCategory(net.Cells, BuildAugmentedSubGraph(net), out int[] counts, out int skipped);

            string message =
                $"Polygon: {net.Polygon.Count} vertices (loop length {net.PolygonLength:F2} m)\n" +
                $"BSP split lines: {net.SplitSegmentCount}\n" +
                $"Cells: {net.Cells.Count}  (dead-end {counts[(int)RoadGridPlacer.ShapeCategory.DeadEnd]}, " +
                $"straight {counts[(int)RoadGridPlacer.ShapeCategory.Straight]}, bend {counts[(int)RoadGridPlacer.ShapeCategory.Bend]}, " +
                $"T-junction {counts[(int)RoadGridPlacer.ShapeCategory.TJunction]}, 4-way {counts[(int)RoadGridPlacer.ShapeCategory.FourWay]})";
            if (skipped > 0)
            {
                message += $"\n{skipped} cell(s) could not be classified and will be skipped.";
            }
            if (net.RoadConnectionExtra.Count > 0)
            {
                message += $"\nMain road connections: {net.SubConnectionExtra.Count} sub-road cell(s) will connect " +
                            $"into {net.RoadConnectionExtra.Count} main-road tile(s).";
            }
            if (net.StraightenedRunCount > 0)
            {
                message += $"\n{net.StraightenedRunCount} staircase run(s) simplified.";
            }
            EditorGUILayout.HelpBox(message, MessageType.None);

            if (net.StraightenedBlockedCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{net.StraightenedBlockedCount} run(s) were left as rasterized - simplifying them " +
                    "would have crossed Boundary Margin, the main road, or another run.", MessageType.Info);
            }

            if (net.SplitSegmentCount == 0)
            {
                EditorGUILayout.HelpBox(
                    "No splits were produced - Min Region Size is too large for this loop's bounding box " +
                    "(or Max Split Depth is too low). Lower Min Region Size to subdivide further.",
                    MessageType.Warning);
            }
            else if (net.Cells.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No road cells survived clipping - Boundary Margin leaves no interior room inside the loop. " +
                    "Reduce Boundary Margin.", MessageType.Warning);
            }
            if (net.DroppedMainRoadOverlapCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{net.DroppedMainRoadOverlapCount} cell(s) exactly coincided with the main loop's own " +
                    "road cells and were dropped to prevent overlap - raise Boundary Margin to avoid this.",
                    MessageType.Warning);
            }
            if (net.CandidateConnectionCount > 0 && net.MainRoadRoot == null)
            {
                EditorGUILayout.HelpBox(
                    $"Main Road object '{mainRoadParentName}' not found - {net.CandidateConnectionCount} candidate " +
                    "connection(s) to the main road can't be applied. Generate the main Road first, or check Main " +
                    "Road Parent Name/Container.", MessageType.Warning);
            }
            else if (net.MainRoadRoot != null && net.CandidateConnectionCount > net.RoadConnectionExtra.Count)
            {
                EditorGUILayout.HelpBox(
                    $"{net.CandidateConnectionCount - net.RoadConnectionExtra.Count} candidate connection(s) had no " +
                    "matching Road tile nearby and won't be applied.", MessageType.Warning);
            }

            if (net.CellSize > 0f && minRegionSize < net.CellSize * 2f)
            {
                EditorGUILayout.HelpBox(
                    $"Min Region Size ({minRegionSize:F2}) is under 2x the cell size ({net.CellSize:F2}) - " +
                    "two cuts can land within a cell of each other and rasterize into a 2-wide road blob " +
                    "instead of a block boundary. Raise Min Region Size.", MessageType.Warning);
            }

            if (loopSpline != null && (loopSpline.transform.rotation != Quaternion.identity ||
                loopSpline.transform.localScale != Vector3.one))
            {
                EditorGUILayout.HelpBox(
                    "Spline Container's transform has non-identity rotation/scale - this desyncs the " +
                    "grid axes from world X/Z. Translation-only is safe; rotating or scaling it is not.",
                    MessageType.Warning);
            }

            if (previewLocked && IsLockedNetworkStale())
            {
                EditorGUILayout.HelpBox(
                    "Locked network no longer matches current settings (loop shape, BSP, or cell-size " +
                    "fields changed). Generate will still use the locked layout - Unlock or Reroll to pick up the change.",
                    MessageType.Info);
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
            if (maxSplitDepth < 1)
            {
                error = "Max Split Depth must be at least 1.";
                return false;
            }
            if (minRegionSize <= 0f)
            {
                error = "Min Region Size must be greater than 0.";
                return false;
            }
            if (boundaryMargin < 0f)
            {
                error = "Boundary Margin must be non-negative.";
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
                Debug.LogWarning($"[{LogPrefix}] Nothing selected.");
                return;
            }
            SplineContainer sc = go.GetComponent<SplineContainer>();
            if (sc == null)
            {
                Debug.LogWarning($"[{LogPrefix}] '{go.name}' has no SplineContainer component.");
                return;
            }
            loopSpline = sc;
            splineIndex = 0;
            Repaint();
        }

        private NetworkResult GetActiveNetwork()
        {
            return previewLocked ? lockedNetwork : BuildNetwork();
        }

        /// <summary>
        /// See SubRoadNetworkShared.BuildAugmentedGraph - kept as a thin unpacking wrapper so
        /// the three places that need it (stats box, Scene gizmos, Generate) stay readable.
        /// </summary>
        private static Dictionary<Vector2Int, RoadGridPlacer.Direction> BuildAugmentedSubGraph(NetworkResult net)
        {
            return SubRoadNetworkShared.BuildAugmentedGraph(net.Cells, net.SubConnectionExtra);
        }

        private void Reroll()
        {
            randomSeed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            Repaint();
            SceneView.RepaintAll();
        }

        private void ToggleLock()
        {
            if (previewLocked)
            {
                previewLocked = false;
                lockedNetwork = default;
            }
            else
            {
                NetworkResult net = BuildNetwork();
                if (!net.Success)
                {
                    Debug.LogWarning($"[{LogPrefix}] Cannot lock - {net.Error}");
                    return;
                }
                lockedNetwork = net;
                previewLocked = true;
            }
            Repaint();
            SceneView.RepaintAll();
        }

        /// <summary>
        /// Compares cell contents (not just counts) against a fresh build, since rerolling the
        /// seed typically keeps the same cell count while changing every coordinate - a
        /// count-only check would miss exactly that case. Cell size is checked separately to
        /// catch a prefab swap changing the resolved spacing while locked.
        /// </summary>
        private bool IsLockedNetworkStale()
        {
            if (!previewLocked)
            {
                return false;
            }
            NetworkResult fresh = BuildNetwork();
            if (!fresh.Success)
            {
                return true;
            }
            if (!Mathf.Approximately(fresh.CellSize, lockedNetwork.CellSize))
            {
                return true;
            }
            if (fresh.Cells.Count != lockedNetwork.Cells.Count)
            {
                return true;
            }
            for (int i = 0; i < fresh.Cells.Count; i++)
            {
                if (fresh.Cells[i] != lockedNetwork.Cells[i])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Runs the whole pipeline (polygon sampling -&gt; bounding rect -&gt; recursive BSP
        /// splitting -&gt; interior clipping -&gt; grid rasterization) purely from the current
        /// serialized field values. This is the single code path both DrawPreview and Generate
        /// call - there is no second, separately-maintained implementation - which is what
        /// actually guarantees the preview and the real output can never drift apart.
        /// </summary>
        private NetworkResult BuildNetwork()
        {
            var result = new NetworkResult();

            if (!CanGenerate(out string error))
            {
                result.Success = false;
                result.Error = error;
                return result;
            }

            Spline spline = loopSpline.Splines[splineIndex];
            float polyStep = Mathf.Max(0.05f, polygonSampleStep);
            List<Vector2> polygon = SubRoadNetworkShared.SamplePolygon(spline, polyStep, loopSpline.transform);
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
            result.Success = true;

            // Hard safety net, independent of Boundary Margin: rasterize the loop itself onto
            // the exact same grid the main Road Generator would use, and later exclude any
            // sub-road cell that lands on one of these cells outright. Same rationale as the
            // Voronoi tool's - see SubRoadNetworkShared.RasterizeLoopCells.
            HashSet<Vector2Int> mainRoadCells = SubRoadNetworkShared.RasterizeLoopCells(polygon, cellSize);

            // BSP partitions the loop's bounding rectangle, not the loop's actual shape - the
            // parts of a cut falling outside the real polygon are discarded by the interior
            // clipping below, exactly as an out-of-bounds Voronoi edge would be.
            Vector2 min = polygon[0];
            Vector2 max = polygon[0];
            for (int i = 1; i < polygon.Count; i++)
            {
                min = Vector2.Min(min, polygon[i]);
                max = Vector2.Max(max, polygon[i]);
            }
            Rect bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);

            List<(Vector2 a, Vector2 b)> splitSegments = BspGeometry.BuildSplits(
                bounds, randomSeed, maxSplitDepth, minRegionSize, splitJitter, splitAxisBias, stopProbability);
            result.Segments = splitSegments;
            result.SplitSegmentCount = splitSegments.Count;

            float edgeStep = Mathf.Max(0.05f, edgeSampleStep);
            var allCells = new List<Vector2Int>();
            foreach ((Vector2 a, Vector2 b) in splitSegments)
            {
                SubRoadNetworkShared.RasterizeInteriorSegment(a, b, polygon, cellSize, edgeStep, boundaryMargin, allCells);
            }

            var distinctCells = new HashSet<Vector2Int>(allCells);
            result.DroppedMainRoadOverlapCount = distinctCells.RemoveWhere(mainRoadCells.Contains);
            var sortedCells = new List<Vector2Int>(distinctCells);
            sortedCells.Sort((p, q) => p.y != q.y ? p.y.CompareTo(q.y) : p.x.CompareTo(q.x));

            // Same optional cleanup pass the Voronoi tool uses, and for the same reasons - though
            // it rarely finds anything here, since every BSP cut is already axis-aligned. Runs
            // strictly before FindMainRoadConnections below, which must see the final cell set.
            if (simplifyStaircases)
            {
                bool IsCellAllowed(Vector2Int cell)
                {
                    var world = new Vector2(cell.x * cellSize, cell.y * cellSize);
                    return VoronoiGeometry.PointInPolygon(world, polygon) &&
                           VoronoiGeometry.DistanceToPolygonBoundary(world, polygon) >= boundaryMargin;
                }
                sortedCells = RoadGridPlacer.SimplifyStaircases(
                    sortedCells, mainRoadCells, straightenToleranceCells, IsCellAllowed,
                    out int straightened, out int blocked);
                sortedCells.Sort((p, q) => p.y != q.y ? p.y.CompareTo(q.y) : p.x.CompareTo(q.x));
                result.StraightenedRunCount = straightened;
                result.StraightenedBlockedCount = blocked;
            }

            result.Cells = sortedCells;
            result.MainRoadCells = mainRoadCells;

            SubRoadNetworkShared.MainRoadConnectionResult connections = SubRoadNetworkShared.FindMainRoadConnections(
                sortedCells, mainRoadCells, cellSize, mainRoadParentName, mainRoadParentUnder,
                cell => CellToWorld(cell, cellSize));
            result.MainRoadRoot = connections.MainRoadRoot;
            result.CandidateConnectionCount = connections.CandidateConnectionCount;
            result.SubConnectionExtra = connections.SubConnectionExtra;
            result.RoadConnectionExtra = connections.RoadConnectionExtra;
            result.RoadConnectionTarget = connections.RoadConnectionTarget;

            return result;
        }

        private Vector3 CellToWorld(Vector2Int cell, float cellSize)
        {
            return new Vector3(cell.x * cellSize, groundY, cell.y * cellSize);
        }

        private Vector3 XZToWorld(Vector2 xz)
        {
            return new Vector3(xz.x, groundY, xz.y);
        }

        private void Generate()
        {
            if (!previewLocked && !CanGenerate(out string error))
            {
                Debug.LogError($"[{LogPrefix}] {error}");
                return;
            }

            NetworkResult net = GetActiveNetwork();
            if (!net.Success)
            {
                Debug.LogError($"[{LogPrefix}] {net.Error}");
                return;
            }
            if (net.Cells.Count == 0)
            {
                Debug.LogWarning($"[{LogPrefix}] Network is empty - nothing to generate.");
                return;
            }

            if (net.CandidateConnectionCount > 0 && net.MainRoadRoot == null)
            {
                Debug.LogWarning($"[{LogPrefix}] Main Road object '{mainRoadParentName}' not found - " +
                    $"{net.CandidateConnectionCount} candidate connection(s) to the main road will be skipped. " +
                    "Generate the main Road first, or check Main Road Parent Name/Container.");
            }
            int skippedConnections = net.CandidateConnectionCount - net.RoadConnectionExtra.Count;
            if (net.MainRoadRoot != null && skippedConnections > 0)
            {
                Debug.LogWarning($"[{LogPrefix}] {skippedConnections} candidate connection(s) had no matching " +
                    "Road tile nearby and were skipped.");
            }

            Dictionary<Vector2Int, RoadGridPlacer.Direction> graph = BuildAugmentedSubGraph(net);
            Dictionary<RoadGridPlacer.ShapeCategory, RoadGridPlacer.RoadShapeConfig> configByCategory =
                RoadGridPlacer.BuildConfigMap(deadEnd, straight, bend, tJunction, fourWay);

            float cellSize = net.CellSize;

            Undo.IncrementCurrentGroup();
            int outerUndoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoLabel);

            RoadGridPlacer.PlaceResult result = RoadGridPlacer.PlaceCells(
                net.Cells, graph, cell => CellToWorld(cell, cellSize), configByCategory,
                parentName, parentUnder, UndoLabel, LogPrefix);

            int roadTilesReplaced = SubRoadNetworkShared.ReplaceMainRoadConnectionTiles(
                net.MainRoadCells, net.RoadConnectionExtra, net.RoadConnectionTarget, net.MainRoadRoot,
                configByCategory, cell => CellToWorld(cell, cellSize), UndoLabel, LogPrefix);

            Undo.CollapseUndoOperations(outerUndoGroup);

            Debug.Log($"[{LogPrefix}] Generated {result.TotalCells - result.SkippedCount} tile(s) " +
                       $"(dead-end {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.DeadEnd]}, straight {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.Straight]}, " +
                       $"bend {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.Bend]}, T-junction {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.TJunction]}, " +
                       $"4-way {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.FourWay]}, skipped {result.SkippedCount}) " +
                       $"inside loop '{(loopSpline != null ? loopSpline.name : "<locked>")}' " +
                       $"({net.SplitSegmentCount} split line(s), seed {randomSeed}, cell size {cellSize:F3} m, " +
                       $"{roadTilesReplaced} main-road tile(s) replaced for connections).");

            previewVisible = false;
            SceneView.RepaintAll();
        }

        private void Clear()
        {
            RoadGridPlacer.ClearGenerated(parentName, parentUnder, LogPrefix);
        }

        // ---------------------------------------------------------------
        // Scene view preview
        // ---------------------------------------------------------------

        private void OnSceneGUI(SceneView sceneView)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }
            if (!previewVisible)
            {
                return;
            }
            NetworkResult net = GetActiveNetwork();
            if (!net.Success)
            {
                return;
            }
            DrawNetworkGizmos(net);
        }

        private void DrawNetworkGizmos(NetworkResult net)
        {
            if (net.Polygon != null && net.Polygon.Count >= 2)
            {
                Handles.color = Color.white;
                var polyline = new Vector3[net.Polygon.Count];
                for (int i = 0; i < net.Polygon.Count; i++)
                {
                    polyline[i] = XZToWorld(net.Polygon[i]);
                }
                Handles.DrawAAPolyLine(2f, polyline);
                Handles.DrawLine(polyline[polyline.Length - 1], polyline[0]);
            }

            if (showRawSplitLines && net.Segments != null)
            {
                Handles.color = new Color(0.3f, 0.6f, 1f, 0.35f);
                foreach ((Vector2 a, Vector2 b) in net.Segments)
                {
                    Handles.DrawLine(XZToWorld(a), XZToWorld(b));
                }
            }

            if (net.RoadConnectionExtra != null && net.RoadConnectionExtra.Count > 0)
            {
                Handles.color = Color.white;
                foreach (Vector2Int roadCell in net.RoadConnectionExtra.Keys)
                {
                    Vector3 center = CellToWorld(roadCell, net.CellSize);
                    Handles.DrawWireDisc(center, Vector3.up, net.CellSize * 0.35f);
                }
            }

            if (net.Cells == null || net.Cells.Count == 0)
            {
                return;
            }

            Dictionary<Vector2Int, RoadGridPlacer.Direction> graph = BuildAugmentedSubGraph(net);
            foreach (Vector2Int cell in net.Cells)
            {
                RoadGridPlacer.Direction mask = graph[cell];
                Vector3 center = CellToWorld(cell, net.CellSize);

                if (!RoadGridPlacer.TryClassify(mask, out RoadGridPlacer.ShapeCategory category))
                {
                    Handles.color = UnclassifiedCellColor;
                    Handles.DrawWireCube(center, new Vector3(net.CellSize, 0.1f, net.CellSize));
                    continue;
                }

                RoadGridPlacer.TryFindRotationSteps(OpenAtZeroByCategory[category], mask, out int steps);
                Quaternion rotation = Quaternion.Euler(0f, steps * 90f, 0f);

                Handles.color = CategoryColors[category];
                Vector3[] rect =
                {
                    center + rotation * new Vector3(-net.CellSize * 0.5f, 0f, -net.CellSize * 0.5f),
                    center + rotation * new Vector3(net.CellSize * 0.5f, 0f, -net.CellSize * 0.5f),
                    center + rotation * new Vector3(net.CellSize * 0.5f, 0f, net.CellSize * 0.5f),
                    center + rotation * new Vector3(-net.CellSize * 0.5f, 0f, net.CellSize * 0.5f),
                };
                Handles.DrawSolidRectangleWithOutline(rect, new Color(Handles.color.r, Handles.color.g, Handles.color.b, 0.35f), Handles.color);
                Handles.DrawLine(center, center + rotation * new Vector3(0f, 0f, net.CellSize * 0.4f));
            }
        }
    }
}
