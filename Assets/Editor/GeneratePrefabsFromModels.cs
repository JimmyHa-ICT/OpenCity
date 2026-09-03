using System.IO;
using UnityEditor;
using UnityEngine;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// One-shot batch tool: creates a regular Prefab asset (nested on top of the
    /// source FBX) for every model under Assets/Models, saved into Assets/Prefabs.
    /// </summary>
    public static class GeneratePrefabsFromModels
    {
        private const string ModelsFolder = "Assets/Models";
        private const string PrefabsFolder = "Assets/Prefabs";

        [MenuItem("Tools/OpenCity/Generate Prefabs From Models")]
        public static void Run()
        {
            if (!AssetDatabase.IsValidFolder(PrefabsFolder))
            {
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            }

            string[] fbxGuids = AssetDatabase.FindAssets("t:Model", new[] { ModelsFolder });
            int createdCount = 0;
            int skippedCount = 0;

            foreach (string guid in fbxGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model == null)
                {
                    Debug.LogWarning($"[GeneratePrefabsFromModels] No root GameObject found in {path}, skipping.");
                    skippedCount++;
                    continue;
                }

                string prefabName = Path.GetFileNameWithoutExtension(path);
                string prefabPath = $"{PrefabsFolder}/{prefabName}.prefab";

                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;

                PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
                Object.DestroyImmediate(instance);
                createdCount++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[GeneratePrefabsFromModels] Done. Created/updated {createdCount} prefab(s) in {PrefabsFolder}, {skippedCount} skipped.");
        }
    }
}
