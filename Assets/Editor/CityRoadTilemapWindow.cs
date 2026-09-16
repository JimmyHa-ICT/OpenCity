using System.Collections.Generic;
using OpenCity.Tiles;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Editor window that lays out the road network of a square city with Binary Space
    /// Partitioning and paints it straight onto a Tilemap as road tiles. With the project's
    /// road RuleTile (Assets/Tiles/New Rule Tile.asset, an OpenCity.Tiles.RoadRuleTile) the
    /// painted cells turn into the right road prefab + rotation on their own; with any plain
    /// placeholder Tile the result can instead be fed to TilemapRoadGeneratorWindow. For
    /// variety, some crossroads can be replaced by the 3x3 road-roundabout piece, which this
    /// window places itself (a RuleTile only fills single cells), painting a RoadCoverTile on
    /// the road cells underneath. The layout algorithm lives in CityBspLayout and the
    /// roundabout choice in RoundaboutPlanner; this class is UI, preview and Tilemap I/O only.
    /// On top of the street grid it can lay an elevated highway network that links the city's far
    /// apart BSP clusters - planned by HighwayPlanner and painted by HighwayBuilder as a highway
    /// RuleTile on a second Tilemap layer, so the highway resolves its own shapes the same way the
    /// streets do without ever mixing with them.
    /// See Docs/CityBspTilemapGenerator.md and Docs/CityHighwayGenerator.md.
    /// </summary>
    public class CityRoadTilemapWindow : EditorWindow
    {
        private const string UndoLabel = "Generate City Roads (BSP Tilemap)";
        private const string ClearUndoLabel = "Clear City Roads (BSP Tilemap)";
        private const string CreateTilemapUndoLabel = "Create City Tilemap";
        private const string LogPrefix = "CityRoadsBsp";
        private const string ClearConfirmOptOutKey = "OpenCity.CityRoadsBsp.ConfirmClear";
        private const string DefaultRoadTilePath = "Assets/Tiles/New Rule Tile.asset";
        private const string DefaultCoverTilePath = "Assets/Tiles/Road Cover Tile.asset";
        private const string DefaultRoundaboutPath = "Assets/Prefabs/Roads/road-roundabout.prefab";
        private const string HighwayTilemapName = "Highways";
        private const int MinSideLength = 3;
        private const int MaxSideLength = 512;
        /// <summary>Above this many road cells the Scene preview skips per-cell squares (blocks and outline still draw).</summary>
        private const int PreviewCellLimit = 10000;

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
        private static readonly Color BlockColor = new Color(0.25f, 0.8f, 0.35f);
        private static readonly Color RoundaboutColor = new Color(0.7f, 0.35f, 1f);
        private static readonly Color DistrictColor = new Color(0.4f, 0.55f, 0.95f);
        private static readonly Color HighwayDeckColor = new Color(0.15f, 0.45f, 1f);
        private static readonly Color HighwayRampColor = new Color(1f, 0.35f, 0.55f);

        [SerializeField] private Tilemap tilemap;
        [SerializeField] private TileBase roadTile;
        [SerializeField] private float newTilemapCellSize = 1f;

        [SerializeField] private int sideLength = 32;
        [SerializeField] private Vector2Int originCell;
        [SerializeField] private bool perimeterRoad = true;

        [SerializeField] private int maxSplitDepth = 6;
        [SerializeField] private int minBlockSize = 3;
        [SerializeField] private int maxBlockSize = 10;
        [SerializeField] private float splitJitter = 0.2f;
        [SerializeField] private float splitAxisBias = 0.75f;
        [SerializeField] private float stopProbability = 0.2f;
        [SerializeField] private int randomSeed = 12345;

        [SerializeField] private bool useRoundabouts = true;
        [SerializeField] private float roundaboutMinRoadFraction = 0.5f;
        [SerializeField] private float roundaboutSpacingFraction = 1f / 3f;
        [SerializeField] private GameObject roundaboutPrefab;
        [SerializeField] private TileBase coverTile;

        [SerializeField] private bool useHighways = true;
        [SerializeField] private int highwayClusterDepth = 3;
        [SerializeField] private float highwayMinSpanFraction = 0.75f;
        [SerializeField] private int highwayMaxLinks = 4;
        [SerializeField] private int highwayRampLength = 2;
        [SerializeField] private int highwayTurnCost = 6;
        [SerializeField] private bool highwayFollowStreets = true;
        [SerializeField] private Tilemap highwayTilemap;
        [SerializeField] private TileBase highwayTile;

        [SerializeField] private bool clearCityAreaFirst = true;

        [SerializeField] private bool previewVisible = true;
        [SerializeField] private bool showBlocks = true;
        [SerializeField] private bool showDistricts = true;

        // Layout cache, keyed by the exact settings it was built from - the scene preview asks for
        // the layout on every repaint, and a 512-wide city is too big to rebuild that often.
        private bool hasCachedLayout;
        private CityBspLayout.Settings cachedSettings;
        private CityBspLayout.Layout cachedLayout;
        private Dictionary<Vector2Int, RoadGridPlacer.Direction> cachedGraph;

        // Roundabout cache, keyed by the layout object plus the effective minimum crossing length
        // (0 = roundabouts off).
        private CityBspLayout.Layout cachedRoundaboutLayout;
        private int cachedRoundaboutMinLength = -1;
        private int cachedRoundaboutSpacing = -1;
        private List<RoundaboutPlanner.Placement> cachedRoundabouts;
        private HashSet<Vector2Int> cachedRoundaboutRoadCells;

        // Highway cache, keyed by the layout and roundabout plan it was built on top of (both are
        // stable object references until their own settings change) plus the highway settings.
        private CityBspLayout.Layout cachedHighwayLayout;
        private List<RoundaboutPlanner.Placement> cachedHighwayRoundabouts;
        private HighwayPlanner.Settings cachedHighwaySettings;
        private HighwayPlanner.Plan cachedHighwayPlan;

        // What is already inside the city square, for the Generate/Clear buttons. Counting means
        // reading a block of tiles and scanning the scene for pieces, so it is recomputed only
        // when the scene, a Tilemap or the square itself changes - not on every GUI event.
        private bool contentsDirty = true;
        private Tilemap contentsTilemap;
        private Tilemap contentsHighwayTilemap;
        private BoundsInt contentsArea;
        private CityAreaContents cachedContents;

        private Vector2 scrollPosition;

        /// <summary>Everything this tool owns inside the city square.</summary>
        private readonly struct CityAreaContents
        {
            public readonly int RoadTiles;
            public readonly int HighwayTiles;
            public readonly int Pieces;

            public CityAreaContents(int roadTiles, int highwayTiles, int pieces)
            {
                RoadTiles = roadTiles;
                HighwayTiles = highwayTiles;
                Pieces = pieces;
            }

            public bool IsEmpty => RoadTiles == 0 && HighwayTiles == 0 && Pieces == 0;

            public string Describe()
            {
                var parts = new List<string>();
                if (RoadTiles > 0) parts.Add($"{RoadTiles} street tile(s)");
                if (HighwayTiles > 0) parts.Add($"{HighwayTiles} highway tile(s)");
                if (Pieces > 0) parts.Add($"{Pieces} roundabout/multi-cell piece(s)");
                return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
            }
        }

        [InitializeOnLoadMethod]
        private static void RegisterUndoRefresh()
        {
            Undo.undoRedoEvent += OnUndoRedo;
        }

        /// <summary>
        /// Undo/redo restores a Tilemap's tile data but not the GameObjects its tiles spawned (the
        /// road RuleTile's prefabs): undoing a Clear brings the road tiles back with no road models
        /// on them. Re-running every tile's GetTileData re-syncs the spawned objects. Hooked
        /// statically rather than per window so it still works after the window is closed, and
        /// filtered to this tool's own undo labels so unrelated undos never pay for a refresh.
        /// (The 3x3 roundabouts are ordinary GameObjects, which undo restores by itself.)
        /// </summary>
        private static void OnUndoRedo(in UndoRedoInfo info)
        {
            if (info.undoName != UndoLabel && info.undoName != ClearUndoLabel)
            {
                return;
            }
            foreach (Tilemap tm in FindObjectsByType<Tilemap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                tm.RefreshAllTiles();
            }
        }

        [MenuItem("Tools/OpenCity/City Road Generator (BSP Tilemap)")]
        private static void Open()
        {
            CityRoadTilemapWindow window = GetWindow<CityRoadTilemapWindow>("City Roads (BSP Tilemap)");
            // Low enough to dock in a short panel - the field list scrolls (see OnGUI).
            window.minSize = new Vector2(360, 320);
        }

        private void OnEnable()
        {
            if (roadTile == null)
            {
                roadTile = AssetDatabase.LoadAssetAtPath<TileBase>(DefaultRoadTilePath);
            }
            if (coverTile == null)
            {
                coverTile = AssetDatabase.LoadAssetAtPath<TileBase>(DefaultCoverTilePath);
            }
            if (roundaboutPrefab == null)
            {
                roundaboutPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultRoundaboutPath);
            }
            if (highwayTile == null)
            {
                highwayTile = AssetDatabase.LoadAssetAtPath<TileBase>(HighwayBuilder.TilePath);
            }
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.hierarchyChanged += OnSceneContentsChanged;
            Undo.undoRedoPerformed += OnSceneContentsChanged;
            Tilemap.tilemapTileChanged += OnTilemapTileChanged;
            contentsDirty = true;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.hierarchyChanged -= OnSceneContentsChanged;
            Undo.undoRedoPerformed -= OnSceneContentsChanged;
            Tilemap.tilemapTileChanged -= OnTilemapTileChanged;
        }

        private void OnSceneContentsChanged()
        {
            contentsDirty = true;
            Repaint();
        }

        private void OnTilemapTileChanged(Tilemap changed, Tilemap.SyncTile[] tiles)
        {
            if (tilemap != null && (changed == tilemap || changed == FindHighwayTilemap()))
            {
                OnSceneContentsChanged();
            }
        }

        // ---------------------------------------------------------------
        // GUI
        // ---------------------------------------------------------------

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();

            // Begin/End rather than a ScrollViewScope: the scrolled region is everything except
            // the action bar below, which stays pinned to the bottom of the window.
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            DrawTargetSection();
            DrawCitySection();
            DrawBspSection();
            DrawVarietySection();
            DrawHighwaySection();
            EditorGUILayout.Space();
            DrawPreviewControls();
            DrawPreview();
            EditorGUILayout.EndScrollView();

            if (EditorGUI.EndChangeCheck())
            {
                SceneView.RepaintAll();
            }

            DrawActionBar();
        }

        private void DrawTargetSection()
        {
            EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);
            tilemap = (Tilemap)EditorGUILayout.ObjectField(new GUIContent(
                "Tilemap",
                "The Tilemap the road cells are painted onto. Its Grid should have Cell Swizzle = XZY " +
                "so it lies flat on the world XZ ground plane."), tilemap, typeof(Tilemap), true);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Use Selected GameObject", GUILayout.Width(190)))
                {
                    UseSelection();
                }
                if (GUILayout.Button("Create City Tilemap", GUILayout.Width(150)))
                {
                    CreateCityTilemap();
                }
            }
            newTilemapCellSize = Mathf.Max(0.01f, EditorGUILayout.FloatField(new GUIContent(
                "New Tilemap Cell Size",
                "Cell size used by 'Create City Tilemap' only. Match your road prefab footprint " +
                "(1 for the Kenney road kit in Assets/Prefabs/Roads)."), newTilemapCellSize));
            roadTile = (TileBase)EditorGUILayout.ObjectField(new GUIContent(
                "Road Tile",
                "The tile painted on every road cell. Defaults to Assets/Tiles/New Rule Tile.asset, the " +
                "road RuleTile that spawns the matching road prefab per cell."), roadTile, typeof(TileBase), false);
        }

        private void DrawCitySection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("City", EditorStyles.boldLabel);
            sideLength = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent(
                "Side Length (cells)",
                $"The city is a square of this many cells per side ({MinSideLength}-{MaxSideLength}). " +
                "Multiply by the Grid cell size for meters."), sideLength), MinSideLength, MaxSideLength);
            originCell = EditorGUILayout.Vector2IntField(new GUIContent(
                "Origin Cell",
                "Tilemap cell of the city's south-west (bottom-left) corner. The city covers Origin Cell " +
                "to Origin Cell + Side Length - 1 on both axes."), originCell);
            perimeterRoad = EditorGUILayout.Toggle(new GUIContent(
                "Perimeter Ring Road",
                "On: the city's outermost ring of cells is a road and BSP partitions the interior - no " +
                "dead ends anywhere. Off: BSP partitions the whole square and streets stop at the city " +
                "edge as dead ends (useful when something else will connect to them)."), perimeterRoad);
        }

        private void DrawBspSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("BSP", EditorStyles.boldLabel);
            maxSplitDepth = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent(
                "Max Split Depth",
                "How many times a region may be recursively split before it becomes a block (up to " +
                "2^depth blocks). Max Block Size can force splits beyond this."), maxSplitDepth));
            minBlockSize = Mathf.Max(1, EditorGUILayout.IntField(new GUIContent(
                "Min Block Size (cells)",
                "Smallest block width/height. A region is only split if both halves keep at least " +
                "this many cells - so parallel roads are always at least this far apart."), minBlockSize));
            maxBlockSize = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent(
                "Max Block Size (cells)",
                "0 = off. A region wider or taller than this is split, overriding Max Split Depth and " +
                "Stop Probability. Only reachable when it's at least 2 x Min Block Size, and a region " +
                "stays whole if every cut would put two intersections side by side."), maxBlockSize));
            splitJitter = EditorGUILayout.Slider(new GUIContent(
                "Split Jitter",
                "0 = every cut at the exact middle of its region. Higher values let the cut wander that " +
                "fraction of the region's extent either side of center. Min Block Size always wins."),
                splitJitter, 0f, 0.5f);
            splitAxisBias = EditorGUILayout.Slider(new GUIContent(
                "Split Axis Bias",
                "Probability of cutting across the region's longer side. 1 = always (squarish blocks), " +
                "0.5 = coin flip (more elongated blocks)."), splitAxisBias, 0.5f, 1f);
            stopProbability = EditorGUILayout.Slider(new GUIContent(
                "Stop Probability",
                "Chance a splittable region stops early and stays one big block. The root split is " +
                "exempt, and Max Block Size overrides it."), stopProbability, 0f, 0.9f);
            using (new EditorGUILayout.HorizontalScope())
            {
                randomSeed = EditorGUILayout.IntField(new GUIContent(
                    "Random Seed",
                    "Same seed + same settings always produce the same city."), randomSeed);
                if (GUILayout.Button("Reroll", GUILayout.Width(70)))
                {
                    randomSeed = Random.Range(int.MinValue, int.MaxValue);
                    GUI.changed = true;
                }
            }
        }

        private void DrawVarietySection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Variety", EditorStyles.boldLabel);
            useRoundabouts = EditorGUILayout.Toggle(new GUIContent(
                "Roundabouts",
                "Put the 3x3 roundabout piece on the large intersections: crossroads where both crossing " +
                "roads are at least Min Crossing Road long, biggest first and at least Min Roundabout Spacing " +
                "apart (and where the piece fits). No randomness - the same " +
                "city always gets the same roundabouts."), useRoundabouts);
            using (new EditorGUI.DisabledScope(!useRoundabouts))
            {
                roundaboutMinRoadFraction = EditorGUILayout.Slider(new GUIContent(
                    $"Min Crossing Road ({RoundaboutMinCrossingLength()} cells)",
                    "How long both roads through a crossroad must be for it to count as a large intersection, " +
                    "as a fraction of the city's side length. The length is the unbroken straight road through " +
                    "the crossroad. Higher = fewer roundabouts, only where the main avenues cross."),
                    roundaboutMinRoadFraction, 0.1f, 1f);
                roundaboutSpacingFraction = EditorGUILayout.Slider(new GUIContent(
                    $"Min Roundabout Spacing ({RoundaboutMinSpacing()} cells)",
                    "How far apart roundabouts must be, as a fraction of the city's side length, measured along " +
                    "either axis. The biggest crossings are served first, so this keeps each part of the city to " +
                    "one roundabout at its biggest intersection. Higher = fewer, more spread out."),
                    roundaboutSpacingFraction, 0.1f, 1f);
            }
            roundaboutPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent(
                "Roundabout Prefab",
                "The 3x3 piece, centered on its pivot, with one exit in the middle of each side."),
                roundaboutPrefab, typeof(GameObject), false);
            using (new EditorGUILayout.HorizontalScope())
            {
                coverTile = (TileBase)EditorGUILayout.ObjectField(new GUIContent(
                    "Cover Tile",
                    "Painted on the road cells under each roundabout and at each highway ramp: spawns nothing, but " +
                    "still counts as road for the pieces around it. Must be a Road Cover Tile."),
                    coverTile, typeof(TileBase), false);
                if (coverTile == null && GUILayout.Button("Create", GUILayout.Width(60)))
                {
                    CreateCoverTile();
                }
            }
        }

        private void DrawHighwaySection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Highways", EditorStyles.boldLabel);
            useHighways = EditorGUILayout.Toggle(new GUIContent(
                "Highways",
                "Lay an elevated highway network over the city, linking the BSP clusters that are far " +
                "enough apart to be worth a direct route. It is painted with the Highway Tile on its own " +
                "Tilemap layer and runs either right on top of a street or well clear of one - never " +
                "alongside it - so the street grid underneath is untouched."), useHighways);
            using (new EditorGUI.DisabledScope(!useHighways))
            {
                highwayClusterDepth = Mathf.Clamp(EditorGUILayout.IntSlider(new GUIContent(
                    $"Cluster Depth ({GetHighwayPlan().Districts.Count} clusters)",
                    "How deep into the BSP tree the city is cut into clusters: depth 1 gives the two halves " +
                    "of the first split, depth 3 up to eight districts. A region that stopped splitting earlier " +
                    "counts as one cluster whatever its depth."), highwayClusterDepth, 0, 8), 0, 8);
                highwayMinSpanFraction = EditorGUILayout.Slider(new GUIContent(
                    $"Min Link Span ({HighwayMinSpan()} cells)",
                    "How far apart two clusters' ramps must be, as a fraction of the city's side length and " +
                    "measured in cells travelled (Manhattan), before a highway between them is worth building. " +
                    "This is what makes the network link only the FAR clusters - neighbouring ones already have " +
                    "streets. Higher = fewer, longer highways."), highwayMinSpanFraction, 0.1f, 2f);
                highwayMaxLinks = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent(
                    "Max Links",
                    "Upper bound on how many cluster-to-cluster links are built. The links are taken shortest " +
                    "first, and only ever when they join two parts of the network that weren't connected yet."),
                    highwayMaxLinks));
                highwayRampLength = EditorGUILayout.IntSlider(new GUIContent(
                    "Ramp Length (cells)",
                    "1 = the steep one-cell road-slant-high. 2 = the eased two-cell road-slant-curve, which " +
                    "looks far better but needs two clear block cells beside a street at every cluster."),
                    highwayRampLength, HighwayPlanner.MinRampLength, HighwayPlanner.MaxRampLength);
                highwayTurnCost = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent(
                    "Turn Cost (cells)",
                    "What one change of direction costs the router, expressed in cells of detour it is worth " +
                    "avoiding. High = long straight viaducts that would rather go around; 0 = the shortest " +
                    "path whatever it looks like."), highwayTurnCost));
                highwayFollowStreets = EditorGUILayout.Toggle(new GUIContent(
                    "Follow Streets",
                    "On: a step on top of a street costs the router half a step, so the highway rides above " +
                    "the street grid wherever it can, like an expressway built over an avenue. Off: it takes " +
                    "whichever legal corridor is shortest, over streets or across the blocks."), highwayFollowStreets);
                highwayTilemap = (Tilemap)EditorGUILayout.ObjectField(new GUIContent(
                    "Highway Tilemap",
                    "The separate Tilemap layer the highway is painted on - a sibling of the street Tilemap in " +
                    "the same Grid. Leave empty to have Generate find or create one called 'Highways'."),
                    highwayTilemap, typeof(Tilemap), true);
                using (new EditorGUILayout.HorizontalScope())
                {
                    highwayTile = (TileBase)EditorGUILayout.ObjectField(new GUIContent(
                        "Highway Tile",
                        "The highway RuleTile painted on every deck cell and ramp foot. Its pieces already stand " +
                        "at deck height: road-bridge for straights, a road piece on columns for turns and " +
                        "junctions, and the slant pieces for ramps. Click Create (or Rebuild) to generate it and " +
                        "its pieces from the Highway kit."), highwayTile, typeof(TileBase), false);
                    if (GUILayout.Button(highwayTile == null ? "Create" : "Rebuild", GUILayout.Width(60)))
                    {
                        CreateHighwayTile();
                    }
                }
            }
        }

        /// <summary>
        /// The pinned bottom bar: what Generate will do, why it can't (right here rather than
        /// scrolled away in the preview), the Clear-first option that decides whether Generate
        /// replaces the old city, and the two buttons - each labelled for what it will actually do.
        /// </summary>
        private void DrawActionBar()
        {
            EditorGUILayout.Space(4);
            Rect separator = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(separator, new Color(0f, 0f, 0f, 0.3f));

            bool canGenerate = CanGenerate(out string generateError);
            CityAreaContents contents = GetCityAreaContents();

            if (!canGenerate)
            {
                EditorGUILayout.HelpBox(generateError, MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField(DescribeGeneration(), EditorStyles.wordWrappedMiniLabel);
            }

            using (new EditorGUI.DisabledScope(tilemap == null))
            {
                clearCityAreaFirst = EditorGUILayout.ToggleLeft(new GUIContent(
                    contents.IsEmpty || !clearCityAreaFirst
                        ? "Clear City Area First"
                        : $"Clear City Area First (replaces {contents.Describe()})",
                    "Erase every tile on both Tilemaps, and every roundabout, inside the city square before painting, " +
                    "so regenerating replaces the previous city. Off: roads are painted over whatever is already there."),
                    clearCityAreaFirst);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                bool regenerate = clearCityAreaFirst && !contents.IsEmpty;
                using (new EditorGUI.DisabledScope(!canGenerate))
                {
                    var generateContent = new GUIContent(
                        regenerate ? "Regenerate" : "Generate",
                        canGenerate
                            ? (regenerate
                                ? "Replace the city already in this square with the current layout. One Undo step."
                                : "Paint the current layout into the city square. One Undo step.")
                            : generateError);
                    if (GUILayout.Button(generateContent, GUILayout.Height(30)))
                    {
                        Generate();
                        GUIUtility.ExitGUI();
                    }
                }
                using (new EditorGUI.DisabledScope(tilemap == null || contents.IsEmpty))
                {
                    var clearContent = new GUIContent(
                        contents.IsEmpty ? "Clear City Area" : $"Clear City Area ({Total(contents)})",
                        tilemap == null
                            ? "Assign a Tilemap first."
                            : contents.IsEmpty
                                ? "Nothing to clear - the city square is already empty on both Tilemaps."
                                : $"Erase {contents.Describe()} inside the {sideLength}x{sideLength} square at cell " +
                                  $"{originCell}. One Undo step.");
                    if (GUILayout.Button(clearContent, GUILayout.Height(30), GUILayout.Width(150)))
                    {
                        Clear(contents);
                        GUIUtility.ExitGUI();
                    }
                }
            }
            EditorGUILayout.Space(2);
        }

        private static int Total(CityAreaContents contents)
        {
            return contents.RoadTiles + contents.HighwayTiles + contents.Pieces;
        }

        /// <summary>One line saying what Generate will paint, for the action bar.</summary>
        private string DescribeGeneration()
        {
            CityBspLayout.Layout layout = GetLayout();
            List<RoundaboutPlanner.Placement> roundabouts = GetRoundaboutPlan();
            HighwayPlanner.Plan highways = GetHighwayPlan();
            string text = $"{layout.SideLength}x{layout.SideLength} city at cell {originCell}: " +
                          $"{layout.RoadCells.Count} street cell(s), {roundabouts.Count} roundabout(s)";
            if (useHighways)
            {
                text += highways.Links.Count > 0
                    ? $", {highways.Links.Count} highway link(s) ({highways.DeckCells.Count} deck cells, " +
                      $"{highways.Ramps.Count} ramps)"
                    : ", no highways";
            }
            return text + ".";
        }

        private void DrawPreviewControls()
        {
            EditorGUILayout.LabelField("Scene Preview", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                previewVisible = EditorGUILayout.ToggleLeft(new GUIContent(
                    "Show Preview", "Draw the layout in the Scene view before painting."),
                    previewVisible, GUILayout.Width(120));
                showBlocks = EditorGUILayout.ToggleLeft(new GUIContent(
                    "Show Blocks", "Also outline each city block (the leaf regions between roads)."),
                    showBlocks, GUILayout.Width(120));
                showDistricts = EditorGUILayout.ToggleLeft(new GUIContent(
                    "Show Clusters", "Also outline the BSP clusters the highways link."),
                    showDistricts, GUILayout.Width(120));
            }
            EditorGUILayout.HelpBox(
                "Scene view: white = city boundary, green = blocks, gray = straight, yellow = bend, " +
                "orange = T-junction, cyan = 4-way, red = dead-end, purple = 3x3 roundabout, " +
                "blue outline = cluster, blue = highway deck, pink = ramp.", MessageType.None);
        }

        private void DrawPreview()
        {
            CityBspLayout.Layout layout = GetLayout();
            List<RoundaboutPlanner.Placement> roundabouts = GetRoundaboutPlan();
            RoadGridPlacer.CountByCategory(layout.RoadCells, cachedGraph, out int[] counts, out int skipped);

            int area = layout.SideLength * layout.SideLength;
            float cellSize = tilemap != null && tilemap.layoutGrid != null ? tilemap.layoutGrid.cellSize.x : newTilemapCellSize;
            string message =
                $"City: {layout.SideLength} x {layout.SideLength} cells ({layout.SideLength * cellSize:0.##} m per side), " +
                $"cells {originCell.x}..{originCell.x + layout.SideLength - 1}, {originCell.y}..{originCell.y + layout.SideLength - 1}\n" +
                $"Road cells: {layout.RoadCells.Count} ({100f * layout.RoadCells.Count / Mathf.Max(1, area):0.#}% of the city)" +
                (layout.PerimeterCellCount > 0 ? $", {layout.PerimeterCellCount} of them perimeter ring" : "") + "\n" +
                $"BSP cuts: {layout.Cuts.Count}   Blocks: {layout.Blocks.Count}{DescribeBlockSizes(layout.Blocks)}\n" +
                $"Pieces: dead-end {counts[(int)RoadGridPlacer.ShapeCategory.DeadEnd]}, " +
                $"straight {counts[(int)RoadGridPlacer.ShapeCategory.Straight] - 4 * roundabouts.Count}, " +
                $"bend {counts[(int)RoadGridPlacer.ShapeCategory.Bend]}, " +
                $"T-junction {counts[(int)RoadGridPlacer.ShapeCategory.TJunction]}, " +
                $"4-way {counts[(int)RoadGridPlacer.ShapeCategory.FourWay] - roundabouts.Count}, roundabout (3x3) {roundabouts.Count}" +
                (useRoundabouts
                    ? $" (crossroads where both roads are at least {RoundaboutMinCrossingLength()} cells long, " +
                      $"at least {RoundaboutMinSpacing()} cells apart)"
                    : "");
            if (layout.ForcedSplitCount > 0)
            {
                message += $"\n{layout.ForcedSplitCount} split(s) forced by Max Block Size.";
            }
            if (layout.SpacingStopCount > 0)
            {
                message += $"\n{layout.SpacingStopCount} region(s) left whole: every cut position would have put " +
                           "two intersections side by side.";
            }
            if (skipped > 0)
            {
                message += $"\n{skipped} road cell(s) have no matching road shape.";
            }
            HighwayPlanner.Plan highways = GetHighwayPlan();
            message += $"\nClusters (BSP depth {highwayClusterDepth}): {highways.Districts.Count}";
            if (useHighways)
            {
                message += $"   Highways: {highways.Links.Count} link(s), {highways.DeckCells.Count} deck cell(s) " +
                           $"({highways.BridgeCells} on top of a street), {highways.Ramps.Count} ramp(s)";
            }
            EditorGUILayout.HelpBox(message, MessageType.None);

            if (layout.Cuts.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No interior roads - the city interior is narrower than 2 x Min Block Size + 1 cells, " +
                    "or Max Split Depth is 0. Raise Side Length or lower Min Block Size.", MessageType.Warning);
            }
            if (maxBlockSize > 0 && maxBlockSize < minBlockSize * 2)
            {
                EditorGUILayout.HelpBox(
                    $"Max Block Size ({maxBlockSize}) is under 2 x Min Block Size ({minBlockSize * 2}) - a region " +
                    "just over the maximum can be too small to split, so some blocks may exceed it.", MessageType.Info);
            }
            if (useRoundabouts && !CanPlaceRoundabouts(out string roundaboutProblem))
            {
                EditorGUILayout.HelpBox("Roundabouts are off: " + roundaboutProblem, MessageType.Info);
            }
            if (useHighways && !CanPlaceHighways(out string highwayProblem))
            {
                EditorGUILayout.HelpBox("Highways are off: " + highwayProblem, MessageType.Info);
            }
            else if (useHighways)
            {
                if (highways.Links.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        highways.Districts.Count < 2
                            ? "No highways: the city is one cluster at this Cluster Depth. Raise Cluster Depth."
                            : $"No highways: no two clusters' ramps are {HighwayMinSpan()} cells apart, or none of " +
                              "them could be routed. Lower Min Link Span, or raise Side Length.", MessageType.Info);
                }
                if (highways.DistrictsWithoutHub > 0)
                {
                    EditorGUILayout.HelpBox(
                        $"{highways.DistrictsWithoutHub} cluster(s) have nowhere to put a ramp: no street in them has " +
                        $"{highwayRampLength} free block cell(s) beside it, clear of other roads and junctions. They " +
                        "are reached over the street grid instead. Lower Ramp Length, or raise Min Block Size.",
                        MessageType.Info);
                }
                if (highways.UnroutableLinks > 0)
                {
                    EditorGUILayout.HelpBox(
                        $"{highways.UnroutableLinks} link(s) had no legal corridor and were dropped - every route " +
                        "between those clusters would have had to run alongside a street. Reroll, or lower Turn Cost.",
                        MessageType.Info);
                }
                if (highwayTile != null && !(highwayTile is HighwayRuleTile))
                {
                    EditorGUILayout.HelpBox(
                        "Highway Tile is not a Highway Rule Tile, so the painted cells won't turn into bridges, " +
                        "elevated junctions and ramps by themselves. Click Rebuild to generate the proper tile.",
                        MessageType.Warning);
                }
                if (highways.Links.Count > 0 && !CanPaintRampTiles())
                {
                    EditorGUILayout.HelpBox(
                        "The ramps will be built, but their cover tiles can't be painted (Cover Tile must be a Road " +
                        "Cover Tile and Road Tile a RoadRuleTile), so the streets they land on won't turn into " +
                        "interchanges - each ramp will just meet a straight road.", MessageType.Warning);
                }
            }
            if (tilemap != null)
            {
                GridLayout grid = tilemap.layoutGrid;
                if (grid != null && grid.cellSwizzle != GridLayout.CellSwizzle.XZY)
                {
                    EditorGUILayout.HelpBox(
                        $"This Tilemap's Grid has Cell Swizzle = {grid.cellSwizzle}, not XZY - it isn't lying flat " +
                        "on the world ground plane, so road prefabs won't sit where the preview shows. Use " +
                        "'Create City Tilemap' or set the Grid's Cell Swizzle to XZY.", MessageType.Warning);
                }
                if (grid != null && roundabouts.Count > 0 && !Mathf.Approximately(grid.cellSize.x, 1f))
                {
                    EditorGUILayout.HelpBox(
                        $"The roundabout is 3 x 3 units but this Grid's cell size is {grid.cellSize.x:0.###}, " +
                        "so it won't fit its 3 x 3 cells. The road kit expects cell size 1.", MessageType.Warning);
                }
            }
            if (roadTile != null && !(roadTile is RuleTile))
            {
                EditorGUILayout.HelpBox(
                    "Road Tile is not a RuleTile, so painted cells won't become road prefabs by themselves. " +
                    "That's fine if you'll run Tools > OpenCity > Road Generator (Tilemap) on this Tilemap next.",
                    MessageType.Info);
            }
        }

        private static string DescribeBlockSizes(List<RectInt> blocks)
        {
            if (blocks.Count == 0)
            {
                return "";
            }
            int minSide = int.MaxValue;
            int maxSide = 0;
            foreach (RectInt block in blocks)
            {
                minSide = Mathf.Min(minSide, Mathf.Min(block.width, block.height));
                maxSide = Mathf.Max(maxSide, Mathf.Max(block.width, block.height));
            }
            return $" (sides {minSide}-{maxSide} cells)";
        }

        private bool CanGenerate(out string error)
        {
            if (tilemap == null)
            {
                error = "Assign a Tilemap (or click 'Create City Tilemap').";
                return false;
            }
            if (roadTile == null)
            {
                error = "Assign a Road Tile.";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>Whether 3x3 roundabouts can be placed with the current fields; says why not otherwise.</summary>
        private bool CanPlaceRoundabouts(out string reason)
        {
            if (!useRoundabouts)
            {
                reason = "Roundabouts is unticked.";
                return false;
            }
            if (roundaboutPrefab == null)
            {
                reason = "assign a Roundabout Prefab.";
                return false;
            }
            if (!(coverTile is RoadCoverTile))
            {
                reason = "assign a Road Cover Tile as Cover Tile (or click Create).";
                return false;
            }
            if (!(roadTile is RoadRuleTile))
            {
                reason = "Road Tile must be a RoadRuleTile - other tiles don't treat the cover tile as road, so the " +
                         "roads leading into each roundabout wouldn't connect to it.";
                return false;
            }
            reason = null;
            return true;
        }

        private CityBspLayout.Settings CurrentSettings()
        {
            return new CityBspLayout.Settings(
                sideLength, perimeterRoad, maxSplitDepth, minBlockSize, maxBlockSize,
                splitJitter, splitAxisBias, stopProbability, randomSeed);
        }

        /// <summary>
        /// The one code path both the preview and Generate read the layout through, so what the
        /// Scene view shows is exactly what gets painted.
        /// </summary>
        private CityBspLayout.Layout GetLayout()
        {
            CityBspLayout.Settings settings = CurrentSettings();
            if (!hasCachedLayout || !cachedSettings.Equals(settings))
            {
                cachedSettings = settings;
                cachedLayout = CityBspLayout.Build(settings);
                cachedGraph = RoadGridPlacer.BuildGraph(cachedLayout.RoadCells);
                hasCachedLayout = true;
            }
            return cachedLayout;
        }

        /// <summary>Min Crossing Road as a cell count for the current Side Length.</summary>
        private int RoundaboutMinCrossingLength()
        {
            return Mathf.Max(1, Mathf.CeilToInt(roundaboutMinRoadFraction * sideLength - 0.0001f));
        }

        /// <summary>Min Roundabout Spacing as a cell count for the current Side Length (never under 3, so pieces can't overlap).</summary>
        private int RoundaboutMinSpacing()
        {
            return Mathf.Max(3, Mathf.CeilToInt(roundaboutSpacingFraction * sideLength - 0.0001f));
        }

        /// <summary>The roundabouts for the current layout - empty when they can't be placed. Shared by preview and Generate.</summary>
        private List<RoundaboutPlanner.Placement> GetRoundaboutPlan()
        {
            CityBspLayout.Layout layout = GetLayout();
            int minLength = CanPlaceRoundabouts(out _) ? RoundaboutMinCrossingLength() : 0;
            int spacing = RoundaboutMinSpacing();
            if (cachedRoundabouts == null || cachedRoundaboutLayout != layout ||
                cachedRoundaboutMinLength != minLength || cachedRoundaboutSpacing != spacing)
            {
                cachedRoundaboutLayout = layout;
                cachedRoundaboutMinLength = minLength;
                cachedRoundaboutSpacing = spacing;
                cachedRoundabouts = minLength > 0
                    ? RoundaboutPlanner.Plan(layout.RoadCells, layout.RoadCellsSorted, minLength, spacing)
                    : new List<RoundaboutPlanner.Placement>();
                cachedRoundaboutRoadCells = new HashSet<Vector2Int>();
                foreach (RoundaboutPlanner.Placement roundabout in cachedRoundabouts)
                {
                    cachedRoundaboutRoadCells.UnionWith(roundabout.RoadCells);
                }
            }
            return cachedRoundabouts;
        }

        /// <summary>Min Link Span as a cell count for the current Side Length.</summary>
        private int HighwayMinSpan()
        {
            return Mathf.Max(1, Mathf.CeilToInt(highwayMinSpanFraction * sideLength - 0.0001f));
        }

        /// <summary>Whether highways can be built with the current fields; says why not otherwise.</summary>
        private bool CanPlaceHighways(out string reason)
        {
            if (!useHighways)
            {
                reason = "Highways is unticked.";
                return false;
            }
            if (highwayTile == null)
            {
                reason = "assign a Highway Tile (or click Create).";
                return false;
            }
            if (highwayRampLength >= 2 && !(coverTile is RoadCoverTile))
            {
                reason = "a two-cell ramp's top cell is painted with the Cover Tile - assign a Road Cover Tile, or set " +
                         "Ramp Length to 1.";
                return false;
            }
            if (highwayTilemap != null && highwayTilemap == tilemap)
            {
                reason = "Highway Tilemap is the street Tilemap - the highway needs its own layer. Clear the field " +
                         "to have one created.";
                return false;
            }
            if (highwayTilemap == null && tilemap != null && tilemap.transform.parent == null)
            {
                reason = "the street Tilemap has no parent Grid to add a Highways layer to - assign a Highway Tilemap.";
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// The highway plan for the current layout - empty when highways are off or can't be painted. Shared by the preview and Generate, like the layout and roundabouts, so the
        /// Scene view is never showing something other than what will be built.
        /// </summary>
        private HighwayPlanner.Plan GetHighwayPlan()
        {
            CityBspLayout.Layout layout = GetLayout();
            List<RoundaboutPlanner.Placement> roundabouts = GetRoundaboutPlan();
            var settings = new HighwayPlanner.Settings(
                highwayClusterDepth, HighwayMinSpan(),
                CanPlaceHighways(out _) ? highwayMaxLinks : 0,
                highwayRampLength, highwayTurnCost, highwayFollowStreets);
            if (cachedHighwayPlan == null || cachedHighwayLayout != layout ||
                !ReferenceEquals(cachedHighwayRoundabouts, roundabouts) || !cachedHighwaySettings.Equals(settings))
            {
                // A roundabout owns all nine of its cells: a highway support or ramp there would land
                // inside the roundabout, so the deck goes round it.
                var reserved = new HashSet<Vector2Int>();
                foreach (RoundaboutPlanner.Placement roundabout in roundabouts)
                {
                    RectInt footprint = roundabout.Footprint;
                    for (int x = footprint.xMin; x < footprint.xMax; x++)
                    {
                        for (int y = footprint.yMin; y < footprint.yMax; y++)
                        {
                            reserved.Add(new Vector2Int(x, y));
                        }
                    }
                }
                cachedHighwayLayout = layout;
                cachedHighwayRoundabouts = roundabouts;
                cachedHighwaySettings = settings;
                cachedHighwayPlan = HighwayPlanner.Build(layout, reserved, settings);
            }
            return cachedHighwayPlan;
        }

        /// <summary>
        /// Whether the ramps' cover tiles can be painted. Without them the ramp still stands, but
        /// the street it lands beside never notices, so it stays a straight instead of becoming the
        /// interchange's T-junction.
        /// </summary>
        private bool CanPaintRampTiles()
        {
            return coverTile is RoadCoverTile && roadTile is RoadRuleTile;
        }

        private void UseSelection()
        {
            GameObject go = Selection.activeGameObject;
            if (go == null)
            {
                Debug.LogWarning($"[{LogPrefix}] Nothing selected.");
                return;
            }
            Tilemap tm = go.GetComponent<Tilemap>();
            if (tm == null)
            {
                tm = go.GetComponentInChildren<Tilemap>();
            }
            if (tm == null)
            {
                Debug.LogWarning($"[{LogPrefix}] '{go.name}' has no Tilemap component.");
                return;
            }
            tilemap = tm;
            Repaint();
            SceneView.RepaintAll();
        }

        private void CreateCityTilemap()
        {
            var gridGo = new GameObject("City Road Grid");
            Undo.RegisterCreatedObjectUndo(gridGo, CreateTilemapUndoLabel);
            Grid grid = gridGo.AddComponent<Grid>();
            grid.cellSwizzle = GridLayout.CellSwizzle.XZY;
            grid.cellSize = new Vector3(newTilemapCellSize, newTilemapCellSize, newTilemapCellSize);

            var tilemapGo = new GameObject("Roads");
            Undo.RegisterCreatedObjectUndo(tilemapGo, CreateTilemapUndoLabel);
            tilemapGo.transform.SetParent(gridGo.transform, false);
            Tilemap tm = tilemapGo.AddComponent<Tilemap>();
            tilemapGo.AddComponent<TilemapRenderer>();

            tilemap = tm;
            highwayTilemap = CreateHighwayTilemap(gridGo.transform, CreateTilemapUndoLabel);
            Selection.activeGameObject = tilemapGo;
            Repaint();
            SceneView.RepaintAll();
            Debug.Log($"[{LogPrefix}] Created '{gridGo.name}/{tilemapGo.name}' and '{gridGo.name}/{highwayTilemap.name}' " +
                      $"(Cell Swizzle XZY, cell size {newTilemapCellSize:0.###}).");
        }

        private static Tilemap CreateHighwayTilemap(Transform grid, string undoLabel)
        {
            var go = new GameObject(HighwayTilemapName);
            Undo.RegisterCreatedObjectUndo(go, undoLabel);
            go.transform.SetParent(grid, false);
            Tilemap tm = go.AddComponent<Tilemap>();
            TilemapRenderer renderer = go.AddComponent<TilemapRenderer>();
            renderer.sortingOrder = 1;
            return tm;
        }

        /// <summary>
        /// The highway's own Tilemap layer if there is one: the assigned one, else a sibling of the
        /// street Tilemap called 'Highways'. Never the street Tilemap itself. Creates nothing, so the
        /// GUI and Clear can use it freely.
        /// </summary>
        private Tilemap FindHighwayTilemap()
        {
            if (tilemap == null)
            {
                return null;
            }
            if (highwayTilemap != null)
            {
                return highwayTilemap != tilemap ? highwayTilemap : null;
            }
            Transform grid = tilemap.transform.parent;
            Transform existing = grid != null ? grid.Find(HighwayTilemapName) : null;
            Tilemap found = existing != null ? existing.GetComponent<Tilemap>() : null;
            return found != tilemap ? found : null;
        }

        /// <summary>
        /// <see cref="FindHighwayTilemap"/>, or a new 'Highways' Tilemap in the street Tilemap's Grid,
        /// remembered in the Highway Tilemap field. Its tile pieces carry their own height, so it sits
        /// on the same ground plane as the streets. Null only if the street Tilemap has no Grid parent.
        /// </summary>
        private Tilemap GetOrCreateHighwayTilemap(string undoLabel)
        {
            Tilemap layer = FindHighwayTilemap();
            if (layer == null && tilemap.transform.parent != null)
            {
                layer = CreateHighwayTilemap(tilemap.transform.parent, undoLabel);
            }
            highwayTilemap = layer;
            return layer;
        }

        private void CreateHighwayTile()
        {
            HighwayRuleTile tile = HighwayBuilder.CreateOrUpdateTile(out string error);
            if (tile == null)
            {
                Debug.LogError($"[{LogPrefix}] Couldn't create the Highway Tile: {error}");
                return;
            }
            highwayTile = tile;
            if (highwayTilemap != null)
            {
                highwayTilemap.RefreshAllTiles();
            }
            Repaint();
        }

        private void CreateCoverTile()
        {
            var existing = AssetDatabase.LoadAssetAtPath<TileBase>(DefaultCoverTilePath);
            if (existing == null)
            {
                existing = CreateInstance<RoadCoverTile>();
                AssetDatabase.CreateAsset(existing, DefaultCoverTilePath);
                AssetDatabase.SaveAssets();
                Debug.Log($"[{LogPrefix}] Created {DefaultCoverTilePath}.");
            }
            coverTile = existing;
            Repaint();
        }

        private BoundsInt CityArea()
        {
            return new BoundsInt(originCell.x, originCell.y, 0, sideLength, sideLength, 1);
        }

        private Vector3Int ToTilemapCell(Vector2Int cityCell)
        {
            return new Vector3Int(originCell.x + cityCell.x, originCell.y + cityCell.y, 0);
        }

        /// <summary>
        /// What this tool already has inside the city square - street tiles, highway tiles and
        /// multi-cell pieces. Cached until the scene, one of the Tilemaps or the square changes.
        /// </summary>
        private CityAreaContents GetCityAreaContents()
        {
            if (tilemap == null)
            {
                return default;
            }
            Tilemap highwayLayer = FindHighwayTilemap();
            BoundsInt area = CityArea();
            if (contentsDirty || contentsTilemap != tilemap || contentsHighwayTilemap != highwayLayer ||
                contentsArea != area)
            {
                contentsTilemap = tilemap;
                contentsHighwayTilemap = highwayLayer;
                contentsArea = area;
                cachedContents = new CityAreaContents(
                    CountTiles(tilemap, area),
                    highwayLayer != null ? CountTiles(highwayLayer, area) : 0,
                    FindPiecesInCityArea(area).Count);
                contentsDirty = false;
            }
            return cachedContents;
        }

        private static int CountTiles(Tilemap target, BoundsInt area)
        {
            BoundsInt used = target.cellBounds;
            if (used.xMax <= area.xMin || area.xMax <= used.xMin || used.yMax <= area.yMin || area.yMax <= used.yMin)
            {
                return 0; // nothing painted anywhere near the square - skip reading the block
            }
            int count = 0;
            foreach (TileBase tile in target.GetTilesBlock(area))
            {
                if (tile != null)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>Erases every tile of <paramref name="target"/> in the city square. Returns how many tiles were there.</summary>
        private static int ClearTiles(Tilemap target, BoundsInt area)
        {
            int count = CountTiles(target, area);
            if (count > 0)
            {
                target.SetTilesBlock(area, new TileBase[area.size.x * area.size.y * area.size.z]);
                EditorUtility.SetDirty(target);
            }
            return count;
        }

        /// <summary>
        /// Every multi-cell piece on the street Tilemap covering any cell of the square: the
        /// roundabouts, plus highway prefabs left by the older prefab-based highway builder.
        /// </summary>
        private List<MultiCellRoadPiece> FindPiecesInCityArea(BoundsInt area)
        {
            var pieces = new List<MultiCellRoadPiece>();
            foreach (MultiCellRoadPiece piece in FindObjectsByType<MultiCellRoadPiece>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (piece.tilemap == tilemap && piece.Overlaps(area))
                {
                    pieces.Add(piece);
                }
            }
            return pieces;
        }

        /// <summary>
        /// Erases the city square on both Tilemaps and destroys its pieces, inside the caller's undo
        /// group. <paramref name="highwayLayer"/> must already be registered for undo if not null.
        /// </summary>
        private CityAreaContents ClearCityArea(Tilemap highwayLayer)
        {
            BoundsInt area = CityArea();
            int roadTiles = ClearTiles(tilemap, area);
            int highwayTiles = highwayLayer != null ? ClearTiles(highwayLayer, area) : 0;
            List<MultiCellRoadPiece> pieces = FindPiecesInCityArea(area);
            foreach (MultiCellRoadPiece piece in pieces)
            {
                Undo.DestroyObjectImmediate(piece.gameObject);
            }
            return new CityAreaContents(roadTiles, highwayTiles, pieces.Count);
        }

        /// <summary>The GameObject the multi-cell pieces are parented under: a sibling of the Tilemap inside its Grid.</summary>
        private Transform GetOrCreatePiecesRoot(string undoLabel)
        {
            string rootName = $"{tilemap.name} Multi-Cell Pieces";
            Transform parent = tilemap.transform.parent;
            Transform root = parent != null ? parent.Find(rootName) : null;
            if (root == null && parent == null)
            {
                foreach (GameObject go in tilemap.gameObject.scene.GetRootGameObjects())
                {
                    if (go.name == rootName)
                    {
                        root = go.transform;
                        break;
                    }
                }
            }
            if (root != null)
            {
                return root;
            }
            var rootGo = new GameObject(rootName);
            Undo.RegisterCreatedObjectUndo(rootGo, undoLabel);
            if (parent != null)
            {
                rootGo.transform.SetParent(parent, false);
            }
            else
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(rootGo, tilemap.gameObject.scene);
            }
            return rootGo.transform;
        }

        private void PlaceRoundabout(RoundaboutPlanner.Placement roundabout, Transform root)
        {
            Vector3Int center = ToTilemapCell(roundabout.Center);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(roundaboutPrefab, root);
            Undo.RegisterCreatedObjectUndo(instance, UndoLabel);
            instance.name = $"{roundaboutPrefab.name} ({center.x}, {center.y})";
            // The piece is symmetric under quarter turns and centered on its pivot, so it sits on the
            // crossroad's cell center with no rotation.
            instance.transform.SetPositionAndRotation(tilemap.GetCellCenterWorld(center), Quaternion.identity);
            instance.transform.localScale = Vector3.one;

            MultiCellRoadPiece marker = instance.AddComponent<MultiCellRoadPiece>();
            marker.tilemap = tilemap;
            RectInt footprint = roundabout.Footprint;
            marker.cells = new BoundsInt(originCell.x + footprint.x, originCell.y + footprint.y, 0, 3, 3, 1);
        }

        private static int BeginUndoGroup(string label)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(label);
            return Undo.GetCurrentGroup();
        }

        /// <summary>
        /// Paints the current plan - streets, roundabouts, ramp interchanges and highways - as one
        /// Undo step. Everything painted is read from the same caches the Scene preview draws.
        /// </summary>
        private void Generate()
        {
            if (!CanGenerate(out string error))
            {
                Debug.LogError($"[{LogPrefix}] {error}");
                return;
            }

            CityBspLayout.Layout layout = GetLayout();
            List<RoundaboutPlanner.Placement> roundabouts = GetRoundaboutPlan();
            HighwayPlanner.Plan highways = GetHighwayPlan();

            int undoGroup = BeginUndoGroup(UndoLabel);
            Undo.RegisterCompleteObjectUndo(tilemap, UndoLabel);
            Tilemap highwayLayer = highways.Links.Count > 0 ? GetOrCreateHighwayTilemap(UndoLabel) : FindHighwayTilemap();
            if (highwayLayer != null)
            {
                Undo.RegisterCompleteObjectUndo(highwayLayer, UndoLabel);
            }

            CityAreaContents cleared = clearCityAreaFirst ? ClearCityArea(highwayLayer) : default;
            PaintStreets(layout);
            int interchanges = PaintRampInterchanges(highways);
            if (roundabouts.Count > 0)
            {
                Transform root = GetOrCreatePiecesRoot(UndoLabel);
                foreach (RoundaboutPlanner.Placement roundabout in roundabouts)
                {
                    PlaceRoundabout(roundabout, root);
                }
            }
            HighwayBuilder.Result highwayResult = highways.Links.Count > 0
                ? HighwayBuilder.Paint(highways, highwayLayer, originCell, highwayTile, coverTile)
                : default;

            Undo.CollapseUndoOperations(undoGroup);
            contentsDirty = true;

            RoadGridPlacer.CountByCategory(layout.RoadCells, cachedGraph, out int[] counts, out _);
            Debug.Log($"[{LogPrefix}] Painted {layout.RoadCellsSorted.Count} road cell(s) onto '{tilemap.name}' for a " +
                      $"{layout.SideLength}x{layout.SideLength} city at cell {originCell} ({layout.Cuts.Count} cut(s), " +
                      $"{layout.Blocks.Count} block(s), T-junction {counts[(int)RoadGridPlacer.ShapeCategory.TJunction]}, " +
                      $"4-way {counts[(int)RoadGridPlacer.ShapeCategory.FourWay] - roundabouts.Count}, roundabout {roundabouts.Count}, " +
                      $"seed {randomSeed}" + (cleared.IsEmpty ? "" : $"; replaced {cleared.Describe()}") + ").");
            if (highways.Links.Count > 0)
            {
                Debug.Log($"[{LogPrefix}] Painted {highways.Links.Count} highway link(s) over {highways.Districts.Count} " +
                          $"cluster(s) at BSP depth {highwayClusterDepth} onto '{highwayLayer.name}': " +
                          $"{highwayResult.DeckTiles} deck tile(s) ({highways.BridgeCells} on top of a street), " +
                          $"{highways.Ramps.Count} ramp(s) in {highwayResult.RampTiles} tile(s), " +
                          $"{interchanges} street interchange cover tile(s)" +
                          (highways.UnroutableLinks > 0 ? $", {highways.UnroutableLinks} link(s) unroutable" : "") + ".");
            }
            ShowNotification(new GUIContent(
                $"{(cleared.IsEmpty ? "Generated" : "Regenerated")} {layout.SideLength}x{layout.SideLength} city" +
                (highways.Links.Count > 0 ? $"\n{highways.Links.Count} highway link(s)" : "")), 1.5);
        }

        /// <summary>Road Tile on every street cell, Cover Tile on the cells under a roundabout, in one batch.</summary>
        private void PaintStreets(CityBspLayout.Layout layout)
        {
            var positions = new Vector3Int[layout.RoadCellsSorted.Count];
            var tiles = new TileBase[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                Vector2Int cell = layout.RoadCellsSorted[i];
                positions[i] = ToTilemapCell(cell);
                tiles[i] = cachedRoundaboutRoadCells.Contains(cell) ? coverTile : roadTile;
            }
            tilemap.SetTiles(positions, tiles);
            EditorUtility.SetDirty(tilemap);
        }

        /// <summary>
        /// A ramp stands on block cells, so it is painted on the street layer separately - and only
        /// its bottom cell is. The cover tile shows nothing but reads as road, which is what turns the
        /// street that cell touches into the interchange's T-junction; painting the rest of the ramp
        /// would hand the same arm to whatever lies past its top, which the highway passes rather
        /// than joins. Returns how many were painted.
        /// </summary>
        private int PaintRampInterchanges(HighwayPlanner.Plan highways)
        {
            if (highways.FootCells.Count == 0 || !CanPaintRampTiles())
            {
                return 0;
            }
            var positions = new Vector3Int[highways.FootCells.Count];
            var tiles = new TileBase[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = ToTilemapCell(highways.FootCells[i]);
                tiles[i] = coverTile;
            }
            tilemap.SetTiles(positions, tiles);
            return positions.Length;
        }

        /// <summary>
        /// Erases the city square on both layers after a confirmation that says exactly what goes.
        /// The dialog can be opted out of for the session; the clear is undoable either way.
        /// </summary>
        private void Clear(CityAreaContents contents)
        {
            if (tilemap == null || contents.IsEmpty)
            {
                return;
            }
            if (!EditorUtility.DisplayDialog(
                    "Clear City Area",
                    $"Erase {contents.Describe()} inside the {sideLength}x{sideLength} city square at cell {originCell}?\n\n" +
                    "Tiles and pieces outside the square are not touched. You can undo this.",
                    "Clear", "Cancel", DialogOptOutDecisionType.ForThisSession, ClearConfirmOptOutKey))
            {
                return;
            }

            int undoGroup = BeginUndoGroup(ClearUndoLabel);
            Undo.RegisterCompleteObjectUndo(tilemap, ClearUndoLabel);
            Tilemap highwayLayer = FindHighwayTilemap();
            if (highwayLayer != null)
            {
                Undo.RegisterCompleteObjectUndo(highwayLayer, ClearUndoLabel);
            }
            CityAreaContents cleared = ClearCityArea(highwayLayer);
            Undo.CollapseUndoOperations(undoGroup);
            contentsDirty = true;

            Debug.Log($"[{LogPrefix}] Cleared {cleared.Describe()} from the {sideLength}x{sideLength} city area at cell {originCell}.");
            ShowNotification(new GUIContent("City area cleared"), 1.5);
        }

        // ---------------------------------------------------------------
        // Scene view preview
        // ---------------------------------------------------------------

        private void OnSceneGUI(SceneView sceneView)
        {
            if (Event.current.type != EventType.Repaint || !previewVisible || tilemap == null)
            {
                return;
            }

            CityBspLayout.Layout layout = GetLayout();
            List<RoundaboutPlanner.Placement> roundabouts = GetRoundaboutPlan();
            HighwayPlanner.Plan highways = GetHighwayPlan();
            // Lift slightly off the ground so the gizmos don't z-fight with painted road prefabs.
            Vector3 lift = Vector3.up * 0.05f;

            Vector3[] outline = CellRectCorners(new RectInt(0, 0, layout.SideLength, layout.SideLength), lift);
            Handles.color = Color.white;
            Handles.DrawAAPolyLine(3f, outline[0], outline[1], outline[2], outline[3], outline[0]);

            if (showBlocks)
            {
                var fill = new Color(BlockColor.r, BlockColor.g, BlockColor.b, 0.12f);
                foreach (RectInt block in layout.Blocks)
                {
                    Handles.DrawSolidRectangleWithOutline(CellRectCorners(block, lift), fill, BlockColor);
                }
            }

            var roundaboutFill = new Color(RoundaboutColor.r, RoundaboutColor.g, RoundaboutColor.b, 0.35f);
            foreach (RoundaboutPlanner.Placement roundabout in roundabouts)
            {
                Handles.DrawSolidRectangleWithOutline(CellRectCorners(roundabout.Footprint, lift * 2f), roundaboutFill, RoundaboutColor);
            }

            if (showDistricts && useHighways)
            {
                Handles.color = DistrictColor;
                foreach (RectInt district in highways.Districts)
                {
                    Vector3[] corners = CellRectCorners(district, lift * 2f);
                    Handles.DrawAAPolyLine(2f, corners[0], corners[1], corners[2], corners[3], corners[0]);
                }
            }

            // The highway is drawn at its real height, so the preview reads as the overpass it is.
            Vector3 deckLift = Vector3.up * (HighwayBuilder.DeckHeight + 0.05f);
            var deckFill = new Color(HighwayDeckColor.r, HighwayDeckColor.g, HighwayDeckColor.b, 0.5f);
            foreach (Vector2Int cell in highways.DeckCellsSorted)
            {
                Handles.DrawSolidRectangleWithOutline(
                    CellRectCorners(new RectInt(cell.x, cell.y, 1, 1), deckLift), deckFill, HighwayDeckColor);
            }
            var rampFill = new Color(HighwayRampColor.r, HighwayRampColor.g, HighwayRampColor.b, 0.5f);
            foreach (HighwayPlanner.Ramp ramp in highways.Ramps)
            {
                // Each ramp cell is drawn at the height that cell's surface actually reaches, so a
                // two-cell ramp visibly climbs.
                for (int i = 0; i < ramp.Length; i++)
                {
                    Vector2Int cell = ramp.Foot + ramp.Direction * i;
                    float t = ramp.Length == 1 ? 0.5f : (i + 0.5f) / ramp.Length;
                    Handles.DrawSolidRectangleWithOutline(
                        CellRectCorners(new RectInt(cell.x, cell.y, 1, 1), Vector3.up * (HighwayBuilder.DeckHeight * t + 0.05f)),
                        rampFill, HighwayRampColor);
                }
            }

            if (layout.RoadCells.Count > PreviewCellLimit)
            {
                return;
            }
            foreach (Vector2Int cell in layout.RoadCellsSorted)
            {
                if (cachedRoundaboutRoadCells.Contains(cell))
                {
                    continue;
                }
                Color color = RoadGridPlacer.TryClassify(cachedGraph[cell], out RoadGridPlacer.ShapeCategory category)
                    ? CategoryColors[category]
                    : UnclassifiedCellColor;
                Handles.DrawSolidRectangleWithOutline(
                    CellRectCorners(new RectInt(cell.x, cell.y, 1, 1), lift),
                    new Color(color.r, color.g, color.b, 0.45f), color);
            }
        }

        /// <summary>
        /// World-space corners of a city-local cell rectangle, read through the Tilemap's own
        /// CellToWorld so Grid cell size, swizzle and transform are all respected.
        /// </summary>
        private Vector3[] CellRectCorners(RectInt rect, Vector3 lift)
        {
            var origin = new Vector3Int(originCell.x + rect.x, originCell.y + rect.y, 0);
            return new[]
            {
                tilemap.CellToWorld(origin) + lift,
                tilemap.CellToWorld(origin + new Vector3Int(rect.width, 0, 0)) + lift,
                tilemap.CellToWorld(origin + new Vector3Int(rect.width, rect.height, 0)) + lift,
                tilemap.CellToWorld(origin + new Vector3Int(0, rect.height, 0)) + lift,
            };
        }
    }
}
