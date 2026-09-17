using OpenCity.Tiles;
using UnityEditor;
using UnityEngine;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Inspector for the BSP tree the City Road Generator (BSP Tilemap) stores in the scene: the
    /// city it belongs to, then the tree itself (see <see cref="CityBspTreePanel"/>), with the
    /// selected region highlighted in the Scene view while this object is selected.
    /// </summary>
    [CustomEditor(typeof(CityBspTree))]
    public class CityBspTreeEditor : Editor
    {
        private CityBspTreePanel panel;

        private void OnEnable()
        {
            panel = new CityBspTreePanel(null);
            panel.Changed += SceneView.RepaintAll;
        }

        public override void OnInspectorGUI()
        {
            var tree = (CityBspTree)target;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Tilemap", tree.tilemap, typeof(UnityEngine.Tilemaps.Tilemap), true);
                EditorGUILayout.Vector2IntField("Origin Cell", tree.originCell);
                EditorGUILayout.IntField("Side Length (cells)", tree.sideLength);
                EditorGUILayout.IntField("Random Seed", tree.seed);
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("BSP Tree", EditorStyles.boldLabel);
            panel.OnGUI(tree.nodes, node => CityBspTreePanel.FrameInSceneView(tree.tilemap, tree.originCell, node.rect));
        }

        private void OnSceneGUI()
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }
            var tree = (CityBspTree)target;
            panel.DrawSceneSelection(tree.tilemap, tree.originCell);
        }
    }
}
