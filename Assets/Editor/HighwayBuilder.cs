using System.Collections.Generic;
using OpenCity.Tiles;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Turns a <see cref="HighwayPlanner.Plan"/> into tiles on the highway's own Tilemap layer, and
    /// creates the <see cref="HighwayRuleTile"/> asset those tiles resolve through.
    ///
    /// The highway is painted exactly like the streets are: one tile per cell, and the RuleTile
    /// picks each cell's piece from its highway neighbours. Because it lives on a separate Tilemap
    /// its neighbours are only ever other highway cells, so a deck running over a street never
    /// mixes with the street's own shapes. The tile's pieces are small prefabs this class builds
    /// out of the kit in Assets/Prefabs/Roads/Highway, each already standing at deck height, so the
    /// highway Tilemap sits on the ground plane like the street one:
    ///
    /// | Highway cell shape | Piece |
    /// |---|---|
    /// | straight | `road-bridge` (its own legs) |
    /// | bend / T / crossroads | the ground road piece for that shape on four `bridge-pillar` columns |
    /// | dead end next to a deck cell | `road-slant-high` (1-cell ramp) |
    /// | dead end next to a cover tile | `road-slant-curve` (2-cell ramp; the cover tile is its top) |
    ///
    /// Columns stand on the cell's corners, where a street's kerbs are, so an elevated turn may
    /// stand over a street junction as well as over a block. See Docs/CityHighwayGenerator.md.
    /// </summary>
    internal static class HighwayBuilder
    {
        public const string TilePath = "Assets/Tiles/Highway Rule Tile.asset";
        public const string PieceFolder = "Assets/Prefabs/Roads/Highway/Tile Pieces";

        private const string BridgePath = "Assets/Prefabs/Roads/Highway/road-bridge.prefab";
        private const string RampShortPath = "Assets/Prefabs/Roads/Highway/road-slant-high.prefab";
        private const string RampLongPath = "Assets/Prefabs/Roads/Highway/road-slant-curve.prefab";
        private const string PillarModelPath = "Assets/Models/bridge-pillar.fbx";
        private const string PillarFallbackPath = "Assets/Prefabs/Roads/Highway/tile-high.prefab";
        private const string BendPath = "Assets/Prefabs/Roads/road-bend.prefab";
        private const string TJunctionPath = "Assets/Prefabs/Roads/road-intersection.prefab";
        private const string FourWayPath = "Assets/Prefabs/Roads/road-crossroad.prefab";

        /// <summary>Height of `road-bridge`'s road surface above its pivot - the highway's deck height.</summary>
        public const float DeckHeight = 0.52f;

        /// <summary>How far a ground road piece's surface sits above its base, so one raised to the deck has its road on DeckHeight.</summary>
        private const float RoadSurfaceInset = 0.01f;

        /// <summary>Distance from a cell's center to each column of an elevated turn, along both axes.</summary>
        private const float PillarInset = 0.42f;

        /// <summary>Columns wider than this are scaled down on X/Z so four of them don't crowd the street below.</summary>
        private const float MaxPillarWidth = 0.14f;

        private static readonly Vector3Int[] FourNeighbors =
        {
            new Vector3Int(0, 1, 0), new Vector3Int(1, 0, 0), new Vector3Int(0, -1, 0), new Vector3Int(-1, 0, 0),
        };

        public readonly struct Result
        {
            public readonly int DeckTiles;
            public readonly int RampTiles;

            public Result(int deckTiles, int rampTiles)
            {
                DeckTiles = deckTiles;
                RampTiles = rampTiles;
            }
        }

        /// <summary>
        /// Paints <paramref name="plan"/> onto <paramref name="highwayTilemap"/>: every deck cell and
        /// ramp foot gets <paramref name="highwayTile"/>, the top cell of each two-cell ramp gets
        /// <paramref name="coverTile"/>. Assumes an Undo group is open and the Tilemap is registered.
        /// </summary>
        public static Result Paint(HighwayPlanner.Plan plan, Tilemap highwayTilemap, Vector2Int originCell,
            TileBase highwayTile, TileBase coverTile)
        {
            var positions = new List<Vector3Int>();
            var tiles = new List<TileBase>();
            foreach (Vector2Int cell in plan.DeckCellsSorted)
            {
                positions.Add(ToTilemapCell(originCell, cell));
                tiles.Add(highwayTile);
            }
            int deckTiles = positions.Count;
            foreach (HighwayPlanner.Ramp ramp in plan.Ramps)
            {
                positions.Add(ToTilemapCell(originCell, ramp.Foot));
                tiles.Add(highwayTile);
                for (int i = 1; i < ramp.Length; i++)
                {
                    positions.Add(ToTilemapCell(originCell, ramp.Foot + ramp.Direction * i));
                    tiles.Add(coverTile);
                }
            }
            highwayTilemap.SetTiles(positions.ToArray(), tiles.ToArray());
            EditorUtility.SetDirty(highwayTilemap);
            return new Result(deckTiles, positions.Count - deckTiles);
        }

        private static Vector3Int ToTilemapCell(Vector2Int originCell, Vector2Int cell)
        {
            return new Vector3Int(originCell.x + cell.x, originCell.y + cell.y, 0);
        }

        // ---------------------------------------------------------------
        // Asset creation
        // ---------------------------------------------------------------

        [MenuItem("Tools/OpenCity/Create Highway Rule Tile")]
        private static void CreateFromMenu()
        {
            HighwayRuleTile tile = CreateOrUpdateTile(out string error);
            if (tile != null)
            {
                Selection.activeObject = tile;
            }
            else
            {
                Debug.LogError($"[Highway] {error}");
            }
        }

        /// <summary>
        /// Builds the highway pieces into <see cref="PieceFolder"/> and writes the RuleTile to
        /// <see cref="TilePath"/>. Re-running it rebuilds both in place, keeping their GUIDs, so any
        /// Tilemap already painted with the tile picks the new pieces up on its next refresh.
        /// </summary>
        public static HighwayRuleTile CreateOrUpdateTile(out string error)
        {
            GameObject bridge = AssetDatabase.LoadAssetAtPath<GameObject>(BridgePath);
            GameObject rampShort = AssetDatabase.LoadAssetAtPath<GameObject>(RampShortPath);
            GameObject rampLong = AssetDatabase.LoadAssetAtPath<GameObject>(RampLongPath);
            GameObject bend = AssetDatabase.LoadAssetAtPath<GameObject>(BendPath);
            GameObject tJunction = AssetDatabase.LoadAssetAtPath<GameObject>(TJunctionPath);
            GameObject fourWay = AssetDatabase.LoadAssetAtPath<GameObject>(FourWayPath);
            GameObject pillar = AssetDatabase.LoadAssetAtPath<GameObject>(PillarModelPath) ??
                                AssetDatabase.LoadAssetAtPath<GameObject>(PillarFallbackPath);
            if (bridge == null || rampShort == null || rampLong == null || bend == null || tJunction == null ||
                fourWay == null || pillar == null)
            {
                error = "a highway kit asset is missing - expected road-bridge, road-slant-high, road-slant-curve " +
                        "(Assets/Prefabs/Roads/Highway), road-bend, road-intersection, road-crossroad " +
                        "(Assets/Prefabs/Roads) and bridge-pillar (Assets/Models).";
                return null;
            }

            EnsureFolder(PieceFolder);

            GameObject straightPiece = SavePiece("highway-straight", root => AddChild(root, bridge, Vector3.zero));
            GameObject bendPiece = SavePiece("highway-bend", root => BuildTurn(root, bend, pillar));
            GameObject tPiece = SavePiece("highway-intersection", root => BuildTurn(root, tJunction, pillar));
            GameObject fourWayPiece = SavePiece("highway-crossroad", root => BuildTurn(root, fourWay, pillar));
            // At rotation 0 every slant piece is high at its west end, which is where the foot's one
            // highway neighbour is in the rules below.
            GameObject rampShortPiece = SavePiece("highway-ramp-short", root => AddChild(root, rampShort, Vector3.zero));
            // The two-cell piece is pivoted on the edge its cells share: half a cell west of the foot.
            GameObject rampLongPiece = SavePiece("highway-ramp-long",
                root => AddChild(root, rampLong, new Vector3(-0.5f, 0f, 0f)));

            HighwayRuleTile tile = AssetDatabase.LoadAssetAtPath<HighwayRuleTile>(TilePath);
            bool created = tile == null;
            if (created)
            {
                tile = ScriptableObject.CreateInstance<HighwayRuleTile>();
            }

            const int This = RuleTile.TilingRuleOutput.Neighbor.This;
            const int Not = RuleTile.TilingRuleOutput.Neighbor.NotThis;
            const int Top = HighwayRuleTile.RampTop;
            tile.m_DefaultGameObject = null;
            tile.m_DefaultSprite = null;
            tile.m_TilingRules = new List<RuleTile.TilingRule>
            {
                // Neighbours are N, E, S, W. Order matters: the first matching rule wins, and a cover
                // tile also counts as This, so the long ramp has to be tried before the short one.
                Rule(0, fourWayPiece, false, This, This, This, This),
                Rule(1, tPiece, true, This, This, Not, This),
                Rule(2, straightPiece, true, Not, This, Not, This),
                Rule(3, bendPiece, true, This, This, Not, Not),
                Rule(4, rampLongPiece, true, Not, Not, Not, Top),
                Rule(5, rampShortPiece, true, Not, Not, Not, This),
            };

            if (created)
            {
                AssetDatabase.CreateAsset(tile, TilePath);
            }
            EditorUtility.SetDirty(tile);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Highway] {(created ? "Created" : "Updated")} {TilePath} and its pieces in {PieceFolder}.");
            error = null;
            return tile;
        }

        private static RuleTile.TilingRule Rule(int id, GameObject piece, bool rotated, int north, int east, int south, int west)
        {
            return new RuleTile.TilingRule
            {
                m_Id = id,
                m_GameObject = piece,
                m_Sprites = new Sprite[1],
                m_RuleTransform = rotated ? RuleTile.TilingRuleOutput.Transform.Rotated : RuleTile.TilingRuleOutput.Transform.Fixed,
                m_Neighbors = new List<int> { north, east, south, west },
                m_NeighborPositions = new List<Vector3Int>(FourNeighbors),
            };
        }

        /// <summary>An elevated turn: the ground road piece for its shape, lifted onto four columns at the cell corners.</summary>
        private static void BuildTurn(Transform root, GameObject road, GameObject pillar)
        {
            float top = DeckHeight - RoadSurfaceInset;
            AddChild(root, road, new Vector3(0f, top, 0f));

            Bounds bounds = RoadGridPlacer.MeasureRendererBounds(pillar);
            Vector3 rootOffset = pillar.transform.localPosition;
            float width = Mathf.Max(bounds.size.x, bounds.size.z);
            float scaleXZ = width > MaxPillarWidth ? MaxPillarWidth / width : 1f;
            float scaleY = bounds.size.y > 0.0001f ? top / bounds.size.y : 1f;
            Vector3 center = bounds.center - rootOffset;
            float baseY = bounds.min.y - rootOffset.y;
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(pillar, root);
                    instance.name = pillar.name;
                    instance.transform.localRotation = Quaternion.identity;
                    instance.transform.localScale = new Vector3(scaleXZ, scaleY, scaleXZ);
                    instance.transform.localPosition = new Vector3(
                        sx * PillarInset - center.x * scaleXZ,
                        -baseY * scaleY,
                        sz * PillarInset - center.z * scaleXZ);
                }
            }
        }

        private static void AddChild(Transform root, GameObject prefab, Vector3 localCenter)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root);
            instance.name = prefab.name;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            instance.transform.localPosition = localCenter + RoadGridPlacer.ComputePivotOffset(prefab);
        }

        private static GameObject SavePiece(string name, System.Action<Transform> build)
        {
            var root = new GameObject(name);
            try
            {
                build(root.transform);
                return PrefabUtility.SaveAsPrefabAsset(root, $"{PieceFolder}/{name}.prefab");
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
