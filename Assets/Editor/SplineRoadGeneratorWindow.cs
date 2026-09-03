using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Editor window that places a chain of modular road tile prefabs along a
    /// Spline (UnityEngine.Splines.SplineContainer), rotated to follow the
    /// spline's tangent at each step. Swaps in a corner prefab (snapped to the
    /// nearest 90 degrees) wherever the spline turns sharply within a tile's
    /// span, instead of only ever placing straight tiles. See
    /// Docs/SplineRoadGenerator.md for full usage notes.
    /// </summary>
    public class SplineRoadGeneratorWindow : EditorWindow
    {
        private enum Axis { PlusX, MinusX, PlusY, MinusY, PlusZ, MinusZ }

        private const string DefaultStraightPrefabPath = "Assets/Prefabs/road-straight.prefab";
        private const string DefaultCornerPrefabPath = "Assets/Prefabs/road-bend.prefab";
        private const string UndoLabel = "Generate Spline Road";

        [SerializeField] private SplineContainer splineContainer;
        [SerializeField] private int splineIndex;
        [SerializeField] private GameObject straightTilePrefab;
        [SerializeField] private Axis forwardAxis = Axis.PlusX;
        [SerializeField] private float tileLengthOverride;
        [SerializeField] private bool stretchToFit;
        [SerializeField] private bool flattenTangentY = true;

        [SerializeField] private bool showCornerSettings;
        [SerializeField] private GameObject cornerPrefab;
        [SerializeField] private float cornerThresholdDegrees = 45f;
        [SerializeField] private Axis cornerAxisA = Axis.PlusX;
        [SerializeField] private Axis cornerAxisB = Axis.PlusZ;

        [SerializeField] private string parentName = "Road";
        [SerializeField] private Transform parentUnder;
        [SerializeField] private float startOffset;
        [SerializeField] private float endOffset;

        [SerializeField] private bool showEndCaps;
        [SerializeField] private GameObject startCapPrefab;
        [SerializeField] private GameObject endCapPrefab;
        [SerializeField] private bool flipStartCap;
        [SerializeField] private bool flipEndCap;

        [MenuItem("Tools/OpenCity/Spline Road Generator")]
        private static void Open()
        {
            SplineRoadGeneratorWindow window = GetWindow<SplineRoadGeneratorWindow>("Spline Road Generator");
            window.minSize = new Vector2(360, 520);
        }

        private void OnEnable()
        {
            if (straightTilePrefab == null)
            {
                straightTilePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultStraightPrefabPath);
            }
            if (cornerPrefab == null)
            {
                cornerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultCornerPrefabPath);
            }
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
            EditorGUILayout.LabelField("Straight Tile", EditorStyles.boldLabel);
            straightTilePrefab = (GameObject)EditorGUILayout.ObjectField(
                "Straight Tile Prefab", straightTilePrefab, typeof(GameObject), false);
            forwardAxis = (Axis)EditorGUILayout.EnumPopup(new GUIContent(
                "Forward Axis",
                "The straight tile's local axis that points along the direction of travel. " +
                "For this project's road-* kit this is +X (see Docs/SplineRoadGenerator.md)."), forwardAxis);
            tileLengthOverride = EditorGUILayout.FloatField(new GUIContent(
                "Tile Length Override",
                "0 = auto-detect from the prefab's renderer bounds along Forward Axis."), tileLengthOverride);
            flattenTangentY = EditorGUILayout.Toggle(new GUIContent(
                "Flatten Elevation",
                "Ignore the spline tangent's Y component so tiles stay level. Disable for ramps/hills."), flattenTangentY);
            stretchToFit = EditorGUILayout.Toggle(new GUIContent(
                "Stretch To Fit",
                "Slightly scale each straight tile along Forward Axis so the run has no gap or overlap at " +
                "the end. Never applied to corner tiles."), stretchToFit);

            EditorGUILayout.Space();
            showCornerSettings = EditorGUILayout.Foldout(showCornerSettings, "Corner Detection (optional)");
            if (showCornerSettings)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "Whenever the spline turns more than the threshold within a single tile's span, that " +
                    "tile is replaced with the corner prefab, rotated in 90 degree steps to best match the " +
                    "incoming/outgoing directions. Leave Corner Prefab empty to always use straight tiles " +
                    "(the old behavior) - useful for gentle curves, which already read fine as rotated " +
                    "straights.", MessageType.Info);
                cornerPrefab = (GameObject)EditorGUILayout.ObjectField(
                    "Corner Prefab", cornerPrefab, typeof(GameObject), false);
                cornerThresholdDegrees = EditorGUILayout.Slider(new GUIContent(
                    "Corner Threshold (deg)",
                    "Turn angle within one tile's span above which a corner prefab replaces the straight tile."),
                    cornerThresholdDegrees, 5f, 175f);
                cornerAxisA = (Axis)EditorGUILayout.EnumPopup(new GUIContent(
                    "Corner Open Axis A",
                    "One of the corner prefab's two open sides at rotation 0 (local axis). " +
                    "Defaults match road-bend/road-curve's documented North+East opening."), cornerAxisA);
                cornerAxisB = (Axis)EditorGUILayout.EnumPopup(new GUIContent(
                    "Corner Open Axis B", "The corner prefab's other open side at rotation 0."), cornerAxisB);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Placement", EditorStyles.boldLabel);
            parentName = EditorGUILayout.TextField(new GUIContent(
                "Parent Name",
                "Name of the GameObject the tiles are generated under. Generate replaces any existing " +
                "object with this exact name (see Clear)."), parentName);
            parentUnder = (Transform)EditorGUILayout.ObjectField(new GUIContent(
                "Parent Container",
                "Optional. Where the generated root is nested. Leave empty for scene root."), parentUnder, typeof(Transform), true);
            startOffset = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent(
                "Start Offset (m)",
                "Distance to skip at the start of the spline before the first tile."), startOffset));
            endOffset = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent(
                "End Offset (m)",
                "Distance to leave uncovered at the end of the spline."), endOffset));

            EditorGUILayout.Space();
            showEndCaps = EditorGUILayout.Foldout(showEndCaps, "End Caps (optional)");
            if (showEndCaps)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "Cap orientation can't be auto-detected from geometry. Try Generate, then toggle Flip " +
                    "if the closed side faces the wrong way. See Docs/SplineRoadGenerator.md.", MessageType.Info);
                startCapPrefab = (GameObject)EditorGUILayout.ObjectField(
                    "Start Cap Prefab", startCapPrefab, typeof(GameObject), false);
                flipStartCap = EditorGUILayout.Toggle("Flip Start Cap", flipStartCap);
                endCapPrefab = (GameObject)EditorGUILayout.ObjectField(
                    "End Cap Prefab", endCapPrefab, typeof(GameObject), false);
                flipEndCap = EditorGUILayout.Toggle("Flip End Cap", flipEndCap);
                EditorGUI.indentLevel--;
            }

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
            float extent = tileLengthOverride > 0f ? tileLengthOverride : MeasureExtentAlongAxis(straightTilePrefab, forwardAxis);
            float usable = Mathf.Max(0f, totalLength - startOffset - endOffset);
            int numTiles = stretchToFit
                ? Mathf.Max(1, Mathf.RoundToInt(usable / extent))
                : Mathf.FloorToInt(usable / extent);
            float covered = stretchToFit ? usable : numTiles * extent;

            string message =
                $"Spline length: {totalLength:F2} m\n" +
                $"Tile length: {extent:F2} m\n" +
                $"Tiles to place: {numTiles}\n" +
                $"Covered length: {covered:F2} m";
            if (!stretchToFit)
            {
                message += $"\nUncovered remainder: {usable - covered:F2} m";
            }
            message += cornerPrefab != null
                ? $"\nCorner prefab: {cornerPrefab.name} (threshold {cornerThresholdDegrees:F0} deg)"
                : "\nCorner prefab: none - all tiles will be straight.";
            EditorGUILayout.HelpBox(message, MessageType.None);
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
            if (straightTilePrefab == null)
            {
                error = "Assign a Straight Tile Prefab.";
                return false;
            }
            float extent = tileLengthOverride > 0f ? tileLengthOverride : MeasureExtentAlongAxis(straightTilePrefab, forwardAxis);
            if (extent <= 0.0001f)
            {
                error = "Could not determine tile length (no Renderer found on prefab). Set Tile Length Override manually.";
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
                Debug.LogWarning("[SplineRoadGenerator] Nothing selected.");
                return;
            }
            SplineContainer sc = go.GetComponent<SplineContainer>();
            if (sc == null)
            {
                Debug.LogWarning($"[SplineRoadGenerator] '{go.name}' has no SplineContainer component.");
                return;
            }
            splineContainer = sc;
            splineIndex = 0;
            Repaint();
        }

        private void Generate()
        {
            if (!CanGenerate(out string error))
            {
                Debug.LogError($"[SplineRoadGenerator] {error}");
                return;
            }

            Spline spline = splineContainer.Splines[splineIndex];
            float totalLength = spline.GetLength();
            Vector3 axisVec = AxisToVector(forwardAxis);
            Vector3 cornerAxisAVec = AxisToVector(cornerAxisA);
            Vector3 cornerAxisBVec = AxisToVector(cornerAxisB);
            float autoExtent = MeasureExtentAlongAxis(straightTilePrefab, forwardAxis);
            float extent = tileLengthOverride > 0f ? tileLengthOverride : autoExtent;
            float usableLength = Mathf.Max(0f, totalLength - startOffset - endOffset);

            int numTiles = stretchToFit
                ? Mathf.Max(1, Mathf.RoundToInt(usableLength / extent))
                : Mathf.FloorToInt(usableLength / extent);

            if (numTiles <= 0)
            {
                Debug.LogWarning("[SplineRoadGenerator] Spline is shorter than one tile - nothing to generate.");
                return;
            }

            float placedTileLength = stretchToFit ? usableLength / numTiles : extent;
            float scaleFactor = placedTileLength / autoExtent;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoLabel);

            ClearInternal();

            var root = new GameObject(parentName);
            Undo.RegisterCreatedObjectUndo(root, UndoLabel);
            root.transform.SetParent(parentUnder, false);

            int straightCount = 0;
            int cornerCount = 0;
            float half = placedTileLength * 0.5f;

            float curT;
            SplineUtility.GetPointAtLinearDistance<Spline>(spline, 0f, startOffset, out curT);

            for (int i = 0; i < numTiles; i++)
            {
                float tileStartT = curT;
                float centerT;
                SplineUtility.GetPointAtLinearDistance<Spline>(spline, tileStartT, half, out centerT);
                float tileEndT;
                SplineUtility.GetPointAtLinearDistance<Spline>(spline, centerT, half, out tileEndT);
                curT = tileEndT;

                float3 posLocal, tanLocal, upLocal;
                SplineUtility.Evaluate<Spline>(spline, centerT, out posLocal, out tanLocal, out upLocal);
                Vector3 worldPos = splineContainer.transform.TransformPoint((Vector3)posLocal);
                Vector3 centerTan = ResolveWorldTangent(tanLocal, axisVec);

                bool placedCorner = false;
                if (cornerPrefab != null)
                {
                    float3 inTanLocal, outTanLocal;
                    SplineUtility.Evaluate<Spline>(spline, tileStartT, out _, out inTanLocal, out _);
                    SplineUtility.Evaluate<Spline>(spline, tileEndT, out _, out outTanLocal, out _);
                    Vector3 inTan = ResolveWorldTangent(inTanLocal, axisVec);
                    Vector3 outTan = ResolveWorldTangent(outTanLocal, axisVec);
                    float turnAngle = Vector3.Angle(inTan, outTan);

                    if (turnAngle >= cornerThresholdDegrees)
                    {
                        Quaternion cornerRot = ComputeCornerRotation(cornerAxisAVec, cornerAxisBVec, inTan, outTan);
                        GameObject cornerInstance = (GameObject)PrefabUtility.InstantiatePrefab(cornerPrefab, root.transform);
                        cornerInstance.transform.SetPositionAndRotation(worldPos, cornerRot);
                        cornerInstance.name = $"{cornerPrefab.name}_{i:00}";
                        Undo.RegisterCreatedObjectUndo(cornerInstance, UndoLabel);
                        placedCorner = true;
                        cornerCount++;
                    }
                }

                if (!placedCorner)
                {
                    Quaternion rot = Quaternion.FromToRotation(axisVec, centerTan);
                    GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(straightTilePrefab, root.transform);
                    instance.transform.SetPositionAndRotation(worldPos, rot);
                    if (stretchToFit && autoExtent > 0.0001f && !Mathf.Approximately(scaleFactor, 1f))
                    {
                        ScaleAlongAxis(instance.transform, forwardAxis, scaleFactor);
                    }
                    instance.name = $"{straightTilePrefab.name}_{i:00}";
                    Undo.RegisterCreatedObjectUndo(instance, UndoLabel);
                    straightCount++;
                }
            }

            if (!spline.Closed)
            {
                if (startCapPrefab != null)
                {
                    PlaceCap(spline, startCapPrefab, 0f, axisVec, flipStartCap, root.transform, "StartCap");
                }
                if (endCapPrefab != null)
                {
                    PlaceCap(spline, endCapPrefab, totalLength, axisVec, flipEndCap, root.transform, "EndCap");
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeGameObject = root;

            Debug.Log($"[SplineRoadGenerator] Generated {straightCount} straight + {cornerCount} corner tile(s) " +
                       $"along '{splineContainer.name}' (spline length {totalLength:F2} m).");
        }

        /// <summary>
        /// Picks the 90-degree rotation (about world/parent Y) whose two open sides
        /// best align with the incoming (reversed) and outgoing travel directions,
        /// trying both pairings since either open side may be the "in" or "out" one.
        /// </summary>
        private static Quaternion ComputeCornerRotation(Vector3 axisA, Vector3 axisB, Vector3 inTangent, Vector3 outTangent)
        {
            float bestScore = float.NegativeInfinity;
            int bestStep = 0;
            for (int step = 0; step < 4; step++)
            {
                Quaternion candidate = Quaternion.Euler(0f, step * 90f, 0f);
                Vector3 openA = candidate * axisA;
                Vector3 openB = candidate * axisB;
                float scoreAB = Vector3.Dot(openA, -inTangent) + Vector3.Dot(openB, outTangent);
                float scoreBA = Vector3.Dot(openB, -inTangent) + Vector3.Dot(openA, outTangent);
                float score = Mathf.Max(scoreAB, scoreBA);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestStep = step;
                }
            }
            return Quaternion.Euler(0f, bestStep * 90f, 0f);
        }

        private void PlaceCap(Spline spline, GameObject prefab, float distanceFromStart, Vector3 axisVec,
            bool flip, Transform parent, string objectName)
        {
            float t;
            SplineUtility.GetPointAtLinearDistance<Spline>(spline, 0f, distanceFromStart, out t);
            float3 posLocal, tanLocal, upLocal;
            SplineUtility.Evaluate<Spline>(spline, t, out posLocal, out tanLocal, out upLocal);

            Vector3 worldPos = splineContainer.transform.TransformPoint((Vector3)posLocal);
            Vector3 worldTan = ResolveWorldTangent(tanLocal, axisVec);
            Quaternion rot = Quaternion.FromToRotation(axisVec, worldTan);
            if (flip)
            {
                rot *= Quaternion.AngleAxis(180f, Vector3.up);
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            instance.transform.SetPositionAndRotation(worldPos, rot);
            instance.name = objectName;
            Undo.RegisterCreatedObjectUndo(instance, UndoLabel);
        }

        private Vector3 ResolveWorldTangent(float3 tangentLocal, Vector3 axisVecFallback)
        {
            Vector3 worldTan = splineContainer.transform.TransformDirection(((Vector3)tangentLocal).normalized);
            if (flattenTangentY)
            {
                worldTan.y = 0f;
            }
            if (worldTan.sqrMagnitude < 1e-6f)
            {
                worldTan = axisVecFallback;
            }
            return worldTan.normalized;
        }

        private void Clear()
        {
            GameObject root = FindGeneratedRoot();
            if (root == null)
            {
                Debug.Log($"[SplineRoadGenerator] No object named '{parentName}' found to clear.");
                return;
            }
            Undo.DestroyObjectImmediate(root);
            Debug.Log($"[SplineRoadGenerator] Cleared '{parentName}'.");
        }

        private void ClearInternal()
        {
            GameObject root = FindGeneratedRoot();
            if (root != null)
            {
                Undo.DestroyObjectImmediate(root);
            }
        }

        private GameObject FindGeneratedRoot()
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

        private static Vector3 AxisToVector(Axis axis)
        {
            switch (axis)
            {
                case Axis.PlusX: return Vector3.right;
                case Axis.MinusX: return Vector3.left;
                case Axis.PlusY: return Vector3.up;
                case Axis.MinusY: return Vector3.down;
                case Axis.PlusZ: return Vector3.forward;
                case Axis.MinusZ: return Vector3.back;
                default: return Vector3.right;
            }
        }

        private static float MeasureExtentAlongAxis(GameObject prefab, Axis axis)
        {
            if (prefab == null)
            {
                return 0f;
            }
            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return 0f;
            }
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }
            switch (axis)
            {
                case Axis.PlusX:
                case Axis.MinusX:
                    return bounds.size.x;
                case Axis.PlusY:
                case Axis.MinusY:
                    return bounds.size.y;
                default:
                    return bounds.size.z;
            }
        }

        private static void ScaleAlongAxis(Transform transform, Axis axis, float factor)
        {
            Vector3 scale = transform.localScale;
            switch (axis)
            {
                case Axis.PlusX:
                case Axis.MinusX:
                    scale.x *= factor;
                    break;
                case Axis.PlusY:
                case Axis.MinusY:
                    scale.y *= factor;
                    break;
                default:
                    scale.z *= factor;
                    break;
            }
            transform.localScale = scale;
        }
    }
}
