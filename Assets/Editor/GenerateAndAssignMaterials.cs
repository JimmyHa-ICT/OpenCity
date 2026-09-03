using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// One-shot batch tool: creates a single URP Lit material from the shared
    /// Models/Textures/colormap.png atlas and remaps it onto every material
    /// slot of every FBX under Assets/Models (and subfolders).
    /// </summary>
    public static class GenerateAndAssignMaterials
    {
        private const string ModelsFolder = "Assets/Models";
        private const string MaterialsFolder = "Assets/Models/Materials";
        private const string TexturePath = "Assets/Models/Textures/colormap.png";
        private const string MaterialPath = "Assets/Models/Materials/M_Colormap.mat";

        [MenuItem("Tools/OpenCity/Generate And Assign Colormap Material")]
        public static void Run()
        {
            if (!AssetDatabase.IsValidFolder(MaterialsFolder))
            {
                AssetDatabase.CreateFolder(ModelsFolder, "Materials");
            }

            Texture2D colormap = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            if (colormap == null)
            {
                Debug.LogError($"[GenerateAndAssignMaterials] Could not find texture at {TexturePath}");
                return;
            }

            Material sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (sharedMaterial == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                {
                    Debug.LogError("[GenerateAndAssignMaterials] Could not find shader 'Universal Render Pipeline/Lit'.");
                    return;
                }

                sharedMaterial = new Material(shader) { name = "M_Colormap" };
                sharedMaterial.SetTexture("_BaseMap", colormap);
                sharedMaterial.SetFloat("_Smoothness", 0.1f);
                sharedMaterial.SetFloat("_Metallic", 0f);

                AssetDatabase.CreateAsset(sharedMaterial, MaterialPath);
                Debug.Log($"[GenerateAndAssignMaterials] Created material at {MaterialPath}");
            }
            else
            {
                // Keep an existing material's settings, but make sure the texture is wired up.
                sharedMaterial.SetTexture("_BaseMap", colormap);
                EditorUtility.SetDirty(sharedMaterial);
            }

            string[] fbxGuids = AssetDatabase.FindAssets("t:Model", new[] { ModelsFolder });
            int assignedCount = 0;
            int skippedCount = 0;

            foreach (string guid in fbxGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!(AssetImporter.GetAtPath(path) is ModelImporter importer))
                {
                    continue;
                }

                Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(path);
                Material[] materialsInModel = subAssets.OfType<Material>().ToArray();

                if (materialsInModel.Length == 0)
                {
                    skippedCount++;
                    continue;
                }

                bool remapChanged = false;
                foreach (Material mat in materialsInModel)
                {
                    var sourceId = new AssetImporter.SourceAssetIdentifier(mat);
                    Object currentRemap = importer.GetExternalObjectMap()
                        .FirstOrDefault(kvp => kvp.Key.Equals(sourceId)).Value;

                    if (currentRemap != sharedMaterial)
                    {
                        importer.AddRemap(sourceId, sharedMaterial);
                        remapChanged = true;
                    }
                }

                if (remapChanged)
                {
                    EditorUtility.SetDirty(importer);
                    importer.SaveAndReimport();
                    assignedCount++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[GenerateAndAssignMaterials] Done. Material assigned/updated on {assignedCount} FBX file(s), {skippedCount} skipped (no material slots).");
        }
    }
}
