using System.Collections.Generic;
using System.IO;
using OpenCity.Tiles;
using UnityEditor;
using UnityEngine;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Turns the building models in <see cref="ModelFolder"/> into tiles for the Buildings Tilemap: one
    /// piece prefab and one <see cref="BuildingTile"/> per model, its size variants, and the
    /// <see cref="BuildingCoverTile"/>.
    ///
    /// Each piece's root is the centre of the building's footprint and its model faces -Z, so the tile
    /// only has to position and turn it. The model is **fitted to its footprint**: the kit's buildings
    /// don't come in whole cells (a skyscraper is 1.36 cells across), so every footprint up to 3x3 is
    /// tried with the uniform scale that makes the model fill it, and the one that wastes the least lot
    /// while changing the model's size the least wins (see <see cref="ChooseFit"/>). Street buildings
    /// are pushed forward to the front edge of their footprint so the facades line the street; towers
    /// stay centred.
    ///
    /// **Size variants.** Every building also gets a tile for each other footprint it can fill without
    /// looking wrong (see <see cref="VariantFits"/>): mostly deeper lots, with the model stretched back
    /// into the block while its facade keeps its proportions. They let the planner fill deep blocks from
    /// the street instead of leaving the back of every lot empty. Variants live in
    /// <see cref="VariantFolderName"/> subfolders and are regenerated on every build, taking their tier
    /// and weight from their building's tile.
    ///
    /// Re-running keeps each tile's footprint, tier and weight as edited in the Inspector, refits the
    /// scale to that footprint and rebuilds the pieces in place (same GUIDs). Refitting instead picks
    /// the footprint and tier again. See Docs/CityBuildingGenerator.md.
    /// </summary>
    internal static class BuildingTileBuilder
    {
        public const string ModelFolder = "Assets/Models/Building";
        public const string PieceFolder = "Assets/Prefabs/Buildings/Tile Pieces";
        public const string TileFolder = "Assets/Tiles/Buildings";
        public const string CoverTilePath = TileFolder + "/Building Cover Tile.asset";
        public const string VariantFolderName = "Variants";

        /// <summary>
        /// Yaw that turns a kit model's front to face -Z. The City Kit (Commercial) models already face
        /// -Z (their entrances, awnings and shop windows are on that side), so no turn is needed.
        /// </summary>
        private const float ModelFrontYaw = 0f;

        /// <summary>How far a horizontal extent may exceed a whole number of cells and still round down.</summary>
        private const float OverhangTolerance = 0.12f;

        /// <summary>Gap left between a street building's facade and the edge of its footprint.</summary>
        private const float FrontSetback = 0.02f;

        private const int MaxFootprint = 3;

        /// <summary>How much of its footprint a fitted model spans along its tighter axis, leaving a gap to the neighbours.</summary>
        private const float FitFill = 0.94f;

        /// <summary>The scale range a model may be fitted with. Beyond it the kit's windows and doors stop matching their neighbours'.</summary>
        private const float MinFitScale = 0.7f;
        private const float MaxFitScale = 1.4f;

        /// <summary>Cost of a scale change against wasted lot area: a 2x change (ln 2 = 0.69) weighs like 35% of the lot left empty.</summary>
        private const float ScaleCost = 0.5f;

        /// <summary>
        /// How much further than the uniform scale a model may be stretched along its looser horizontal
        /// axis to fill its lot. Small enough that a box-shaped building's windows still look square.
        /// </summary>
        private const float MaxStretch = 1.15f;

        /// <summary>
        /// How far past the uniform scale a size variant may stretch its model back into the block. The
        /// facade keeps its proportions; only the sides get longer.
        /// </summary>
        private const float MaxDepthStretch = 2f;

        /// <summary>The deepest footprint a size variant may have.</summary>
        private const int MaxVariantDepth = 4;

        [MenuItem("Tools/OpenCity/Create Building Tiles")]
        private static void CreateFromMenu()
        {
            List<BuildingTile> tiles = CreateOrUpdateTiles(false, out string error);
            if (tiles == null)
            {
                Debug.LogError($"[Buildings] {error}");
                return;
            }
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<Object>(TileFolder);
        }

        /// <summary>Every BuildingTile in <see cref="TileFolder"/>, sorted by asset path.</summary>
        public static List<BuildingTile> LoadTiles(string folder = TileFolder)
        {
            var tiles = new List<BuildingTile>();
            if (!AssetDatabase.IsValidFolder(folder))
            {
                return tiles;
            }
            var paths = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:BuildingTile", new[] { folder }))
            {
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            }
            paths.Sort(string.CompareOrdinal);
            foreach (string path in paths)
            {
                var tile = AssetDatabase.LoadAssetAtPath<BuildingTile>(path);
                if (tile != null)
                {
                    tiles.Add(tile);
                }
            }
            return tiles;
        }

        public static BuildingCoverTile LoadOrCreateCoverTile()
        {
            var cover = AssetDatabase.LoadAssetAtPath<BuildingCoverTile>(CoverTilePath);
            if (cover == null)
            {
                EnsureFolder(TileFolder);
                cover = ScriptableObject.CreateInstance<BuildingCoverTile>();
                AssetDatabase.CreateAsset(cover, CoverTilePath);
            }
            return cover;
        }

        /// <summary>
        /// Builds or rebuilds every piece and tile. With <paramref name="refit"/>, existing tiles get their
        /// footprint and tier chosen again from the model, as for a new tile; otherwise they keep them.
        /// Null (with <paramref name="error"/>) if there are no models.
        /// </summary>
        public static List<BuildingTile> CreateOrUpdateTiles(bool refit, out string error)
        {
            var models = new List<GameObject>();
            foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { ModelFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string name = Path.GetFileNameWithoutExtension(path);
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model == null)
                {
                    continue;
                }
                if (name.StartsWith("building-"))
                {
                    models.Add(model);
                }
            }
            if (models.Count == 0)
            {
                error = $"no building-*.fbx models found in {ModelFolder}.";
                return null;
            }
            models.Sort((p, q) => string.CompareOrdinal(p.name, q.name));

            EnsureFolder(PieceFolder);
            EnsureFolder(TileFolder);
            LoadOrCreateCoverTile();

            var tiles = new List<BuildingTile>();
            var variantNames = new HashSet<string>();
            int variants = 0;
            foreach (GameObject model in models)
            {
                BuildingTile tile = BuildStreetTile(model, refit);
                tiles.Add(tile);
                foreach (BuildingTile variant in BuildVariants(model, tile))
                {
                    variantNames.Add(variant.name);
                    tiles.Add(variant);
                    variants++;
                }
            }
            RemoveStaleVariants(variantNames);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Buildings] Created or updated {tiles.Count} building tile(s) ({variants} size variant(s)) in {TileFolder} " +
                      $"and their pieces in {PieceFolder}.");
            error = null;
            return tiles;
        }

        private static BuildingTile BuildStreetTile(GameObject model, bool refit)
        {
            string tilePath = $"{TileFolder}/{model.name}.asset";
            var tile = AssetDatabase.LoadAssetAtPath<BuildingTile>(tilePath);
            bool created = tile == null;

            Bounds bounds = MeasureTurned(model, ModelFrontYaw);
            if (created)
            {
                tile = ScriptableObject.CreateInstance<BuildingTile>();
                tile.weight = 1f;
            }
            if (created || refit)
            {
                ChooseFit(bounds.size, out Vector2Int footprint, out float uniform);
                tile.footprint = footprint;
                tile.tier = TierFor(model.name, bounds.size.y * uniform);
            }
            Vector3 scale = FitScaleVector(bounds.size, tile.footprint);

            tile.piece = SaveStreetPiece(model.name, PieceFolder, model, bounds, tile.footprint, scale,
                tile.tier == BuildingTier.Tower);

            if (created)
            {
                AssetDatabase.CreateAsset(tile, tilePath);
            }
            EditorUtility.SetDirty(tile);
            return tile;
        }

        /// <summary>
        /// The size-variant tiles of <paramref name="model"/>: one per footprint up to 3 wide and
        /// <see cref="MaxVariantDepth"/> deep, other than its own tile's, that <see cref="VariantFits"/>.
        /// Tier and weight come from <paramref name="baseTile"/>, so editing that tile edits its variants.
        /// </summary>
        private static List<BuildingTile> BuildVariants(GameObject model, BuildingTile baseTile)
        {
            var variants = new List<BuildingTile>();
            Bounds bounds = MeasureTurned(model, ModelFrontYaw);
            bool centred = baseTile.tier == BuildingTier.Tower;
            float depthStretch = centred ? MaxStretch : MaxDepthStretch;
            string tileFolder = $"{TileFolder}/{VariantFolderName}";
            string pieceFolder = $"{PieceFolder}/{VariantFolderName}";
            for (int x = 1; x <= MaxFootprint; x++)
            {
                for (int z = 1; z <= MaxVariantDepth; z++)
                {
                    var footprint = new Vector2Int(x, z);
                    if (footprint == baseTile.footprint || !VariantFits(bounds.size, footprint, depthStretch))
                    {
                        continue;
                    }
                    EnsureFolder(tileFolder);
                    EnsureFolder(pieceFolder);
                    string name = VariantName(model.name, footprint);
                    string tilePath = $"{tileFolder}/{name}.asset";
                    var tile = AssetDatabase.LoadAssetAtPath<BuildingTile>(tilePath);
                    bool created = tile == null;
                    if (created)
                    {
                        tile = ScriptableObject.CreateInstance<BuildingTile>();
                    }
                    tile.footprint = footprint;
                    tile.tier = baseTile.tier;
                    tile.weight = baseTile.weight;
                    tile.previewSprite = baseTile.previewSprite;
                    Vector3 scale = FitScaleVector(bounds.size, footprint, depthStretch);
                    tile.piece = SaveStreetPiece(name, pieceFolder, model, bounds, footprint, scale, centred);
                    if (created)
                    {
                        AssetDatabase.CreateAsset(tile, tilePath);
                    }
                    EditorUtility.SetDirty(tile);
                    variants.Add(tile);
                }
            }
            return variants;
        }

        public static string VariantName(string modelName, Vector2Int footprint)
        {
            return $"{modelName}-{footprint.x}x{footprint.y}";
        }

        /// <summary>
        /// Whether a model of <paramref name="size"/> can fill <paramref name="footprint"/>: its uniform
        /// fit scale is in the allowed range, and within their stretch limits it spans the lot's width
        /// (<see cref="MaxStretch"/>) and depth (<paramref name="depthStretch"/>).
        /// </summary>
        private static bool VariantFits(Vector3 size, Vector2Int footprint, float depthStretch)
        {
            if (size.x <= 0.0001f || size.z <= 0.0001f)
            {
                return false;
            }
            float uniform = UnclampedFitScale(size, footprint);
            if (uniform < MinFitScale || uniform > MaxFitScale)
            {
                return false;
            }
            const float slack = 0.001f;
            return size.x * uniform * MaxStretch >= footprint.x * FitFill - slack &&
                   size.z * uniform * depthStretch >= footprint.y * FitFill - slack;
        }

        /// <summary>Deletes variant tiles and pieces no longer produced (a model removed, a fit that no longer qualifies).</summary>
        private static void RemoveStaleVariants(HashSet<string> keep)
        {
            foreach (string folder in new[] { $"{TileFolder}/{VariantFolderName}", $"{PieceFolder}/{VariantFolderName}" })
            {
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    continue;
                }
                foreach (string guid in AssetDatabase.FindAssets("", new[] { folder }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!AssetDatabase.IsValidFolder(path) && !keep.Contains(Path.GetFileNameWithoutExtension(path)))
                    {
                        AssetDatabase.DeleteAsset(path);
                    }
                }
            }
        }

        /// <summary>
        /// Saves the piece of a street building: the model scaled by <paramref name="scale"/> and, unless
        /// <paramref name="centred"/>, pushed forward onto the front edge of its footprint.
        /// </summary>
        private static GameObject SaveStreetPiece(string name, string folder, GameObject model, Bounds bounds,
            Vector2Int footprint, Vector3 scale, bool centred)
        {
            return SavePiece(name, folder, root =>
            {
                GameObject instance = AddModel(root, model, ModelFrontYaw, bounds, Vector3.zero, scale);
                if (!centred)
                {
                    // Front edge of the model onto the front (-Z) edge of the footprint.
                    float frontGap = footprint.y * 0.5f - bounds.size.z * scale.z * 0.5f - FrontSetback;
                    instance.transform.localPosition += new Vector3(0f, 0f, -Mathf.Max(0f, frontGap));
                }
            });
        }

        /// <summary>
        /// The footprint and uniform scale a model of <paramref name="size"/> fits best. Every footprint up
        /// to 3x3 is tried at the scale that makes the model span <see cref="FitFill"/> of it along its
        /// tighter axis, within the allowed scale range. Each costs the share of the lot the scaled model
        /// leaves empty, plus <see cref="ScaleCost"/> per unit of |ln scale|; the cheapest wins, the smaller
        /// footprint on a tie. With no candidate in range, the model keeps its size on the footprint its
        /// extents round to.
        /// </summary>
        public static void ChooseFit(Vector3 size, out Vector2Int footprint, out float scale)
        {
            float bestCost = float.MaxValue;
            footprint = new Vector2Int(CellsFor(size.x), CellsFor(size.z));
            scale = FitScale(size, footprint);
            for (int x = 1; x <= MaxFootprint; x++)
            {
                for (int z = 1; z <= MaxFootprint; z++)
                {
                    var candidate = new Vector2Int(x, z);
                    float s = UnclampedFitScale(size, candidate);
                    if (s < MinFitScale || s > MaxFitScale)
                    {
                        continue;
                    }
                    float used = Mathf.Min(size.x * s, x) * Mathf.Min(size.z * s, z);
                    float cost = (1f - used / (x * z)) + ScaleCost * Mathf.Abs(Mathf.Log(s));
                    if (cost < bestCost - 0.0001f)
                    {
                        bestCost = cost;
                        footprint = candidate;
                        scale = s;
                    }
                }
            }
        }

        /// <summary>The uniform scale that fits a model of <paramref name="size"/> to <paramref name="footprint"/>, clamped to the allowed range.</summary>
        public static float FitScale(Vector3 size, Vector2Int footprint)
        {
            return Mathf.Clamp(UnclampedFitScale(size, footprint), MinFitScale, MaxFitScale);
        }

        /// <summary>
        /// <see cref="FitScale"/> on the height and the tighter horizontal axis, and up to
        /// <see cref="MaxStretch"/> times more along the looser one, so a model narrower than its lot
        /// fills more of its width.
        /// </summary>
        public static Vector3 FitScaleVector(Vector3 size, Vector2Int footprint)
        {
            return FitScaleVector(size, footprint, MaxStretch);
        }

        /// <summary><see cref="FitScaleVector(Vector3, Vector2Int)"/>, letting the depth stretch up to <paramref name="depthStretch"/>.</summary>
        private static Vector3 FitScaleVector(Vector3 size, Vector2Int footprint, float depthStretch)
        {
            float uniform = FitScale(size, footprint);
            float x = size.x > 0.0001f ? footprint.x * FitFill / size.x : uniform;
            float z = size.z > 0.0001f ? footprint.y * FitFill / size.z : uniform;
            return new Vector3(
                Mathf.Clamp(x, uniform, uniform * MaxStretch),
                uniform,
                Mathf.Clamp(z, uniform, uniform * depthStretch));
        }

        private static float UnclampedFitScale(Vector3 size, Vector2Int footprint)
        {
            float sx = size.x > 0.0001f ? footprint.x * FitFill / size.x : 1f;
            float sz = size.z > 0.0001f ? footprint.y * FitFill / size.z : 1f;
            return Mathf.Min(sx, sz);
        }

        /// <summary>Whole cells for a horizontal extent, forgiving a small overhang.</summary>
        private static int CellsFor(float size)
        {
            return Mathf.Clamp(Mathf.CeilToInt(size - OverhangTolerance), 1, MaxFootprint);
        }

        private static BuildingTier TierFor(string name, float height)
        {
            if (name.Contains("skyscraper") || height > 3.2f) return BuildingTier.Tower;
            if (height > 1.8f) return BuildingTier.High;
            if (height > 1.0f) return BuildingTier.Mid;
            return BuildingTier.Low;
        }

        /// <summary>World bounds of the model turned by <paramref name="yaw"/>, measured on a temporary instance.</summary>
        private static Bounds MeasureTurned(GameObject model, float yaw)
        {
            var holder = new GameObject("measure");
            try
            {
                holder.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                var instance = (GameObject)Object.Instantiate(model, holder.transform, false);
                Renderer[] renderers = instance.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0)
                {
                    return new Bounds(Vector3.zero, Vector3.zero);
                }
                Bounds bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }
                return bounds;
            }
            finally
            {
                Object.DestroyImmediate(holder);
            }
        }

        /// <summary>
        /// Adds <paramref name="model"/> under <paramref name="root"/>, turned by <paramref name="yaw"/>,
        /// scaled by <paramref name="scale"/> along the piece's axes, with its measured footprint centred on
        /// <paramref name="center"/> and its base on y = 0.
        /// </summary>
        private static GameObject AddModel(Transform root, GameObject model, float yaw, Bounds turnedBounds, Vector3 center,
            Vector3 scale)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, root);
            instance.name = model.name;
            Transform t = instance.transform;
            // Keep the model asset's own root transform (FBX axis conversion, offsets) under the yaw -
            // it is what the bounds were measured with.
            Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
            Vector3 ownOffset = turn * model.transform.localPosition;
            t.localRotation = turn * model.transform.localRotation;

            // The stretch has to happen along the piece's axes, not the model's own (its FBX root may be
            // rotated), so the model hangs under an unrotated holder that carries the scale. Scaling the
            // holder scales the model about the piece root: bounds and own offset alike.
            var holder = new GameObject(model.name);
            holder.transform.SetParent(root, false);
            holder.transform.localScale = scale;
            t.SetParent(holder.transform, false);
            t.localScale = model.transform.localScale;
            Vector3 offset = ownOffset - new Vector3(turnedBounds.center.x, turnedBounds.min.y, turnedBounds.center.z);
            t.localPosition = offset;
            holder.transform.localPosition = center;
            return holder;
        }

        private static GameObject SavePiece(string name, string folder, System.Action<Transform> build)
        {
            var root = new GameObject(name);
            try
            {
                build(root.transform);
                return PrefabUtility.SaveAsPrefabAsset(root, $"{folder}/{name}.prefab");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }
            int slash = path.LastIndexOf('/');
            EnsureFolder(path.Substring(0, slash));
            AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
        }
    }
}
