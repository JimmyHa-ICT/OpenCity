using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Editor window that generates an interior street network inside a closed-loop main
    /// road by scattering Voronoi sites and rasterizing the Voronoi diagram's edges onto
    /// the grid, placed by the same connectivity "grammar" as RoadGeneratorWindow and
    /// TilemapRoadGeneratorWindow. Sub-roads are kept inset from the loop by Boundary
    /// Margin, but wherever that margin lets a sub-road cell end up touching the main
    /// loop's own road cells, Generate wires the two together: the sub-road side opens
    /// toward the connection and the corresponding main-road tile (found in the existing
    /// "Road" object, by position, at Generate time) is replaced with a connector piece.
    /// Everything else about "SubRoads" - its own Clear/Generate, its own parent object -
    /// stays independent of "Road". The geometry (Delaunay triangulation, Voronoi dual,
    /// polygon tests) lives in VoronoiGeometry; the grammar/placement engine is
    /// RoadGridPlacer, shared with the other two tools; the spline-sampling, rasterization
    /// and main-road-connection plumbing lives in SubRoadNetworkShared, shared with the
    /// BSP sub-road tool. See Docs/VoronoiSubRoadGenerator.md.
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
            public List<(Vector2 a, Vector2 b)> Segments;
            public Vector2 Centroid;
            public int DroppedMainRoadOverlapCount;
            public int StraightenedRunCount;
            public int StraightenedBlockedCount;

            // Main-road connection data (see "Connecting to the main road" in BuildNetwork).
            public HashSet<Vector2Int> MainRoadCells;
            public GameObject MainRoadRoot;
            public int CandidateConnectionCount;
            /// <summary>Extra open-direction bits to OR into each connecting sub-road cell's own mask.</summary>
            public Dictionary<Vector2Int, RoadGridPlacer.Direction> SubConnectionExtra;
            /// <summary>Extra open-direction bits to OR into each connecting main-road cell's own mask.</summary>
            public Dictionary<Vector2Int, RoadGridPlacer.Direction> RoadConnectionExtra;
            /// <summary>The actual placed Road tile instance to replace at each connecting main-road cell.</summary>
            public Dictionary<Vector2Int, GameObject> RoadConnectionTarget;
        }

        private static readonly Dictionary<RoadGridPlacer.ShapeCategory, RoadGridPlacer.Direction> OpenAtZeroByCategory =
            new Dictionary<RoadGridPlacer.ShapeCategory, RoadGridPlacer.Direction>
            {
                { RoadGridPlacer.ShapeCategory.DeadEnd, RoadGridPlacer.DeadEndOpenAtZero },
                { RoadGridPlacer.ShapeCategory.Straight, RoadGridPlacer.StraightOpenAtZero },
                { RoadGridPlacer.ShapeCategory.Bend, RoadGridPlacer.BendOpenAtZero },
                { RoadGridPlacer.ShapeCategory.TJunction, RoadGridPlacer.TJunctionOpenAtZero },
                { RoadGridPlacer.ShapeCategory.FourWay, RoadGridPlacer.FourWayOpenAtZero },
            };

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

        [SerializeField] private int siteCount = 40;
        [SerializeField] private float minSiteSpacing = 4f;
        [SerializeField] private float siteGridBias;
        [SerializeField] private float siteGridSpacing;
        [SerializeField] private float boundaryMargin = 2f;
        [SerializeField] private int randomSeed = 12345;
        [SerializeField] private float polygonSampleStep = 0.5f;
        [SerializeField] private float edgeSampleStep = 0.5f;

        [SerializeField] private float stretchAngle;
        [SerializeField] private float stretchFactor = 1f;

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

        [SerializeField] private bool showRawVoronoiEdges;
        [SerializeField] private bool previewVisible = true;

        private bool previewLocked;
        private NetworkResult lockedNetwork;
        private Vector2 scrollPosition;

        [MenuItem("Tools/OpenCity/Sub-Road Generator (Voronoi)")]
        private static void Open()
        {
            VoronoiSubRoadWindow window = GetWindow<VoronoiSubRoadWindow>("Sub-Road Generator (Voronoi)");
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
            EditorGUILayout.LabelField("Voronoi", EditorStyles.boldLabel);
            siteCount = Mathf.Max(3, EditorGUILayout.IntField(new GUIContent(
                "Site Count",
                "How many Voronoi sites to scatter inside the loop. More sites make a denser street network."), siteCount));
            minSiteSpacing = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent(
                "Min Site Spacing",
                "Minimum world-space distance between accepted sites - keeps blocks from being tiny."), minSiteSpacing));
            siteGridBias = EditorGUILayout.Slider(new GUIContent(
                "Site Grid Bias",
                "0 = fully random site scattering (default, unchanged). Higher values pull each candidate " +
                "site toward the nearest point on an implicit alignment grid before the usual acceptance " +
                "tests run, biasing Voronoi edges to land closer to horizontal/vertical so they rasterize " +
                "with less staircasing. 1 = sites snap fully onto the grid (blocks become rigid)."),
                siteGridBias, 0f, 1f);
            siteGridSpacing = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent(
                "Site Grid Spacing",
                "Spacing of the implicit alignment grid Site Grid Bias pulls candidates toward. " +
                "0 = auto (uses Min Site Spacing). Only has an effect while Site Grid Bias is above 0."),
                siteGridSpacing));
            boundaryMargin = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent(
                "Boundary Margin",
                "Minimum distance sites and road cells must stay from the main loop's boundary. " +
                "Wherever it lets a sub-road cell end up touching a main-road cell, that becomes a " +
                "connection point (see Main Road Connection below) - lower this to create more of them."), boundaryMargin));
            using (new EditorGUILayout.HorizontalScope())
            {
                randomSeed = EditorGUILayout.IntField(new GUIContent(
                    "Random Seed",
                    "Same seed + same settings always scatters the same sites and produces the same network."), randomSeed);
                GUI.enabled = !previewLocked;
                if (GUILayout.Button("Reroll", GUILayout.Width(70)))
                {
                    Reroll();
                }
                GUI.enabled = true;
            }
            polygonSampleStep = Mathf.Max(0.05f, EditorGUILayout.FloatField(new GUIContent(
                "Polygon Sample Step",
                "Arc-length step used to sample the loop spline into a polygon for site scattering and clipping."), polygonSampleStep));
            edgeSampleStep = Mathf.Max(0.05f, EditorGUILayout.FloatField(new GUIContent(
                "Edge Sample Step",
                "World-space step used to sample each Voronoi edge when rasterizing it onto the grid."), edgeSampleStep));
            stretchAngle = EditorGUILayout.Slider(new GUIContent(
                "Stretch Angle",
                "Direction (degrees) interior blocks are elongated along. No visible effect while Stretch Factor is 1."),
                stretchAngle, 0f, 180f);
            stretchFactor = Mathf.Max(1f, EditorGUILayout.FloatField(new GUIContent(
                "Stretch Factor",
                "1 = normal organic blocks. Higher values elongate blocks along Stretch Angle - e.g. mimicking long " +
                "blocks along a dominant avenue direction. Typical useful range 1-4."), stretchFactor));

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
                "Post-rasterization cleanup: collapses short zigzag jogs between junctions into a " +
                "cleaner straight run or a single corner, without moving any dead-end/junction/main-road " +
                "connection cell. Off by default - existing scenes regenerate identically until enabled."),
                simplifyStaircases);
            using (new EditorGUI.DisabledScope(!simplifyStaircases))
            {
                straightenToleranceCells = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent(
                    "Straighten Tolerance (cells)",
                    "How many cells of lateral drift along an otherwise-straight run get snapped flat " +
                    "instead of kept as a real bend. Higher values straighten more aggressively (can also " +
                    "flatten intentional gentle curves)."), straightenToleranceCells));
            }

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
                "existing object with this exact name (see Clear). This object stays independent " +
                "from the main Road Generator's output; see Main Road Connection below for the " +
                "one place this tool does read/modify the main Road."), parentName);
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
                showRawVoronoiEdges = EditorGUILayout.ToggleLeft(new GUIContent(
                    "Show Raw Voronoi Edges",
                    "Scene view only: also draws the pre-rasterization Voronoi segments, secondary to the grid cells."),
                    showRawVoronoiEdges, GUILayout.Width(210));
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
                "a connection. A pale line through the centroid shows Stretch Angle whenever Stretch Factor is " +
                "above 1.", MessageType.None);
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
                    "Locked network no longer matches current settings (loop shape, Voronoi, or cell-size " +
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
                    Debug.LogWarning($"[VoronoiSubRoads] Cannot lock - {net.Error}");
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

            // Hard safety net, independent of Boundary Margin: rasterize the loop itself onto
            // the exact same grid the main Road Generator would use, and later exclude any
            // sub-road cell that lands on one of these cells outright. Boundary Margin is a
            // continuous-distance heuristic sampled along each Voronoi edge, then rounded to a
            // grid cell - that rounding (plus WalkOrthogonalBridged's unchecked staircase
            // bridging) can still let a cell coincide with the main loop's own footprint when
            // Margin is set tight relative to Cell Size. This guarantees no such overlap ever
            // reaches Generate, regardless of how Margin is tuned.
            HashSet<Vector2Int> mainRoadCells = SubRoadNetworkShared.RasterizeLoopCells(polygon, cellSize);

            List<Vector2> sites = VoronoiGeometry.ScatterSites(
                polygon, siteCount, minSiteSpacing, boundaryMargin, randomSeed, out _,
                gridBias: siteGridBias, gridSpacing: siteGridSpacing);
            result.AcceptedSites = sites;
            result.Success = true;

            Vector2 centroid = VoronoiGeometry.PolygonCentroid(polygon);
            result.Centroid = centroid;

            if (sites.Count < 3)
            {
                result.TriangleCount = 0;
                result.KeptEdgeCount = 0;
                result.Cells = new List<Vector2Int>();
                result.Segments = new List<(Vector2, Vector2)>();
                result.MainRoadCells = mainRoadCells;
                result.SubConnectionExtra = new Dictionary<Vector2Int, RoadGridPlacer.Direction>();
                result.RoadConnectionExtra = new Dictionary<Vector2Int, RoadGridPlacer.Direction>();
                result.RoadConnectionTarget = new Dictionary<Vector2Int, GameObject>();
                return result;
            }

            // Anisotropic stretch: triangulate/dual in a squeezed space (compressed along
            // Stretch Angle, expanded perpendicular to it) so the resulting cells come out
            // elongated along that angle once mapped back - see VoronoiGeometry.WarpAnisotropic.
            // Site scattering above and rasterization below stay in true world space regardless,
            // so Min Site Spacing/Boundary Margin remain real-world distances either way.
            bool warp = !Mathf.Approximately(stretchFactor, 1f);
            List<Vector2> triSites = sites;
            List<Vector2> triPolygon = polygon;
            Vector2 triCentroid = centroid;
            if (warp)
            {
                triSites = new List<Vector2>(sites.Count);
                foreach (Vector2 s in sites)
                {
                    triSites.Add(VoronoiGeometry.WarpAnisotropic(s, centroid, stretchAngle, stretchFactor));
                }
                triPolygon = new List<Vector2>(polygon.Count);
                foreach (Vector2 v in polygon)
                {
                    triPolygon.Add(VoronoiGeometry.WarpAnisotropic(v, centroid, stretchAngle, stretchFactor));
                }
                // Warp is pivot-relative (doesn't re-add pivot), so the warped polygon's centroid
                // is a different coordinate space from world `centroid` - recompute, don't reuse.
                triCentroid = VoronoiGeometry.PolygonCentroid(triPolygon);
            }

            List<VoronoiGeometry.Triangle> triangles = VoronoiGeometry.Triangulate(triSites);
            result.TriangleCount = triangles.Count;

            Dictionary<VoronoiGeometry.EdgeKey, List<int>> edgeMap = VoronoiGeometry.BuildEdgeToTriangleMap(triangles);
            float boundingRadius = VoronoiGeometry.PolygonBoundingRadius(triPolygon, triCentroid);

            VoronoiGeometry.BuildVoronoiSegments(
                triangles, edgeMap, triCentroid, boundingRadius, RunawayMultiplier,
                out List<(Vector2 a, Vector2 b)> rawSegments, out int droppedHull, out int droppedRunaway);
            result.DroppedHullEdgeCount = droppedHull;
            result.DroppedRunawayEdgeCount = droppedRunaway;

            List<(Vector2 a, Vector2 b)> segments = rawSegments;
            if (warp)
            {
                segments = new List<(Vector2, Vector2)>(rawSegments.Count);
                foreach ((Vector2 a, Vector2 b) in rawSegments)
                {
                    segments.Add((
                        VoronoiGeometry.UnwarpAnisotropic(a, centroid, stretchAngle, stretchFactor),
                        VoronoiGeometry.UnwarpAnisotropic(b, centroid, stretchAngle, stretchFactor)));
                }
            }
            result.KeptEdgeCount = segments.Count;
            result.Segments = segments;

            float edgeStep = Mathf.Max(0.05f, edgeSampleStep);
            var allCells = new List<Vector2Int>();
            foreach ((Vector2 a, Vector2 b) in segments)
            {
                SubRoadNetworkShared.RasterizeInteriorSegment(a, b, polygon, cellSize, edgeStep, boundaryMargin, allCells);
            }

            var distinctCells = new HashSet<Vector2Int>(allCells);
            result.DroppedMainRoadOverlapCount = distinctCells.RemoveWhere(mainRoadCells.Contains);
            var sortedCells = new List<Vector2Int>(distinctCells);
            sortedCells.Sort((p, q) => p.y != q.y ? p.y.CompareTo(q.y) : p.x.CompareTo(q.x));

            // Post-rasterization cleanup, strictly before FindMainRoadConnections below: it
            // must see the final, cleaned-up cell set (not the raw staircase), and must never
            // itself move a cell FindMainRoadConnections would otherwise have wired into the
            // main road - see RoadGridPlacer.SimplifyStaircases' own anchor rule for how that's
            // guaranteed. Off by default (Simplify Staircases), so an existing scene keeps
            // regenerating byte-for-byte identical until the user opts in.
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
                Debug.LogError($"[VoronoiSubRoads] {error}");
                return;
            }

            NetworkResult net = GetActiveNetwork();
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

            if (net.CandidateConnectionCount > 0 && net.MainRoadRoot == null)
            {
                Debug.LogWarning($"[VoronoiSubRoads] Main Road object '{mainRoadParentName}' not found - " +
                    $"{net.CandidateConnectionCount} candidate connection(s) to the main road will be skipped. " +
                    "Generate the main Road first, or check Main Road Parent Name/Container.");
            }
            int skippedConnections = net.CandidateConnectionCount - net.RoadConnectionExtra.Count;
            if (net.MainRoadRoot != null && skippedConnections > 0)
            {
                Debug.LogWarning($"[VoronoiSubRoads] {skippedConnections} candidate connection(s) had no matching " +
                    "Road tile nearby and were skipped.");
            }

            // Merging each applied connection's extra open-direction bit into the self-only graph
            // (via the same BuildAugmentedSubGraph the preview uses) is the only change needed for
            // connecting cells to classify/rotate as a bend/T-junction/4-way that opens toward the
            // main road, via the same generic solver every other cell already goes through.
            Dictionary<Vector2Int, RoadGridPlacer.Direction> graph = BuildAugmentedSubGraph(net);
            Dictionary<RoadGridPlacer.ShapeCategory, RoadGridPlacer.RoadShapeConfig> configByCategory =
                RoadGridPlacer.BuildConfigMap(deadEnd, straight, bend, tJunction, fourWay);

            float cellSize = net.CellSize;

            Undo.IncrementCurrentGroup();
            int outerUndoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoLabel);

            RoadGridPlacer.PlaceResult result = RoadGridPlacer.PlaceCells(
                net.Cells, graph, cell => CellToWorld(cell, cellSize), configByCategory,
                parentName, parentUnder, UndoLabel, "VoronoiSubRoads");

            int roadTilesReplaced = SubRoadNetworkShared.ReplaceMainRoadConnectionTiles(
                net.MainRoadCells, net.RoadConnectionExtra, net.RoadConnectionTarget, net.MainRoadRoot,
                configByCategory, cell => CellToWorld(cell, cellSize), UndoLabel, "VoronoiSubRoads");

            Undo.CollapseUndoOperations(outerUndoGroup);

            Debug.Log($"[VoronoiSubRoads] Generated {result.TotalCells - result.SkippedCount} tile(s) " +
                       $"(dead-end {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.DeadEnd]}, straight {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.Straight]}, " +
                       $"bend {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.Bend]}, T-junction {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.TJunction]}, " +
                       $"4-way {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.FourWay]}, skipped {result.SkippedCount}) " +
                       $"inside loop '{(loopSpline != null ? loopSpline.name : "<locked>")}' " +
                       $"({net.AcceptedSites.Count} sites, seed {randomSeed}, cell size {cellSize:F3} m, " +
                       $"{roadTilesReplaced} main-road tile(s) replaced for connections).");

            previewVisible = false;
            SceneView.RepaintAll();
        }

        private void Clear()
        {
            RoadGridPlacer.ClearGenerated(parentName, parentUnder, "VoronoiSubRoads");
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

                if (stretchFactor > 1.001f)
                {
                    float radius = VoronoiGeometry.PolygonBoundingRadius(net.Polygon, net.Centroid);
                    float rad = stretchAngle * Mathf.Deg2Rad;
                    var axis = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * radius;
                    Handles.color = new Color(0.6f, 0.9f, 1f);
                    Handles.DrawLine(XZToWorld(net.Centroid - axis), XZToWorld(net.Centroid + axis));
                }
            }

            if (net.AcceptedSites != null)
            {
                Handles.color = Color.green;
                foreach (Vector2 site in net.AcceptedSites)
                {
                    Vector3 pos = XZToWorld(site);
                    float size = HandleUtility.GetHandleSize(pos) * 0.1f;
                    Handles.SphereHandleCap(0, pos, Quaternion.identity, size, EventType.Repaint);
                }
            }

            if (showRawVoronoiEdges && net.Segments != null)
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
