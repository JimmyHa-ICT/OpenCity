using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Editor window for authoring paths that are straight segments joined by rounded
    /// corners at a single global radius, either by rounding an existing SplineContainer
    /// drawn with Unity's Spline Tool, or by clicking waypoints directly in the Scene view.
    /// Both modes share SplineFilletUtility's geometry core. See
    /// Docs/RoundedSplinePath.md for full usage notes.
    /// </summary>
    public class RoundedSplinePathWindow : EditorWindow
    {
        private enum Mode { RoundExistingSpline, DrawNewWaypoints }

        private const string UndoLabelRound = "Round Spline Corners";
        private const string UndoLabelBuild = "Build Rounded Spline Path";

        [SerializeField] private Mode mode = Mode.RoundExistingSpline;
        [SerializeField] private float cornerRadius = 3f;

        // --- Round Existing Spline ---
        [SerializeField] private SplineContainer existingSplineContainer;
        [SerializeField] private int existingSplineIndex;

        // --- Draw New Waypoints ---
        [SerializeField] private SplineContainer targetContainer;
        [SerializeField] private string targetContainerName = "RoundedPath";
        [SerializeField] private Transform targetParentUnder;
        [SerializeField] private bool closedPath;
        [SerializeField] private float placementPlaneY;
        [SerializeField] private List<Vector3> waypointsWorld = new List<Vector3>();

        private bool isPlacing;
        private Vector2 scrollPosition;

        [MenuItem("Tools/OpenCity/Rounded Spline Path Tool")]
        private static void Open()
        {
            RoundedSplinePathWindow window = GetWindow<RoundedSplinePathWindow>("Rounded Spline Path");
            // Low enough to dock in a short panel - the body scrolls (see OnGUI), so the minimum
            // no longer has to fit every field at once.
            window.minSize = new Vector2(360, 320);
        }

        private void OnEnable()
        {
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
            // The mode toolbar stays pinned above the scrolled body - it selects which section is
            // shown, so it should never scroll out of reach. Unlike the road generator windows,
            // each section here ends with its own action button, so those scroll with the body
            // rather than being pinned to the bottom of the window.
            mode = (Mode)GUILayout.Toolbar((int)mode, new[] { "Round Existing Spline", "Draw New Waypoints" });

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Rounding", EditorStyles.boldLabel);
            cornerRadius = Mathf.Max(0.0001f, EditorGUILayout.FloatField(new GUIContent(
                "Corner Radius (m)",
                "Fillet radius applied to every corner. Clamped down per-corner if the adjacent " +
                "straight segments are too short to fit it."), cornerRadius));

            EditorGUILayout.Space();
            switch (mode)
            {
                case Mode.RoundExistingSpline:
                    DrawRoundExistingSection();
                    break;
                case Mode.DrawNewWaypoints:
                    DrawWaypointSection();
                    break;
            }

            EditorGUILayout.EndScrollView();
        }

        // ---------------------------------------------------------------
        // Round Existing Spline
        // ---------------------------------------------------------------

        private void DrawRoundExistingSection()
        {
            EditorGUILayout.LabelField("Spline", EditorStyles.boldLabel);
            existingSplineContainer = (SplineContainer)EditorGUILayout.ObjectField(
                "Spline Container", existingSplineContainer, typeof(SplineContainer), true);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Use Selected GameObject", GUILayout.Width(190)))
                {
                    UseSelectionForExisting();
                }
            }

            if (existingSplineContainer != null && existingSplineContainer.Splines.Count > 1)
            {
                string[] options = new string[existingSplineContainer.Splines.Count];
                for (int i = 0; i < options.Length; i++)
                {
                    options[i] = $"[{i}] {existingSplineContainer.Splines[i].Count} knots";
                }
                existingSplineIndex = EditorGUILayout.Popup("Spline Index", existingSplineIndex, options);
            }
            else
            {
                existingSplineIndex = 0;
            }

            EditorGUILayout.Space();
            DrawExistingPreview();

            EditorGUILayout.Space();
            GUI.enabled = CanRoundExisting(out _);
            if (GUILayout.Button("Round Corners", GUILayout.Height(30)))
            {
                RoundExisting();
            }
            GUI.enabled = true;
        }

        private void UseSelectionForExisting()
        {
            GameObject go = Selection.activeGameObject;
            if (go == null)
            {
                Debug.LogWarning("[RoundedSplinePath] Nothing selected.");
                return;
            }
            SplineContainer sc = go.GetComponent<SplineContainer>();
            if (sc == null)
            {
                Debug.LogWarning($"[RoundedSplinePath] '{go.name}' has no SplineContainer component.");
                return;
            }
            existingSplineContainer = sc;
            existingSplineIndex = 0;
            Repaint();
        }

        private bool CanRoundExisting(out string error)
        {
            if (existingSplineContainer == null)
            {
                error = "Assign a Spline Container.";
                return false;
            }
            if (existingSplineIndex < 0 || existingSplineIndex >= existingSplineContainer.Splines.Count)
            {
                error = "Invalid spline index.";
                return false;
            }
            if (existingSplineContainer.Splines[existingSplineIndex].Count < 2)
            {
                error = "Spline needs at least 2 knots.";
                return false;
            }
            error = null;
            return true;
        }

        private void DrawExistingPreview()
        {
            if (!CanRoundExisting(out string error))
            {
                if (!string.IsNullOrEmpty(error))
                {
                    EditorGUILayout.HelpBox(error, MessageType.Warning);
                }
                return;
            }

            Spline spline = existingSplineContainer.Splines[existingSplineIndex];
            List<float3> waypoints = ReadKnotPositions(spline);
            SplineFilletUtility.FilletPreviewInfo info = SplineFilletUtility.ComputePreview(waypoints, cornerRadius, spline.Closed);
            EditorGUILayout.HelpBox(
                $"Waypoints: {info.WaypointCount}\n" +
                $"Corners: {info.CornerCount} ({info.ClampedCornerCount} radius-clamped)\n" +
                $"Resulting knots: {info.ResultingKnotCount}", MessageType.None);
        }

        private void RoundExisting()
        {
            if (!CanRoundExisting(out string error))
            {
                Debug.LogError($"[RoundedSplinePath] {error}");
                return;
            }

            Spline spline = existingSplineContainer.Splines[existingSplineIndex];
            List<float3> waypoints = ReadKnotPositions(spline);
            int knotCountBefore = spline.Count;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoLabelRound);

            Undo.RecordObject(existingSplineContainer, UndoLabelRound);
            SplineFilletUtility.BuildRoundedSpline(spline, waypoints, cornerRadius, spline.Closed);
            EditorUtility.SetDirty(existingSplineContainer);

            Undo.CollapseUndoOperations(undoGroup);

            Debug.Log($"[RoundedSplinePath] Rounded '{existingSplineContainer.name}' spline [{existingSplineIndex}]: " +
                       $"{knotCountBefore} knots -> {spline.Count} knots.");
        }

        private static List<float3> ReadKnotPositions(Spline spline)
        {
            var waypoints = new List<float3>(spline.Count);
            for (int i = 0; i < spline.Count; i++)
            {
                waypoints.Add(spline[i].Position);
            }
            return waypoints;
        }

        // ---------------------------------------------------------------
        // Draw New Waypoints
        // ---------------------------------------------------------------

        private void DrawWaypointSection()
        {
            EditorGUILayout.LabelField("Target Spline", EditorStyles.boldLabel);
            targetContainerName = EditorGUILayout.TextField(new GUIContent(
                "Target Name",
                "Name of the GameObject the spline is built into. If Target Container is empty, an " +
                "existing object with this exact name (under Parent, or scene root) is reused, " +
                "otherwise a new one is created."), targetContainerName);
            targetParentUnder = (Transform)EditorGUILayout.ObjectField(new GUIContent(
                "Parent Container",
                "Optional. Where the target GameObject is nested. Leave empty for scene root."), targetParentUnder, typeof(Transform), true);
            targetContainer = (SplineContainer)EditorGUILayout.ObjectField(new GUIContent(
                "Target Container (optional)",
                "Build directly into this existing SplineContainer instead of auto-creating/reusing one by name."),
                targetContainer, typeof(SplineContainer), true);
            closedPath = EditorGUILayout.Toggle(new GUIContent(
                "Closed Path", "Loop the path back from the last waypoint to the first."), closedPath);
            placementPlaneY = EditorGUILayout.FloatField(new GUIContent(
                "Ground Plane Y",
                "Fallback height used to place a waypoint when the click doesn't hit any Scene geometry."), placementPlaneY);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Waypoints", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                isPlacing
                    ? "Placing: left-click in the Scene view to add waypoints. Alt+drag still orbits/pans. " +
                      "Escape or Finish Placing to stop."
                    : "Click Start Placing, then left-click in the Scene view to add waypoints.", MessageType.Info);
            EditorGUILayout.LabelField($"Waypoint count: {waypointsWorld.Count}");

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = !isPlacing;
                if (GUILayout.Button("Start Placing"))
                {
                    isPlacing = true;
                }
                GUI.enabled = isPlacing;
                if (GUILayout.Button("Finish Placing"))
                {
                    isPlacing = false;
                }
                GUI.enabled = waypointsWorld.Count > 0;
                if (GUILayout.Button("Undo Last Point"))
                {
                    waypointsWorld.RemoveAt(waypointsWorld.Count - 1);
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button("Clear Points"))
                {
                    waypointsWorld.Clear();
                    isPlacing = false;
                    SceneView.RepaintAll();
                }
                GUI.enabled = true;
            }

            EditorGUILayout.Space();
            DrawWaypointPreview();

            EditorGUILayout.Space();
            GUI.enabled = CanBuildNew(out _);
            if (GUILayout.Button("Build Spline", GUILayout.Height(30)))
            {
                BuildNew();
            }
            GUI.enabled = true;
        }

        private bool CanBuildNew(out string error)
        {
            if (waypointsWorld.Count < 2)
            {
                error = "Place at least 2 waypoints.";
                return false;
            }
            error = null;
            return true;
        }

        private List<float3> GetLocalWaypoints(Transform referenceTransform)
        {
            var local = new List<float3>(waypointsWorld.Count);
            foreach (Vector3 world in waypointsWorld)
            {
                Vector3 p = referenceTransform != null ? referenceTransform.InverseTransformPoint(world) : world;
                local.Add(p);
            }
            return local;
        }

        private void DrawWaypointPreview()
        {
            if (!CanBuildNew(out string error))
            {
                if (!string.IsNullOrEmpty(error))
                {
                    EditorGUILayout.HelpBox(error, MessageType.Warning);
                }
                return;
            }

            Transform reference = targetContainer != null ? targetContainer.transform : targetParentUnder;
            List<float3> waypoints = GetLocalWaypoints(reference);
            SplineFilletUtility.FilletPreviewInfo info = SplineFilletUtility.ComputePreview(waypoints, cornerRadius, closedPath);
            EditorGUILayout.HelpBox(
                $"Waypoints: {info.WaypointCount}\n" +
                $"Corners: {info.CornerCount} ({info.ClampedCornerCount} radius-clamped)\n" +
                $"Resulting knots: {info.ResultingKnotCount}", MessageType.None);
        }

        private void BuildNew()
        {
            if (!CanBuildNew(out string error))
            {
                Debug.LogError($"[RoundedSplinePath] {error}");
                return;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoLabelBuild);

            SplineContainer container = targetContainer;
            if (container == null)
            {
                GameObject go = FindTargetRoot();
                if (go == null)
                {
                    go = new GameObject(targetContainerName);
                    Undo.RegisterCreatedObjectUndo(go, UndoLabelBuild);
                    go.transform.SetParent(targetParentUnder, false);
                }
                container = go.GetComponent<SplineContainer>();
                if (container == null)
                {
                    container = Undo.AddComponent<SplineContainer>(go);
                }
                targetContainer = container;
            }
            else
            {
                Undo.RecordObject(container, UndoLabelBuild);
            }

            List<float3> localWaypoints = GetLocalWaypoints(container.transform);
            SplineFilletUtility.BuildRoundedSpline(container.Spline, localWaypoints, cornerRadius, closedPath);
            EditorUtility.SetDirty(container);

            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeGameObject = container.gameObject;

            Debug.Log($"[RoundedSplinePath] Built '{container.name}' spline from {waypointsWorld.Count} waypoint(s), " +
                       $"{container.Spline.Count} resulting knots.");
        }

        private GameObject FindTargetRoot()
        {
            if (string.IsNullOrEmpty(targetContainerName))
            {
                return null;
            }
            if (targetParentUnder != null)
            {
                Transform found = targetParentUnder.Find(targetContainerName);
                return found != null ? found.gameObject : null;
            }
            Scene scene = SceneManager.GetActiveScene();
            foreach (GameObject go in scene.GetRootGameObjects())
            {
                if (go.name == targetContainerName)
                {
                    return go;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------
        // Scene view interaction
        // ---------------------------------------------------------------

        private void OnSceneGUI(SceneView sceneView)
        {
            DrawWaypointHandles();

            if (mode != Mode.DrawNewWaypoints || !isPlacing)
            {
                return;
            }

            Event e = Event.current;

            if (e.type == EventType.Layout)
            {
                int id = GUIUtility.GetControlID(FocusType.Passive);
                HandleUtility.AddDefaultControl(id);
            }

            if (e.alt)
            {
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                Vector3 point;
                if (HandleUtility.PlaceObject(e.mousePosition, out point, out _))
                {
                    waypointsWorld.Add(point);
                }
                else
                {
                    Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                    var groundPlane = new Plane(Vector3.up, new Vector3(0f, placementPlaneY, 0f));
                    if (groundPlane.Raycast(ray, out float dist))
                    {
                        waypointsWorld.Add(ray.GetPoint(dist));
                    }
                }
                e.Use();
                Repaint();
                sceneView.Repaint();
            }
            else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                isPlacing = false;
                e.Use();
                Repaint();
                sceneView.Repaint();
            }
        }

        private void DrawWaypointHandles()
        {
            if (mode != Mode.DrawNewWaypoints || waypointsWorld.Count == 0)
            {
                return;
            }

            Handles.color = Color.yellow;
            for (int i = 0; i < waypointsWorld.Count; i++)
            {
                float size = HandleUtility.GetHandleSize(waypointsWorld[i]) * 0.1f;
                Handles.SphereHandleCap(0, waypointsWorld[i], Quaternion.identity, size, EventType.Repaint);
                if (i > 0)
                {
                    Handles.DrawLine(waypointsWorld[i - 1], waypointsWorld[i]);
                }
            }
            if (closedPath && waypointsWorld.Count > 2)
            {
                Handles.DrawLine(waypointsWorld[waypointsWorld.Count - 1], waypointsWorld[0]);
            }

            if (waypointsWorld.Count >= 2)
            {
                Transform reference = targetContainer != null ? targetContainer.transform : targetParentUnder;
                List<float3> localWaypoints = GetLocalWaypoints(reference);
                var previewSpline = new Spline();
                SplineFilletUtility.BuildRoundedSpline(previewSpline, localWaypoints, cornerRadius, closedPath);

                Handles.color = Color.cyan;
                const int steps = 64;
                var polyline = new List<Vector3>(steps + 1);
                for (int i = 0; i <= steps; i++)
                {
                    float t = (float)i / steps;
                    float3 posLocal = SplineUtility.EvaluatePosition<Spline>(previewSpline, t);
                    Vector3 posWorld = reference != null ? reference.TransformPoint(posLocal) : (Vector3)posLocal;
                    polyline.Add(posWorld);
                }
                Handles.DrawAAPolyLine(3f, polyline.ToArray());
            }
        }
    }
}
