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
    /// See Docs/CityBspTilemapGenerator.md.
    /// </summary>
    public class CityRoadTilemapWindow : EditorWindow
    {
        private const string UndoLabel = "Generate City Roads (BSP Tilemap)";
        private const string ClearUndoLabel = "Clear City Roads (BSP Tilemap)";
        private const string CreateTilemapUndoLabel = "Create City Tilemap";
        private const string LogPrefix = "CityRoadsBsp";
        private const string DefaultRoadTilePath = "Assets/Tiles/New Rule Tile.asset";
        private const string DefaultCoverTilePath = "Assets/Tiles/Road Cover Tile.asset";
        private const string DefaultRoundaboutPath = "Assets/Prefabs/Roads/road-roundabout.prefab";
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

        [SerializeField] private bool clearCityAreaFirst = true;

        [SerializeField] private bool previewVisible = true;
        [SerializeField] private bool showBlocks = true;

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

        private Vector2 scrollPosition;

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
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();

            // Begin/End rather than a ScrollViewScope: the scrolled region is everything except
            // the Generate/Clear row below, which stays pinned to the bottom of the window.
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

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
                    "Painted on the road cells under each roundabout: spawns nothing, but still counts as road " +
                    "for the road pieces around it. Must be a Road Cover Tile."), coverTile, typeof(TileBase), false);
                if (coverTile == null && GUILayout.Button("Create", GUILayout.Width(60)))
                {
                    CreateCoverTile();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Painting", EditorStyles.boldLabel);
            clearCityAreaFirst = EditorGUILayout.Toggle(new GUIContent(
                "Clear City Area First",
                "Erase every tile (and every roundabout) inside the city square before painting, so " +
                "regenerating replaces the previous layout. Off: roads are painted over whatever is already there."),
                clearCityAreaFirst);

            EditorGUILayout.Space();
            DrawPreviewControls();
            DrawPreview();

            EditorGUILayout.EndScrollView();

            if (EditorGUI.EndChangeCheck())
            {
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = CanGenerate(out _);
                if (GUILayout.Button("Generate", GUILayout.Height(30)))
                {
                    Generate();
                }
                GUI.enabled = tilemap != null;
                if (GUILayout.Button("Clear City Area", GUILayout.Height(30), GUILayout.Width(120)))
                {
                    Clear();
                }
                GUI.enabled = true;
            }
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
            }
            EditorGUILayout.HelpBox(
                "Scene view: white = city boundary, green = blocks, gray = straight, yellow = bend, " +
                "orange = T-junction, cyan = 4-way, red = dead-end, purple = 3x3 roundabout.", MessageType.None);
        }

        private void DrawPreview()
        {
            if (!CanGenerate(out string error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Warning);
            }

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
            Selection.activeGameObject = tilemapGo;
            Repaint();
            SceneView.RepaintAll();
            Debug.Log($"[{LogPrefix}] Created '{gridGo.name}/{tilemapGo.name}' (Cell Swizzle XZY, cell size {newTilemapCellSize:0.###}).");
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

        private BoundsInt CityArea(int side)
        {
            return new BoundsInt(originCell.x, originCell.y, 0, side, side, 1);
        }

        private Vector3Int ToTilemapCell(Vector2Int cityCell)
        {
            return new Vector3Int(originCell.x + cityCell.x, originCell.y + cityCell.y, 0);
        }

        /// <summary>Erases every tile in the city square. Returns how many tiles were there.</summary>
        private int ClearCityArea(int side)
        {
            BoundsInt area = CityArea(side);
            TileBase[] existing = tilemap.GetTilesBlock(area);
            int count = 0;
            foreach (TileBase tile in existing)
            {
                if (tile != null)
                {
                    count++;
                }
            }
            if (count > 0)
            {
                tilemap.SetTilesBlock(area, new TileBase[existing.Length]);
            }
            return count;
        }

        /// <summary>Destroys (undoably) every multi-cell piece on this Tilemap that covers any cell of the city square.</summary>
        private int RemovePiecesInCityArea(int side)
        {
            BoundsInt area = CityArea(side);
            int removed = 0;
            foreach (MultiCellRoadPiece piece in FindObjectsByType<MultiCellRoadPiece>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (piece.tilemap == tilemap && piece.Overlaps(area))
                {
                    Undo.DestroyObjectImmediate(piece.gameObject);
                    removed++;
                }
            }
            return removed;
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

        private void Generate()
        {
            if (!CanGenerate(out string error))
            {
                Debug.LogError($"[{LogPrefix}] {error}");
                return;
            }

            CityBspLayout.Layout layout = GetLayout();
            List<RoundaboutPlanner.Placement> roundabouts = GetRoundaboutPlan();

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoLabel);
            Undo.RegisterCompleteObjectUndo(tilemap, UndoLabel);

            int cleared = 0;
            int removedPieces = 0;
            if (clearCityAreaFirst)
            {
                cleared = ClearCityArea(layout.SideLength);
                removedPieces = RemovePiecesInCityArea(layout.SideLength);
            }

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

            if (roundabouts.Count > 0)
            {
                Transform root = GetOrCreatePiecesRoot(UndoLabel);
                foreach (RoundaboutPlanner.Placement roundabout in roundabouts)
                {
                    PlaceRoundabout(roundabout, root);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);

            RoadGridPlacer.CountByCategory(layout.RoadCells, cachedGraph, out int[] counts, out _);
            Debug.Log($"[{LogPrefix}] Painted {positions.Length} road cell(s) onto '{tilemap.name}' for a " +
                      $"{layout.SideLength}x{layout.SideLength} city at cell {originCell} ({layout.Cuts.Count} cut(s), " +
                      $"{layout.Blocks.Count} block(s), T-junction {counts[(int)RoadGridPlacer.ShapeCategory.TJunction]}, " +
                      $"4-way {counts[(int)RoadGridPlacer.ShapeCategory.FourWay] - roundabouts.Count}, roundabout {roundabouts.Count}, seed {randomSeed}" +
                      (cleared > 0 ? $", {cleared} old tile(s) cleared" : "") +
                      (removedPieces > 0 ? $", {removedPieces} old multi-cell piece(s) removed" : "") + ").");
        }

        private void Clear()
        {
            if (tilemap == null)
            {
                return;
            }
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(ClearUndoLabel);
            Undo.RegisterCompleteObjectUndo(tilemap, ClearUndoLabel);
            int cleared = ClearCityArea(sideLength);
            int removedPieces = RemovePiecesInCityArea(sideLength);
            EditorUtility.SetDirty(tilemap);
            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log($"[{LogPrefix}] Cleared {cleared} tile(s) and {removedPieces} multi-cell piece(s) from the " +
                      $"{sideLength}x{sideLength} city area at cell {originCell}.");
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
