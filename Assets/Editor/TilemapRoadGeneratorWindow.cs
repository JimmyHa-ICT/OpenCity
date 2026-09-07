using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Editor window that reads the occupied cells straight off a painted Tilemap and
    /// places modular road prefabs by the same connectivity "grammar" as
    /// RoadGeneratorWindow: each occupied cell's set of occupied N/E/S/W neighbors picks a
    /// shape category (dead-end / straight / bend / T-junction / 4-way) and a 90-degree
    /// rotation. Unlike the spline tool, there is no rasterization step - the painted
    /// cells *are* the occupied set, so there's no sampling/rounding/staircase-bridging
    /// approximation to tune. The grammar/placement engine lives in RoadGridPlacer,
    /// shared with RoadGeneratorWindow. See Docs/TilemapRoadGenerator.md.
    /// </summary>
    public class TilemapRoadGeneratorWindow : EditorWindow
    {
        private const string UndoLabel = "Generate Road (Tilemap)";
        private const string CreateTilemapUndoLabel = "Create Road Tilemap";

        [SerializeField] private Tilemap tilemap;

        [SerializeField] private float cellSizeOverride;
        [SerializeField] private float groundY;

        [SerializeField] private RoadGridPlacer.RoadShapeConfig deadEnd = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig straight = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig bend = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig tJunction = new RoadGridPlacer.RoadShapeConfig();
        [SerializeField] private RoadGridPlacer.RoadShapeConfig fourWay = new RoadGridPlacer.RoadShapeConfig();

        [SerializeField] private string parentName = "Road";
        [SerializeField] private Transform parentUnder;

        [MenuItem("Tools/OpenCity/Road Generator (Tilemap)")]
        private static void Open()
        {
            TilemapRoadGeneratorWindow window = GetWindow<TilemapRoadGeneratorWindow>("Road Generator (Tilemap)");
            window.minSize = new Vector2(360, 520);
        }

        private void OnEnable()
        {
            RoadGridPlacer.LoadDefaults(deadEnd, straight, bend, tJunction, fourWay);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Tilemap", EditorStyles.boldLabel);
            tilemap = (Tilemap)EditorGUILayout.ObjectField(
                "Tilemap", tilemap, typeof(Tilemap), true);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Use Selected GameObject", GUILayout.Width(190)))
                {
                    UseSelection();
                }
                if (GUILayout.Button("Create Road Tilemap", GUILayout.Width(150)))
                {
                    CreateRoadTilemap();
                }
            }
            EditorGUILayout.HelpBox(
                "Paint the road layout with the Tile Palette (Window > 2D > Tile Palette) using " +
                "any placeholder Tile - only whether a cell has a tile matters, not which one.",
                MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Grid", EditorStyles.boldLabel);
            cellSizeOverride = EditorGUILayout.FloatField(new GUIContent(
                "Cell Size Override",
                "0 = auto-detect from the Straight prefab's renderer bounds along X. Used to " +
                "validate the Tilemap's own cell size and to size a newly created Tilemap."), cellSizeOverride);
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

            List<Vector2Int> occupied = GetOccupiedCells();
            Dictionary<Vector2Int, RoadGridPlacer.Direction> graph = RoadGridPlacer.BuildGraph(occupied);
            RoadGridPlacer.CountByCategory(occupied, graph, out int[] counts, out int skipped);

            string message =
                $"Cells: {occupied.Count}  (dead-end {counts[(int)RoadGridPlacer.ShapeCategory.DeadEnd]}, " +
                $"straight {counts[(int)RoadGridPlacer.ShapeCategory.Straight]}, bend {counts[(int)RoadGridPlacer.ShapeCategory.Bend]}, " +
                $"T-junction {counts[(int)RoadGridPlacer.ShapeCategory.TJunction]}, 4-way {counts[(int)RoadGridPlacer.ShapeCategory.FourWay]})";
            if (skipped > 0)
            {
                message += $"\n{skipped} cell(s) could not be classified and will be skipped.";
            }
            EditorGUILayout.HelpBox(message, MessageType.None);

            GridLayout.CellSwizzle swizzle = tilemap.layoutGrid != null
                ? tilemap.layoutGrid.cellSwizzle
                : GridLayout.CellSwizzle.XYZ;
            if (swizzle != GridLayout.CellSwizzle.XZY)
            {
                EditorGUILayout.HelpBox(
                    "This Tilemap's Grid has Cell Swizzle = " + swizzle + ", not XZY - it isn't lying " +
                    "flat on the world XZ ground plane, so placement will not land where you painted. " +
                    "Use 'Create Road Tilemap' or set the Grid's Cell Swizzle to XZY yourself.",
                    MessageType.Warning);
            }

            float detectedSize = RoadGridPlacer.ResolveCellSize(cellSizeOverride, straight.prefab);
            if (tilemap.layoutGrid != null && detectedSize > 0.0001f)
            {
                Vector3 gridCellSize = tilemap.layoutGrid.cellSize;
                if (Mathf.Abs(gridCellSize.x - detectedSize) > 0.001f || Mathf.Abs(gridCellSize.y - detectedSize) > 0.001f)
                {
                    EditorGUILayout.HelpBox(
                        $"This Tilemap's Grid cell size ({gridCellSize.x:F3} x {gridCellSize.y:F3}) doesn't match " +
                        $"the road prefab footprint ({detectedSize:F3}) - placed prefabs may overlap or gap.",
                        MessageType.Warning);
                }
            }
        }

        private bool CanGenerate(out string error)
        {
            if (tilemap == null)
            {
                error = "Assign a Tilemap.";
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
                Debug.LogWarning("[TilemapRoadGenerator] Nothing selected.");
                return;
            }
            Tilemap tm = go.GetComponent<Tilemap>();
            if (tm == null)
            {
                tm = go.GetComponentInChildren<Tilemap>();
            }
            if (tm == null)
            {
                Debug.LogWarning($"[TilemapRoadGenerator] '{go.name}' has no Tilemap component.");
                return;
            }
            tilemap = tm;
            Repaint();
        }

        private void CreateRoadTilemap()
        {
            float cellSize = RoadGridPlacer.ResolveCellSize(cellSizeOverride, straight.prefab);
            if (cellSize <= 0.0001f)
            {
                Debug.LogWarning("[TilemapRoadGenerator] Could not determine cell size - assign a Straight " +
                                  "prefab or a Cell Size Override before creating a Tilemap.");
                return;
            }

            var gridGo = new GameObject("Road Tilemap Grid");
            Undo.RegisterCreatedObjectUndo(gridGo, CreateTilemapUndoLabel);
            Grid grid = gridGo.AddComponent<Grid>();
            grid.cellSwizzle = GridLayout.CellSwizzle.XZY;
            grid.cellSize = new Vector3(cellSize, cellSize, cellSize);
            gridGo.transform.position = new Vector3(0f, groundY, 0f);

            var tilemapGo = new GameObject("Tilemap");
            Undo.RegisterCreatedObjectUndo(tilemapGo, CreateTilemapUndoLabel);
            tilemapGo.transform.SetParent(gridGo.transform, false);
            Tilemap tm = tilemapGo.AddComponent<Tilemap>();
            tilemapGo.AddComponent<TilemapRenderer>();

            tilemap = tm;
            Selection.activeGameObject = tilemapGo;
            Repaint();

            Debug.Log($"[TilemapRoadGenerator] Created '{gridGo.name}/{tilemapGo.name}' with Cell Swizzle " +
                       $"XZY and cell size {cellSize:F3} m. Open Window > 2D > Tile Palette, assign a " +
                       "placeholder Tile (Assets > Create > 2D > Tiles > Tile if you don't have one), and " +
                       "paint the road layout.");
        }

        private List<Vector2Int> GetOccupiedCells()
        {
            var cells = new List<Vector2Int>();
            if (tilemap == null)
            {
                return cells;
            }
            BoundsInt bounds = tilemap.cellBounds;
            foreach (Vector3Int pos in bounds.allPositionsWithin)
            {
                if (tilemap.HasTile(pos))
                {
                    cells.Add(new Vector2Int(pos.x, pos.y));
                }
            }
            return cells;
        }

        private Vector3 CellToWorld(Vector2Int cell)
        {
            Vector3 world = tilemap.GetCellCenterWorld(new Vector3Int(cell.x, cell.y, 0));
            world.y = groundY;
            return world;
        }

        private void Generate()
        {
            if (!CanGenerate(out string error))
            {
                Debug.LogError($"[TilemapRoadGenerator] {error}");
                return;
            }

            List<Vector2Int> occupied = GetOccupiedCells();
            if (occupied.Count == 0)
            {
                Debug.LogWarning("[TilemapRoadGenerator] Tilemap has no painted cells - nothing to generate.");
                return;
            }
            Dictionary<Vector2Int, RoadGridPlacer.Direction> graph = RoadGridPlacer.BuildGraph(occupied);

            Dictionary<RoadGridPlacer.ShapeCategory, RoadGridPlacer.RoadShapeConfig> configByCategory =
                RoadGridPlacer.BuildConfigMap(deadEnd, straight, bend, tJunction, fourWay);

            RoadGridPlacer.PlaceResult result = RoadGridPlacer.PlaceCells(
                occupied, graph, CellToWorld, configByCategory,
                parentName, parentUnder, UndoLabel, "TilemapRoadGenerator");

            float cellSize = RoadGridPlacer.ResolveCellSize(cellSizeOverride, straight.prefab);
            Debug.Log($"[TilemapRoadGenerator] Generated {result.TotalCells - result.SkippedCount} tile(s) " +
                       $"(dead-end {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.DeadEnd]}, straight {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.Straight]}, " +
                       $"bend {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.Bend]}, T-junction {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.TJunction]}, " +
                       $"4-way {result.PlacedCounts[(int)RoadGridPlacer.ShapeCategory.FourWay]}, skipped {result.SkippedCount}) " +
                       $"from '{tilemap.name}' (cell size {cellSize:F3} m).");
        }

        private void Clear()
        {
            RoadGridPlacer.ClearGenerated(parentName, parentUnder, "TilemapRoadGenerator");
        }
    }
}
