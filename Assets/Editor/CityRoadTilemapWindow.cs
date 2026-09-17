using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OpenCity.Tiles;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEditor.IMGUI.Controls;
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
    /// On top of the street grid it can deck the city's main avenues over as elevated expressways -
    /// planned by HighwayPlanner from the BSP tree's top levels and painted by HighwayBuilder as a
    /// highway RuleTile on a second Tilemap layer, so the highway resolves its own shapes the same way
    /// the streets do without ever mixing with them. Expressways are planned before roundabouts, so a
    /// big crossing under the deck stays a flyover crossing.
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
        private const string BuildingTilemapName = "Buildings";
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
        private static readonly Color CoverageNearColor = new Color(0.2f, 0.85f, 0.3f);
        private static readonly Color CoverageFarColor = new Color(0.95f, 0.2f, 0.15f);
        private static readonly Color HighwayDeckColor = new Color(0.15f, 0.45f, 1f);
        private static readonly Color HighwayRampColor = new Color(1f, 0.35f, 0.55f);

        // Building preview, by (int)BuildingTier: Low, Mid, High, Tower, Filler.
        private static readonly Color[] BuildingTierColors =
        {
            new Color(0.55f, 0.8f, 0.5f), new Color(0.95f, 0.8f, 0.45f), new Color(1f, 0.55f, 0.2f),
            new Color(0.9f, 0.2f, 0.25f), new Color(0.45f, 0.45f, 0.5f),
        };
        /// <summary>Preview box height per cell of a tier, so the Scene view reads as a rough skyline.</summary>
        private static readonly float[] BuildingTierPreviewHeights = { 0.8f, 1.3f, 2.2f, 4f, 1.6f };

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
        [SerializeField] private int highwayLevels = 2;
        [SerializeField] private float highwayMinCorridorFraction = 0.5f;
        [SerializeField] private float highwayCorridorSpacingFraction = 0.25f;
        [SerializeField] private int highwayRampSpacing = 10;
        [SerializeField] private int highwayRampLength = 2;
        [SerializeField] private bool highwayRingExpressway;
        [SerializeField] private Tilemap highwayTilemap;
        [SerializeField] private TileBase highwayTile;

        [SerializeField] private bool useBuildings = true;
        [SerializeField] private float buildingDensity = 0.85f;
        [SerializeField] private float buildingDowntown = 0.5f;
        [SerializeField] private int buildingSeed = 777;
        [SerializeField] private Tilemap buildingTilemap;
        [SerializeField] private bool showBuildings = true;

        [SerializeField] private bool clearCityAreaFirst = true;

        [SerializeField] private bool previewVisible = true;
        [SerializeField] private bool showBlocks = true;
        [SerializeField] private bool showCoverage = true;

        [SerializeField] private bool showBspTree = true;
        [SerializeField] private TreeViewState<int> bspTreeState = new TreeViewState<int>();
        private CityBspTreePanel bspTreePanel;
        // The tree panel's rows, rebuilt only when the layout object changes.
        private CityBspLayout.Layout cachedTreeLayout;
        private HighwayPlanner.Plan cachedTreeHighways;
        private List<CityBspTree.Node> cachedTreeNodes;

        // Layout cache, keyed by the exact settings it was built from - the scene preview asks for
        // the layout on every repaint, and a 512-wide city is too big to rebuild that often.
        private bool hasCachedLayout;
        private CityBspLayout.Settings cachedSettings;
        private CityBspLayout.Layout cachedLayout;
        private Dictionary<Vector2Int, RoadGridPlacer.Direction> cachedGraph;

        // Roundabout cache, keyed by the layout and highway plan objects plus the effective minimum
        // crossing length (0 = roundabouts off).
        private CityBspLayout.Layout cachedRoundaboutLayout;
        private HighwayPlanner.Plan cachedRoundaboutHighways;
        private int cachedRoundaboutMinLength = -1;
        private int cachedRoundaboutSpacing = -1;
        private List<RoundaboutPlanner.Placement> cachedRoundabouts;
        private HashSet<Vector2Int> cachedRoundaboutRoadCells;

        // Highway cache, keyed by the layout it was built on (a stable object reference until a layout
        // setting changes) plus the highway settings.
        private CityBspLayout.Layout cachedHighwayLayout;
        private HighwayPlanner.Settings cachedHighwaySettings;
        private HighwayPlanner.Plan cachedHighwayPlan;

        // Building tiles, loaded from BuildingTileBuilder.TileFolder when first needed and after Create
        // or a project change; the plan is keyed by everything it is built on.
        private List<BuildingTile> buildingTiles;
        private BuildingCoverTile buildingCoverTile;
        private CityBspLayout.Layout cachedBuildingLayout;
        private HighwayPlanner.Plan cachedBuildingHighways;
        private List<RoundaboutPlanner.Placement> cachedBuildingRoundabouts;
        private int cachedBuildingTilesSignature;
        private BuildingPlanner.Settings cachedBuildingSettings;
        private BuildingPlanner.Plan cachedBuildingPlan;

        // What is already inside the city square, for the Generate/Clear buttons. Counting means
        // reading a block of tiles and scanning the scene for pieces, so it is recomputed only
        // when the scene, a Tilemap or the square itself changes - not on every GUI event.
        private bool contentsDirty = true;
        private Tilemap contentsTilemap;
        private Tilemap contentsHighwayTilemap;
        private Tilemap contentsBuildingTilemap;
        private BoundsInt contentsArea;
        private CityAreaContents cachedContents;

        private Vector2 scrollPosition;

        /// <summary>
        /// Where the window's settings are kept between editor sessions. UserSettings is per project
        /// and ignored by git, so each checkout keeps its own city without touching version control.
        /// </summary>
        private const string SavedStatePath = "UserSettings/OpenCity/CityRoadTilemapWindow.json";

        // Saved object ids that couldn't be resolved yet, by field name - typically a Tilemap whose
        // scene isn't open. Kept so saving doesn't forget them, and retried whenever a scene opens.
        private readonly Dictionary<string, string> pendingObjectIds = new Dictionary<string, string>();

        /// <summary>
        /// The saved settings: one entry per [SerializeField] declared on this window, so a setting
        /// added later is remembered without touching this code. Object fields are stored as
        /// GlobalObjectIds, which - unlike instance IDs - stay valid across an editor restart and work
        /// for scene objects as well as assets.
        /// </summary>
        [Serializable]
        private class SavedState
        {
            public List<SavedField> fields = new List<SavedField>();
        }

        [Serializable]
        private class SavedField
        {
            public string name;
            public int intValue;
            public float floatValue;
            public bool boolValue;
            public Vector2Int vectorValue;
            public string objectId;
        }

        /// <summary>Everything this tool owns inside the city square.</summary>
        private readonly struct CityAreaContents
        {
            public readonly int RoadTiles;
            public readonly int HighwayTiles;
            public readonly int BuildingTiles;
            public readonly int Pieces;

            public CityAreaContents(int roadTiles, int highwayTiles, int buildingTiles, int pieces)
            {
                RoadTiles = roadTiles;
                HighwayTiles = highwayTiles;
                BuildingTiles = buildingTiles;
                Pieces = pieces;
            }

            public bool IsEmpty => RoadTiles == 0 && HighwayTiles == 0 && BuildingTiles == 0 && Pieces == 0;

            public string Describe()
            {
                var parts = new List<string>();
                if (RoadTiles > 0) parts.Add($"{RoadTiles} street tile(s)");
                if (HighwayTiles > 0) parts.Add($"{HighwayTiles} highway tile(s)");
                if (BuildingTiles > 0) parts.Add($"{BuildingTiles} building tile(s)");
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
            LoadState();
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
            if (bspTreeState == null)
            {
                bspTreeState = new TreeViewState<int>();
            }
            bspTreePanel = new CityBspTreePanel(bspTreeState);
            bspTreePanel.Changed += SceneView.RepaintAll;
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.hierarchyChanged += OnSceneContentsChanged;
            Undo.undoRedoPerformed += OnSceneContentsChanged;
            Tilemap.tilemapTileChanged += OnTilemapTileChanged;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorApplication.projectChanged += OnProjectChanged;
            contentsDirty = true;
        }

        private void OnDisable()
        {
            SaveState();
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.hierarchyChanged -= OnSceneContentsChanged;
            Undo.undoRedoPerformed -= OnSceneContentsChanged;
            Tilemap.tilemapTileChanged -= OnTilemapTileChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorApplication.projectChanged -= OnProjectChanged;
        }

        /// <summary>A building tile may have been added, deleted or edited: reload them on next use.</summary>
        private void OnProjectChanged()
        {
            buildingTiles = null;
            Repaint();
        }

        // ---------------------------------------------------------------
        // Saved settings
        // ---------------------------------------------------------------

        /// <summary>
        /// Restores the settings saved by <see cref="SaveState"/>. Runs on every OnEnable, which is
        /// after Unity has already restored whatever the window layout kept, so the saved file wins -
        /// it is also the only source that remembers the scene Tilemaps across a restart.
        /// </summary>
        private void LoadState()
        {
            if (!File.Exists(SavedStatePath))
            {
                return;
            }
            SavedState state;
            try
            {
                state = JsonUtility.FromJson<SavedState>(File.ReadAllText(SavedStatePath));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogPrefix}] Couldn't read saved settings from {SavedStatePath}: {e.Message}");
                return;
            }
            if (state?.fields == null)
            {
                return;
            }

            var persisted = new Dictionary<string, FieldInfo>();
            foreach (FieldInfo field in PersistedFields())
            {
                persisted[field.Name] = field;
            }
            pendingObjectIds.Clear();
            foreach (SavedField saved in state.fields)
            {
                if (saved?.name == null || !persisted.TryGetValue(saved.name, out FieldInfo field))
                {
                    continue;
                }
                Type type = field.FieldType;
                if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                {
                    if (string.IsNullOrEmpty(saved.objectId))
                    {
                        continue;
                    }
                    UnityEngine.Object resolved = Resolve(saved.objectId);
                    if (resolved != null && type.IsInstanceOfType(resolved))
                    {
                        field.SetValue(this, resolved);
                    }
                    else if ((UnityEngine.Object)field.GetValue(this) == null)
                    {
                        // After a domain reload (script compile, entering Play Mode) the field usually
                        // still holds a live reference; only when it doesn't is the id worth waiting on.
                        pendingObjectIds[field.Name] = saved.objectId;
                    }
                }
                else if (type == typeof(int)) field.SetValue(this, saved.intValue);
                else if (type == typeof(float)) field.SetValue(this, saved.floatValue);
                else if (type == typeof(bool)) field.SetValue(this, saved.boolValue);
                else if (type == typeof(Vector2Int)) field.SetValue(this, saved.vectorValue);
            }
        }

        /// <summary>
        /// Writes the current settings to <see cref="SavedStatePath"/>. Called whenever the window
        /// changes something, not just on close, so a crash doesn't lose them. Skipped in Play Mode,
        /// where the scene references point at the runtime copies of the Tilemaps.
        /// </summary>
        private void SaveState()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }
            var state = new SavedState();
            foreach (FieldInfo field in PersistedFields())
            {
                var saved = new SavedField { name = field.Name };
                object value = field.GetValue(this);
                Type type = field.FieldType;
                if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                {
                    var obj = (UnityEngine.Object)value;
                    if (obj != null)
                    {
                        pendingObjectIds.Remove(field.Name);
                        saved.objectId = IdOf(obj);
                    }
                    else
                    {
                        pendingObjectIds.TryGetValue(field.Name, out saved.objectId);
                    }
                }
                else if (type == typeof(int)) saved.intValue = (int)value;
                else if (type == typeof(float)) saved.floatValue = (float)value;
                else if (type == typeof(bool)) saved.boolValue = (bool)value;
                else if (type == typeof(Vector2Int)) saved.vectorValue = (Vector2Int)value;
                state.fields.Add(saved);
            }

            string json = JsonUtility.ToJson(state, true);
            try
            {
                if (File.Exists(SavedStatePath) && File.ReadAllText(SavedStatePath) == json)
                {
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(SavedStatePath));
                File.WriteAllText(SavedStatePath, json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogPrefix}] Couldn't save settings to {SavedStatePath}: {e.Message}");
            }
        }

        /// <summary>
        /// This window's own [SerializeField]s of a type <see cref="SavedField"/> can hold. The
        /// EditorWindow base class's state (position, title, overlays) is deliberately left out.
        /// </summary>
        private static IEnumerable<FieldInfo> PersistedFields()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (FieldInfo field in typeof(CityRoadTilemapWindow).GetFields(flags))
            {
                if (!field.IsDefined(typeof(SerializeField), false))
                {
                    continue;
                }
                Type type = field.FieldType;
                if (type == typeof(int) || type == typeof(float) || type == typeof(bool) || type == typeof(Vector2Int) ||
                    typeof(UnityEngine.Object).IsAssignableFrom(type))
                {
                    yield return field;
                }
            }
        }

        private void OnSceneOpened(UnityEngine.SceneManagement.Scene scene, OpenSceneMode mode)
        {
            if (ResolvePendingObjects())
            {
                contentsDirty = true;
                Repaint();
                SceneView.RepaintAll();
            }
        }

        /// <summary>Assigns the saved objects that can be found now. Returns whether any were.</summary>
        private bool ResolvePendingObjects()
        {
            bool resolvedAny = false;
            foreach (FieldInfo field in PersistedFields())
            {
                if (!pendingObjectIds.TryGetValue(field.Name, out string id))
                {
                    continue;
                }
                UnityEngine.Object resolved = Resolve(id);
                if (resolved != null && field.FieldType.IsInstanceOfType(resolved))
                {
                    field.SetValue(this, resolved);
                    pendingObjectIds.Remove(field.Name);
                    resolvedAny = true;
                }
            }
            return resolvedAny;
        }

        /// <summary>The GlobalObjectId of <paramref name="obj"/>; null for an object in a never-saved scene, which has no stable id.</summary>
        private static string IdOf(UnityEngine.Object obj)
        {
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(obj);
            return id.assetGUID == default ? null : id.ToString();
        }

        private static UnityEngine.Object Resolve(string id)
        {
            return GlobalObjectId.TryParse(id, out GlobalObjectId globalId)
                ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId)
                : null;
        }

        private void OnSceneContentsChanged()
        {
            contentsDirty = true;
            Repaint();
        }

        private void OnTilemapTileChanged(Tilemap changed, Tilemap.SyncTile[] tiles)
        {
            if (tilemap != null && (changed == tilemap || changed == FindHighwayTilemap() || changed == FindBuildingTilemap()))
            {
                OnSceneContentsChanged();
            }
        }

        // ---------------------------------------------------------------
        // GUI
        // ---------------------------------------------------------------

        private void OnGUI()
        {
            try
            {
                DrawWindow();
            }
            finally
            {
                // Finally, because the Generate/Clear buttons leave OnGUI through ExitGUI's exception.
                if (GUI.changed)
                {
                    SaveState();
                }
            }
        }

        private void DrawWindow()
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
            DrawBuildingSection();
            EditorGUILayout.Space();
            DrawPreviewControls();
            DrawPreview();
            DrawBspTreeSection();
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
                    randomSeed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
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
                "Deck the city's main avenues over as elevated expressways. Each runs straight above its " +
                "avenue, meets the other expressways in interchanges, comes down onto the avenue at a free end, " +
                "and has on/off ramps onto cross streets along the way. Painted with the Highway Tile on its " +
                "own Tilemap layer, so the street grid underneath keeps working."), useHighways);
            using (new EditorGUI.DisabledScope(!useHighways))
            {
                highwayLevels = EditorGUILayout.IntSlider(new GUIContent(
                    $"Avenue Levels ({GetHighwayPlan().Corridors.Count} expressways)",
                    "How many tiers of the city's main avenues become expressways. The BSP tree ranks every " +
                    "street by the split that made it: level 1 is the avenue that crosses the whole city, level 2 " +
                    "adds the avenues that cross each half, and so on. 0 = no expressways."),
                    highwayLevels, 0, HighwayPlanner.MaxLevels);
                highwayMinCorridorFraction = EditorGUILayout.Slider(new GUIContent(
                    $"Min Avenue Length ({HighwayMinCorridorLength()} cells)",
                    "Avenues shorter than this fraction of the city's side length stay ordinary streets, however " +
                    "high their level."), highwayMinCorridorFraction, 0.1f, 1f);
                highwayCorridorSpacingFraction = EditorGUILayout.Slider(new GUIContent(
                    $"Min Expressway Spacing ({HighwayCorridorSpacing()} cells)",
                    "An avenue running parallel to an expressway closer than this fraction of the side length is " +
                    "left as a street, so no two expressways run side by side. The higher-level avenue wins."),
                    highwayCorridorSpacingFraction, 0f, 0.5f);
                highwayRampSpacing = Mathf.Max(2, EditorGUILayout.IntField(new GUIContent(
                    "Ramp Spacing (cells)",
                    "How far apart the on/off ramps along an expressway are, at least. A ramp branches off the deck " +
                    "and comes down onto a cross street. Lower = better served blocks, busier deck."),
                    highwayRampSpacing));
                highwayRampLength = EditorGUILayout.IntSlider(new GUIContent(
                    "Ramp Length (cells)",
                    "1 = the steep one-cell road-slant-high. 2 = the eased two-cell road-slant-curve, which " +
                    "looks far better but needs two straight street cells with no cross street."),
                    highwayRampLength, HighwayPlanner.MinRampLength, HighwayPlanner.MaxRampLength);
                using (new EditorGUI.DisabledScope(!perimeterRoad))
                {
                    highwayRingExpressway = EditorGUILayout.Toggle(new GUIContent(
                        "Ring Expressway",
                        "Deck over the perimeter ring road too, so the avenue expressways meet it in interchanges " +
                        "instead of coming down before the edge. Needs Perimeter Ring Road."), highwayRingExpressway);
                }
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

        private void DrawBuildingSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Buildings", EditorStyles.boldLabel);
            useBuildings = EditorGUILayout.Toggle(new GUIContent(
                "Buildings",
                "Fill the blocks with buildings from the Building Tiles, painted on their own Buildings Tilemap " +
                "layer. Every building fronts a street and faces it (none stand in a block's middle), packed with the " +
                "footprints that cover the most of the block; the tallest stand downtown and along the main " +
                "avenues, the lowest on the outskirts."), useBuildings);
            using (new EditorGUI.DisabledScope(!useBuildings))
            {
                List<BuildingTile> tiles = GetBuildingTiles();
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel(new GUIContent(
                        "Building Tiles",
                        $"Every Building Tile in {BuildingTileBuilder.TileFolder}. Create/Rebuild makes one per model in " +
                        $"{BuildingTileBuilder.ModelFolder}, plus deeper and wider size variants of it, keeping the footprint, tier and weight you edited on existing ones."));
                    EditorGUILayout.LabelField(DescribeBuildingTiles(tiles), EditorStyles.miniLabel);
                    if (GUILayout.Button(tiles.Count == 0 ? "Create" : "Rebuild", GUILayout.Width(60)))
                    {
                        CreateBuildingTiles(false);
                    }
                    using (new EditorGUI.DisabledScope(tiles.Count == 0))
                    {
                        if (GUILayout.Button(new GUIContent(
                                "Refit",
                                "Rebuild, and choose every tile's footprint, fitted scale and tier again from its model - " +
                                "discarding footprint and tier edits (weights are kept)."), GUILayout.Width(50)))
                        {
                            CreateBuildingTiles(true);
                        }
                    }
                }
                buildingDensity = EditorGUILayout.Slider(new GUIContent(
                    "Density",
                    "The chance a lot on an ordinary street is built. The rest stay open lots. Downtown is always " +
                    "built solid."), buildingDensity, 0f, 1f);
                buildingDowntown = EditorGUILayout.Slider(new GUIContent(
                    "Downtown Size",
                    "How much of the city counts as downtown. Land value comes from how central a block is, how " +
                    "important its avenues are and how near an expressway ramp it is; this bends it. Higher = a " +
                    "taller skyline over more of the city."), buildingDowntown, 0f, 1f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    buildingSeed = EditorGUILayout.IntField(new GUIContent(
                        "Building Seed",
                        "Same city + same seed = same buildings. Separate from the street seed, so you can reshuffle " +
                        "the buildings without moving a road."), buildingSeed);
                    if (GUILayout.Button("Reroll", GUILayout.Width(70)))
                    {
                        buildingSeed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
                        GUI.changed = true;
                    }
                }
                buildingTilemap = (Tilemap)EditorGUILayout.ObjectField(new GUIContent(
                    "Building Tilemap",
                    "The Tilemap layer the buildings are painted on - a sibling of the street Tilemap in the same Grid. " +
                    "Leave empty to have Generate find or create one called 'Buildings'."),
                    buildingTilemap, typeof(Tilemap), true);
            }
        }

        private static string DescribeBuildingTiles(List<BuildingTile> tiles)
        {
            if (tiles.Count == 0)
            {
                return "none yet - click Create";
            }
            var counts = new int[Enum.GetValues(typeof(BuildingTier)).Length];
            foreach (BuildingTile tile in tiles)
            {
                counts[(int)tile.tier]++;
            }
            return $"{tiles.Count}: {counts[0]} low, {counts[1]} mid, {counts[2]} high, {counts[3]} tower, {counts[4]} filler";
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
            return contents.RoadTiles + contents.HighwayTiles + contents.BuildingTiles + contents.Pieces;
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
                text += highways.Corridors.Count > 0
                    ? $", {highways.Corridors.Count} expressway(s) ({highways.DeckCells.Count} deck cells, " +
                      $"{highways.Ramps.Count} ramps)"
                    : ", no expressways";
            }
            if (useBuildings)
            {
                BuildingPlanner.Plan buildings = GetBuildingPlan();
                text += buildings.IsEmpty ? ", no buildings" : $", {buildings.Placements.Count} building(s)";
            }
            return text + ".";
        }

        /// <summary>
        /// The live layout's BSP tree, from the whole city down to its blocks. Generate stores the same
        /// tree in the scene (a CityBspTree beside the Tilemap), where its Inspector shows it again.
        /// </summary>
        private void DrawBspTreeSection()
        {
            EditorGUILayout.Space();
            showBspTree = EditorGUILayout.Foldout(showBspTree, new GUIContent(
                "BSP Tree",
                "Every region the city is split into, as a tree: each split region lists its two halves, down to " +
                "the blocks. Select a row to highlight it in the Scene view; double-click to frame it. Generate " +
                "stores this tree in the scene next to the Tilemap."), true, EditorStyles.foldoutHeader);
            if (!showBspTree)
            {
                return;
            }
            CityBspLayout.Layout layout = GetLayout();
            HighwayPlanner.Plan highways = GetHighwayPlan();
            if (cachedTreeNodes == null || cachedTreeLayout != layout || cachedTreeHighways != highways)
            {
                cachedTreeLayout = layout;
                cachedTreeHighways = highways;
                cachedTreeNodes = CityBspTreePanel.ToTreeNodes(layout, GetHighwayPlan().ExpresswayNodes);
            }
            bspTreePanel.OnGUI(cachedTreeNodes, node => CityBspTreePanel.FrameInSceneView(tilemap, originCell, node.rect));
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
                showCoverage = EditorGUILayout.ToggleLeft(new GUIContent(
                    "Show Coverage", "Also shade each block by how far it is, along the streets, from the nearest " +
                    "expressway ramp: green = next to one, red = the farthest block."),
                    showCoverage, GUILayout.Width(120));
                showBuildings = EditorGUILayout.ToggleLeft(new GUIContent(
                    "Show Buildings", "Also draw every building as a box coloured and sized by its tier, with a white " +
                    "line along its front."), showBuildings, GUILayout.Width(120));
            }
            EditorGUILayout.HelpBox(
                "Scene view: white = city boundary, green = blocks, gray = straight, yellow = bend, " +
                "orange = T-junction, cyan = 4-way, red = dead-end, purple = 3x3 roundabout, " +
                "blue = expressway deck, pink = ramp, green-to-red block shading = distance to the nearest ramp, " +
                "building boxes: green = low, yellow = mid, orange = high, red = tower, gray = filler.",
                MessageType.None);
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
            if (useHighways)
            {
                int endRamps = 0;
                foreach (HighwayPlanner.Ramp ramp in highways.Ramps)
                {
                    if (ramp.Kind == HighwayPlanner.RampKind.End) endRamps++;
                }
                message += $"\nExpressways: {highways.Corridors.Count} of {highways.CandidateCorridors} candidate avenue(s), " +
                           $"{highways.DeckCells.Count} deck cell(s), {highways.Interchanges} interchange(s), " +
                           $"{highways.Ramps.Count} ramp(s) ({endRamps} at ends, {highways.Ramps.Count - endRamps} along the way)";
                if (highways.MaxBlockDistance >= 0)
                {
                    message += $"\nBlock to nearest ramp: {highways.MeanBlockDistance:0.#} cells on average, " +
                               $"{highways.MaxBlockDistance} at most (along the streets)";
                }
            }
            if (useBuildings)
            {
                BuildingPlanner.Plan buildings = GetBuildingPlan();
                if (!buildings.IsEmpty)
                {
                    int[] tiers = buildings.TierCounts;
                    message += $"\nBuildings: {buildings.Placements.Count} (low {tiers[0]}, mid {tiers[1]}, high {tiers[2]}, " +
                               $"tower {tiers[3]}), covering {100f * buildings.BuiltCells / Mathf.Max(1, buildings.BlockCells):0}% " +
                               $"of the block land, {buildings.OpenLots} open lot(s)";
                }
            }
            EditorGUILayout.HelpBox(message, MessageType.None);

            if (useBuildings && !CanPlaceBuildings(out string buildingProblem))
            {
                EditorGUILayout.HelpBox("Buildings are off: " + buildingProblem, MessageType.Info);
            }
            else if (useBuildings && GetBuildingPlan().UnfilledFrontage > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{GetBuildingPlan().UnfilledFrontage} street-front cell(s) have no building that fits (the block is " +
                    "too shallow or the gap too narrow for every tile of that height). Add smaller Building Tiles, or " +
                    "raise Min Block Size.", MessageType.None);
            }

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
                if (highways.Corridors.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        highwayLevels == 0
                            ? "No expressways: Avenue Levels is 0."
                            : highways.CandidateCorridors == 0
                                ? $"No expressways: no avenue in the top {highwayLevels} level(s) is {HighwayMinCorridorLength()} " +
                                  "cells long. Lower Min Avenue Length, or raise Avenue Levels."
                                : "No expressways: every candidate avenue was too close to another or had no room for a ramp.",
                        MessageType.Info);
                }
                if (highways.TooCloseCorridors > 0)
                {
                    EditorGUILayout.HelpBox(
                        $"{highways.TooCloseCorridors} avenue(s) left as streets: closer than {HighwayCorridorSpacing()} cells " +
                        "to a parallel expressway. Lower Min Expressway Spacing to keep them.", MessageType.None);
                }
                if (highways.UnrampableCorridors > 0)
                {
                    EditorGUILayout.HelpBox(
                        $"{highways.UnrampableCorridors} avenue(s) left as streets: a free end had no {highwayRampLength} " +
                        "straight street cell(s) without a cross street for its ramp. Lower Ramp Length, or turn on Ring " +
                        "Expressway so the avenues meet the ring instead.", MessageType.None);
                }
                if (highwayTile != null && !(highwayTile is HighwayRuleTile))
                {
                    EditorGUILayout.HelpBox(
                        "Highway Tile is not a Highway Rule Tile, so the painted cells won't turn into bridges, " +
                        "elevated junctions and ramps by themselves. Click Rebuild to generate the proper tile.",
                        MessageType.Warning);
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

        /// <summary>
        /// The roundabouts for the current layout - empty when they can't be placed. Shared by preview and
        /// Generate. Planned after the expressways: a big crossing under the deck is a flyover crossing, so
        /// only crossings the deck and ramps stay clear of can become roundabouts.
        /// </summary>
        private List<RoundaboutPlanner.Placement> GetRoundaboutPlan()
        {
            CityBspLayout.Layout layout = GetLayout();
            HighwayPlanner.Plan highways = GetHighwayPlan();
            int minLength = CanPlaceRoundabouts(out _) ? RoundaboutMinCrossingLength() : 0;
            int spacing = RoundaboutMinSpacing();
            if (cachedRoundabouts == null || cachedRoundaboutLayout != layout || cachedRoundaboutHighways != highways ||
                cachedRoundaboutMinLength != minLength || cachedRoundaboutSpacing != spacing)
            {
                cachedRoundaboutLayout = layout;
                cachedRoundaboutHighways = highways;
                cachedRoundaboutMinLength = minLength;
                cachedRoundaboutSpacing = spacing;
                HashSet<Vector2Int> elevated = null;
                if (!highways.IsEmpty)
                {
                    elevated = new HashSet<Vector2Int>(highways.DeckCells);
                    elevated.UnionWith(highways.RampCells);
                }
                cachedRoundabouts = minLength > 0
                    ? RoundaboutPlanner.Plan(layout.RoadCells, layout.RoadCellsSorted, minLength, spacing, elevated)
                    : new List<RoundaboutPlanner.Placement>();
                cachedRoundaboutRoadCells = new HashSet<Vector2Int>();
                foreach (RoundaboutPlanner.Placement roundabout in cachedRoundabouts)
                {
                    cachedRoundaboutRoadCells.UnionWith(roundabout.RoadCells);
                }
            }
            return cachedRoundabouts;
        }

        /// <summary>Min Avenue Length as a cell count for the current Side Length.</summary>
        private int HighwayMinCorridorLength()
        {
            return Mathf.Max(1, Mathf.CeilToInt(highwayMinCorridorFraction * sideLength - 0.0001f));
        }

        /// <summary>Min Expressway Spacing as a cell count for the current Side Length.</summary>
        private int HighwayCorridorSpacing()
        {
            return Mathf.Max(0, Mathf.CeilToInt(highwayCorridorSpacingFraction * sideLength - 0.0001f));
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
            if (!(coverTile is RoadCoverTile) || !(roadTile is RoadRuleTile))
            {
                reason = "every ramp lies over a street, which is painted with the Cover Tile underneath so the street " +
                         "stays connected - assign a Road Cover Tile (Variety section, Create) and a RoadRuleTile as Road Tile.";
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
        /// The expressway plan for the current layout - empty when highways are off or can't be painted.
        /// Shared by the preview and Generate, like the layout and roundabouts, so the Scene view is never
        /// showing something other than what will be built.
        /// </summary>
        private HighwayPlanner.Plan GetHighwayPlan()
        {
            CityBspLayout.Layout layout = GetLayout();
            var settings = new HighwayPlanner.Settings(
                CanPlaceHighways(out _) ? highwayLevels : 0,
                HighwayMinCorridorLength(), HighwayCorridorSpacing(), highwayRampSpacing, highwayRampLength,
                highwayRingExpressway && perimeterRoad);
            if (cachedHighwayPlan == null || cachedHighwayLayout != layout || !cachedHighwaySettings.Equals(settings))
            {
                cachedHighwayLayout = layout;
                cachedHighwaySettings = settings;
                cachedHighwayPlan = HighwayPlanner.Build(layout, settings);
            }
            return cachedHighwayPlan;
        }

        private List<BuildingTile> GetBuildingTiles()
        {
            if (buildingTiles == null)
            {
                buildingTiles = BuildingTileBuilder.LoadTiles();
                buildingTiles.RemoveAll(tile => tile == null || tile.piece == null);
            }
            return buildingTiles;
        }

        private void CreateBuildingTiles(bool refit)
        {
            List<BuildingTile> tiles = BuildingTileBuilder.CreateOrUpdateTiles(refit, out string error);
            if (tiles == null)
            {
                Debug.LogError($"[{LogPrefix}] Couldn't create the Building Tiles: {error}");
                return;
            }
            buildingTiles = null;
            buildingCoverTile = null;
            Tilemap layer = FindBuildingTilemap();
            if (layer != null)
            {
                layer.RefreshAllTiles();
            }
            Repaint();
            SceneView.RepaintAll();
        }

        /// <summary>Whether buildings can be placed with the current fields; says why not otherwise.</summary>
        private bool CanPlaceBuildings(out string reason)
        {
            if (!useBuildings)
            {
                reason = "Buildings is unticked.";
                return false;
            }
            if (GetBuildingTiles().Count == 0)
            {
                reason = "there are no Building Tiles yet - click Create next to Building Tiles.";
                return false;
            }
            if (buildingTilemap != null && (buildingTilemap == tilemap || buildingTilemap == FindHighwayTilemap()))
            {
                reason = "Building Tilemap is the street or highway Tilemap - buildings need their own layer. Clear the " +
                         "field to have one created.";
                return false;
            }
            if (buildingTilemap == null && tilemap != null && tilemap.transform.parent == null)
            {
                reason = "the street Tilemap has no parent Grid to add a Buildings layer to - assign a Building Tilemap.";
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// The buildings for the current layout - empty when buildings are off or can't be placed. Built on
        /// the layout, the expressway plan (ramp access raises land value) and the roundabouts (the corners
        /// of their 3x3 footprint are block cells nothing may stand on). Shared by the preview and Generate.
        /// </summary>
        private BuildingPlanner.Plan GetBuildingPlan()
        {
            CityBspLayout.Layout layout = GetLayout();
            HighwayPlanner.Plan highways = GetHighwayPlan();
            List<RoundaboutPlanner.Placement> roundabouts = GetRoundaboutPlan();
            List<BuildingTile> tiles = CanPlaceBuildings(out _) ? GetBuildingTiles() : new List<BuildingTile>();
            var settings = new BuildingPlanner.Settings(buildingSeed, buildingDensity, buildingDowntown);
            if (cachedBuildingPlan == null || cachedBuildingLayout != layout || cachedBuildingHighways != highways ||
                !ReferenceEquals(cachedBuildingRoundabouts, roundabouts) || cachedBuildingTilesSignature != TilesSignature(tiles) ||
                !cachedBuildingSettings.Equals(settings))
            {
                var reserved = new HashSet<Vector2Int>();
                foreach (RoundaboutPlanner.Placement roundabout in roundabouts)
                {
                    foreach (Vector2Int cell in roundabout.Footprint.allPositionsWithin)
                    {
                        if (!layout.RoadCells.Contains(cell))
                        {
                            reserved.Add(cell);
                        }
                    }
                }
                var catalog = new List<BuildingPlanner.Entry>(tiles.Count);
                for (int i = 0; i < tiles.Count; i++)
                {
                    catalog.Add(new BuildingPlanner.Entry(i, tiles[i].footprint, tiles[i].tier, tiles[i].weight));
                }
                cachedBuildingLayout = layout;
                cachedBuildingHighways = highways;
                cachedBuildingRoundabouts = roundabouts;
                cachedBuildingTilesSignature = TilesSignature(tiles);
                cachedBuildingSettings = settings;
                cachedBuildingPlan = BuildingPlanner.Build(layout, highways.IsEmpty ? null : highways, reserved, catalog, settings);
            }
            return cachedBuildingPlan;
        }

        /// <summary>Changes whenever a tile is added, removed, reordered or has its footprint, tier or weight edited.</summary>
        private static int TilesSignature(List<BuildingTile> tiles)
        {
            int hash = 17;
            foreach (BuildingTile tile in tiles)
            {
                hash = HashCode.Combine(hash, tile.GetInstanceID(), tile.footprint, tile.tier, tile.weight);
            }
            return hash;
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
            SaveState();
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
            buildingTilemap = CreateLayerTilemap(gridGo.transform, BuildingTilemapName, 2, CreateTilemapUndoLabel);
            SaveState();
            Selection.activeGameObject = tilemapGo;
            Repaint();
            SceneView.RepaintAll();
            Debug.Log($"[{LogPrefix}] Created '{gridGo.name}/{tilemapGo.name}' and '{gridGo.name}/{highwayTilemap.name}' " +
                      $"(Cell Swizzle XZY, cell size {newTilemapCellSize:0.###}).");
        }

        private static Tilemap CreateHighwayTilemap(Transform grid, string undoLabel)
        {
            return CreateLayerTilemap(grid, HighwayTilemapName, 1, undoLabel);
        }

        private static Tilemap CreateLayerTilemap(Transform grid, string name, int sortingOrder, string undoLabel)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, undoLabel);
            go.transform.SetParent(grid, false);
            Tilemap tm = go.AddComponent<Tilemap>();
            TilemapRenderer renderer = go.AddComponent<TilemapRenderer>();
            renderer.sortingOrder = sortingOrder;
            if (name == BuildingTilemapName)
            {
                go.AddComponent<BuildingTilemap>();
            }
            return tm;
        }

        /// <summary>
        /// The buildings' own Tilemap layer if there is one: the assigned one, else a sibling of the street
        /// Tilemap called 'Buildings'. Never the street or highway Tilemap. Creates nothing.
        /// </summary>
        private Tilemap FindBuildingTilemap()
        {
            if (tilemap == null)
            {
                return null;
            }
            Tilemap highwayLayer = FindHighwayTilemap();
            if (buildingTilemap != null)
            {
                return buildingTilemap != tilemap && buildingTilemap != highwayLayer ? buildingTilemap : null;
            }
            Transform grid = tilemap.transform.parent;
            Transform existing = grid != null ? grid.Find(BuildingTilemapName) : null;
            Tilemap found = existing != null ? existing.GetComponent<Tilemap>() : null;
            return found != tilemap && found != highwayLayer ? found : null;
        }

        /// <summary><see cref="FindBuildingTilemap"/>, or a new 'Buildings' Tilemap in the street Tilemap's Grid.</summary>
        private Tilemap GetOrCreateBuildingTilemap(string undoLabel)
        {
            Tilemap layer = FindBuildingTilemap();
            if (layer == null && tilemap.transform.parent != null)
            {
                layer = CreateLayerTilemap(tilemap.transform.parent, BuildingTilemapName, 2, undoLabel);
            }
            buildingTilemap = layer;
            return layer;
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
            SaveState();
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
            SaveState();
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
            Tilemap buildingLayer = FindBuildingTilemap();
            BoundsInt area = CityArea();
            if (contentsDirty || contentsTilemap != tilemap || contentsHighwayTilemap != highwayLayer ||
                contentsBuildingTilemap != buildingLayer || contentsArea != area)
            {
                contentsTilemap = tilemap;
                contentsHighwayTilemap = highwayLayer;
                contentsBuildingTilemap = buildingLayer;
                contentsArea = area;
                cachedContents = new CityAreaContents(
                    CountTiles(tilemap, area),
                    highwayLayer != null ? CountTiles(highwayLayer, area) : 0,
                    buildingLayer != null ? CountTiles(buildingLayer, area) : 0,
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
        private CityAreaContents ClearCityArea(Tilemap highwayLayer, Tilemap buildingLayer)
        {
            BoundsInt area = CityArea();
            int roadTiles = ClearTiles(tilemap, area);
            int highwayTiles = highwayLayer != null ? ClearTiles(highwayLayer, area) : 0;
            int buildingTiles = buildingLayer != null ? ClearTiles(buildingLayer, area) : 0;
            List<MultiCellRoadPiece> pieces = FindPiecesInCityArea(area);
            foreach (MultiCellRoadPiece piece in pieces)
            {
                Undo.DestroyObjectImmediate(piece.gameObject);
            }
            RemoveStoredTrees(tree => tree.Overlaps(area));
            return new CityAreaContents(roadTiles, highwayTiles, buildingTiles, pieces.Count);
        }

        /// <summary>Destroys (undoably) this Tilemap's stored BSP trees that <paramref name="match"/> accepts.</summary>
        private void RemoveStoredTrees(Predicate<CityBspTree> match)
        {
            foreach (CityBspTree tree in FindObjectsByType<CityBspTree>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (tree.tilemap == tilemap && match(tree))
                {
                    Undo.DestroyObjectImmediate(tree.gameObject);
                }
            }
        }

        /// <summary>
        /// Stores the generated layout's BSP tree in the scene, on a GameObject beside the Tilemap in its
        /// Grid, replacing the tree of any earlier city on exactly this square. Inside Generate's undo group.
        /// </summary>
        private CityBspTree StoreTree(CityBspLayout.Layout layout, HighwayPlanner.Plan highways)
        {
            BoundsInt area = CityArea();
            RemoveStoredTrees(tree => tree.CityArea == area);

            var go = new GameObject($"{tilemap.name} BSP Tree ({originCell.x}, {originCell.y})");
            Undo.RegisterCreatedObjectUndo(go, UndoLabel);
            Transform parent = tilemap.transform.parent;
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }
            else
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, tilemap.gameObject.scene);
            }
            CityBspTree stored = go.AddComponent<CityBspTree>();
            stored.tilemap = tilemap;
            stored.originCell = originCell;
            stored.sideLength = layout.SideLength;
            stored.seed = randomSeed;
            stored.nodes = CityBspTreePanel.ToTreeNodes(layout, highways.ExpresswayNodes);
            return stored;
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
            Tilemap highwayLayer = !highways.IsEmpty ? GetOrCreateHighwayTilemap(UndoLabel) : FindHighwayTilemap();
            if (highwayLayer != null)
            {
                Undo.RegisterCompleteObjectUndo(highwayLayer, UndoLabel);
            }
            BuildingPlanner.Plan buildings = GetBuildingPlan();
            Tilemap buildingLayer = !buildings.IsEmpty ? GetOrCreateBuildingTilemap(UndoLabel) : FindBuildingTilemap();
            if (buildingLayer != null)
            {
                Undo.RegisterCompleteObjectUndo(buildingLayer, UndoLabel);
            }

            CityAreaContents cleared = clearCityAreaFirst ? ClearCityArea(highwayLayer, buildingLayer) : default;
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
            CityBspTree storedTree = StoreTree(layout, highways);
            HighwayBuilder.Result highwayResult = !highways.IsEmpty
                ? HighwayBuilder.Paint(highways, highwayLayer, originCell, highwayTile, coverTile)
                : default;

            int buildingCount = !buildings.IsEmpty ? PaintBuildings(buildings, buildingLayer) : 0;

            Undo.CollapseUndoOperations(undoGroup);
            contentsDirty = true;
            SaveState(); // Generate may have created and assigned the Highways and Buildings layers

            RoadGridPlacer.CountByCategory(layout.RoadCells, cachedGraph, out int[] counts, out _);
            Debug.Log($"[{LogPrefix}] Painted {layout.RoadCellsSorted.Count} road cell(s) onto '{tilemap.name}' for a " +
                      $"{layout.SideLength}x{layout.SideLength} city at cell {originCell} ({layout.Cuts.Count} cut(s), " +
                      $"{layout.Blocks.Count} block(s), T-junction {counts[(int)RoadGridPlacer.ShapeCategory.TJunction]}, " +
                      $"4-way {counts[(int)RoadGridPlacer.ShapeCategory.FourWay] - roundabouts.Count}, roundabout {roundabouts.Count}, " +
                      $"seed {randomSeed}" + (cleared.IsEmpty ? "" : $"; replaced {cleared.Describe()}") + "). " +
                      $"BSP tree of {storedTree.nodes.Count} region(s) stored on '{storedTree.name}'.", storedTree);
            if (!highways.IsEmpty)
            {
                Debug.Log($"[{LogPrefix}] Painted {highways.Corridors.Count} expressway(s) onto '{highwayLayer.name}': " +
                          $"{highwayResult.DeckTiles} deck tile(s), {highways.Interchanges} interchange(s), " +
                          $"{highways.Ramps.Count} ramp(s) in {highwayResult.RampTiles} tile(s), " +
                          $"{interchanges} street cover tile(s) under the ramps; farthest block is " +
                          $"{highways.MaxBlockDistance} cell(s) from a ramp.");
            }
            if (buildingCount > 0)
            {
                int[] tiers = buildings.TierCounts;
                Debug.Log($"[{LogPrefix}] Painted {buildingCount} building(s) onto '{buildingLayer.name}': low {tiers[0]}, " +
                          $"mid {tiers[1]}, high {tiers[2]}, tower {tiers[3]}, filler {tiers[4]}; " +
                          $"{100f * buildings.BuiltCells / Mathf.Max(1, buildings.BlockCells):0}% of the block land built, " +
                          $"{buildings.OpenLots} open lot(s), building seed {buildingSeed}.");
            }
            ShowNotification(new GUIContent(
                $"{(cleared.IsEmpty ? "Generated" : "Regenerated")} {layout.SideLength}x{layout.SideLength} city" +
                (!highways.IsEmpty ? $"\n{highways.Corridors.Count} expressway(s)" : "") +
                (buildingCount > 0 ? $"\n{buildingCount} building(s)" : "")), 1.5);
        }

        /// <summary>
        /// Paints every building in one batch: its Building Tile on the footprint's south-west cell, turned by
        /// the cell's transform matrix to face its street, and the Building Cover Tile on the rest of the
        /// footprint. Returns how many buildings were painted.
        /// </summary>
        private int PaintBuildings(BuildingPlanner.Plan buildings, Tilemap layer)
        {
            if (layer == null)
            {
                return 0;
            }
            if (layer.GetComponent<BuildingTilemap>() == null)
            {
                Undo.AddComponent<BuildingTilemap>(layer.gameObject);
            }
            if (buildingCoverTile == null)
            {
                buildingCoverTile = BuildingTileBuilder.LoadOrCreateCoverTile();
            }
            List<BuildingTile> tiles = GetBuildingTiles();
            var changes = new List<TileChangeData>();
            int count = 0;
            foreach (BuildingPlanner.Placement placement in buildings.Placements)
            {
                if (placement.Id < 0 || placement.Id >= tiles.Count)
                {
                    continue;
                }
                foreach (Vector2Int cell in placement.Cells)
                {
                    bool anchor = cell == placement.Anchor;
                    changes.Add(new TileChangeData(
                        ToTilemapCell(cell),
                        anchor ? tiles[placement.Id] : buildingCoverTile,
                        Color.white,
                        anchor ? BuildingTile.RotationMatrix(placement.Turns) : Matrix4x4.identity));
                }
                count++;
            }
            layer.SetTiles(changes.ToArray(), true);
            // Each piece was spawned before its cell got its rotation; refreshing the anchor re-places it
            // (see BuildingTile.GetTileData).
            foreach (BuildingPlanner.Placement placement in buildings.Placements)
            {
                layer.RefreshTile(ToTilemapCell(placement.Anchor));
            }
            EditorUtility.SetDirty(layer);
            return count;
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
        /// Every ramp lies over a street, so its cells get the Cover Tile on the street layer: it shows
        /// nothing, leaving the ramp piece as the road surface there, but still reads as road so the
        /// street stays connected to the pieces around it. Returns how many were painted.
        /// </summary>
        private int PaintRampInterchanges(HighwayPlanner.Plan highways)
        {
            if (highways.RampCellsSorted.Count == 0)
            {
                return 0;
            }
            var positions = new Vector3Int[highways.RampCellsSorted.Count];
            var tiles = new TileBase[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = ToTilemapCell(highways.RampCellsSorted[i]);
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
            Tilemap buildingLayer = FindBuildingTilemap();
            if (buildingLayer != null)
            {
                Undo.RegisterCompleteObjectUndo(buildingLayer, ClearUndoLabel);
            }
            CityAreaContents cleared = ClearCityArea(highwayLayer, buildingLayer);
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

            if (showCoverage && useHighways && highways.MaxBlockDistance >= 0)
            {
                // Green next to a ramp, through yellow, to red for the farthest block in this city.
                float farthest = Mathf.Max(1, highways.MaxBlockDistance);
                for (int i = 0; i < layout.Blocks.Count; i++)
                {
                    int distance = highways.BlockDistances[i];
                    Color color = distance < 0 ? Color.gray : Color.Lerp(CoverageNearColor, CoverageFarColor, distance / farthest);
                    Handles.DrawSolidRectangleWithOutline(CellRectCorners(layout.Blocks[i], lift * 1.5f),
                        new Color(color.r, color.g, color.b, 0.35f), Color.clear);
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

            if (showBuildings && useBuildings)
            {
                DrawBuildingPreview(GetBuildingPlan());
            }

            if (showBspTree)
            {
                bspTreePanel.DrawSceneSelection(tilemap, originCell);
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
        /// Each building as a wireframe box coloured and sized by tier, with a white line along its front, so
        /// the skyline and which way the facades face can be judged before painting.
        /// </summary>
        private void DrawBuildingPreview(BuildingPlanner.Plan buildings)
        {
            const float inset = 0.08f;
            foreach (BuildingPlanner.Placement placement in buildings.Placements)
            {
                Color color = BuildingTierColors[(int)placement.Tier];
                float height = BuildingTierPreviewHeights[(int)placement.Tier];
                Vector3[] floor = CellRectCorners(placement.Rect, Vector3.up * 0.06f);
                Vector3 center = (floor[0] + floor[2]) * 0.5f;
                for (int i = 0; i < 4; i++)
                {
                    floor[i] = Vector3.MoveTowards(floor[i], new Vector3(center.x, floor[i].y, center.z), inset);
                }
                var roof = new Vector3[4];
                for (int i = 0; i < 4; i++)
                {
                    roof[i] = floor[i] + Vector3.up * height;
                }
                Handles.DrawSolidRectangleWithOutline(roof, new Color(color.r, color.g, color.b, 0.35f), color);
                Handles.color = color;
                for (int i = 0; i < 4; i++)
                {
                    Handles.DrawLine(floor[i], roof[i]);
                }
                // Corners run SW, SE, NE, NW, so edge k (corner k to k+1) is the south, east, north, west
                // side in turn - the same order as the placement's quarter turns.
                int a = placement.Turns;
                int b = (a + 1) % 4;
                Handles.color = Color.white;
                Handles.DrawAAPolyLine(3f, floor[a] + Vector3.up * 0.02f, floor[b] + Vector3.up * 0.02f);
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
