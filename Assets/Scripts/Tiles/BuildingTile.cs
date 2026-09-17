using UnityEngine;
using UnityEngine.Tilemaps;

namespace OpenCity.Tiles
{
    /// <summary>How tall a building is, which decides where in the city the generator puts it.</summary>
    public enum BuildingTier
    {
        /// <summary>Up to about one storey-block tall: the outskirts.</summary>
        Low,
        /// <summary>The ordinary city street.</summary>
        Mid,
        /// <summary>Along the main avenues and around downtown.</summary>
        High,
        /// <summary>Skyscrapers: downtown only.</summary>
        Tower,
        /// <summary>Plain background massing for the inside of a block, never on a street front.</summary>
        Filler,
    }

    /// <summary>
    /// One building type, painted on the Buildings Tilemap of a Grid with Cell Swizzle = XZY. The
    /// tile is painted on the south-west cell of the building's footprint (its anchor) and spawns
    /// <see cref="piece"/> centred on the whole footprint, turned to face the way the cell's
    /// transform matrix says: rotation 0 faces south (cell -Y, world -Z), and each quarter turn
    /// counter-clockwise about the cell's Z axis - what the Tile Palette's rotate buttons do -
    /// turns it a quarter to the east, north, then west. The other cells of a multi-cell footprint
    /// hold a <see cref="BuildingCoverTile"/> so they read as taken.
    ///
    /// The piece is a prefab whose root is the footprint's centre, with the model already turned so
    /// its front faces -Z and sitting on y = 0; the Building Tile builder makes them from the kit.
    /// </summary>
    [CreateAssetMenu(fileName = "New Building Tile", menuName = "2D/Tiles/Building Tile (XZ Grid)")]
    public class BuildingTile : TileBase
    {
        [Tooltip("The building piece: root at the footprint centre, front facing -Z, base on y = 0.")]
        public GameObject piece;

        [Tooltip("Cells the building covers at rotation 0: x along the street it faces, y deep into the block.")]
        public Vector2Int footprint = Vector2Int.one;

        [Tooltip("Decides where the generator puts it: Low on the outskirts, Tower downtown, Filler inside blocks.")]
        public BuildingTier tier = BuildingTier.Mid;

        [Tooltip("Relative chance of being picked among the buildings that fit the same spot.")]
        [Min(0f)] public float weight = 1f;

        [Tooltip("Optional sprite drawn by the Tile Palette so the tile can be told apart there. Not drawn in the scene.")]
        public Sprite previewSprite;

        public override void GetTileData(Vector3Int position, ITilemap tilemap, ref TileData tileData)
        {
            tileData.sprite = null;
            tileData.gameObject = piece;
            tileData.color = Color.white;
            tileData.transform = tilemap.GetTransformMatrix(position);
            // Not locked, so the rotation painted with the Tile Palette or set by the generator stays.
            tileData.flags = TileFlags.None;
            tileData.colliderType = Tile.ColliderType.None;

            // The Tilemap spawns the piece as soon as the tile is set - before a rotation painted with it
            // (by the generator, or by the Tile Palette's rotate buttons) reaches the cell - and a later
            // refresh calls this method but not StartUp again. So an already spawned piece is re-placed
            // here, from the matrix the cell has now.
            var map = tilemap.GetComponent<Tilemap>();
            if (map != null)
            {
                GameObject spawned = map.GetInstantiatedObject(position);
                if (spawned != null)
                {
                    Place(spawned.transform, position, map, tileData.transform);
                }
            }
        }

        /// <summary>
        /// Places the piece when it spawns. The Tilemap spawns it before the cell's rotation is set -
        /// whether the tile was just painted with a rotation or the scene is loading - so the rotation read
        /// here is still the default. In the Editor the piece is placed again right after the current
        /// operation, once the rotation has landed; in Play Mode <see cref="BuildingTilemap"/> does it.
        /// </summary>
        public override bool StartUp(Vector3Int position, ITilemap tilemap, GameObject instantiatedGameObject)
        {
            if (instantiatedGameObject == null)
            {
                return true;
            }
            var map = tilemap.GetComponent<Tilemap>();
            Place(instantiatedGameObject.transform, position, map, tilemap.GetTransformMatrix(position));
#if UNITY_EDITOR
            if (map != null && !Application.isPlaying)
            {
                GameObject spawned = instantiatedGameObject;
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    if (map != null && spawned != null && map.GetTile(position) == this &&
                        map.GetInstantiatedObject(position) == spawned)
                    {
                        Place(spawned.transform, position, map, map.GetTransformMatrix(position));
                    }
                };
            }
#endif
            return true;
        }

        /// <summary>Puts a spawned piece on the centre of its rotated footprint, turned to match the cell's matrix.</summary>
        public void Place(Transform piece, Vector3Int position, Tilemap map, Matrix4x4 cellMatrix)
        {
            int turns = QuarterTurns(cellMatrix);
            Vector2Int size = Rotated(footprint, turns);
            piece.localPosition = map != null
                ? map.CellToLocalInterpolated(new Vector3(position.x + size.x * 0.5f, position.y + size.y * 0.5f, position.z))
                : Vector3.zero;
            // On an XZY grid a counter-clockwise turn about cell Z is a clockwise one about world Y.
            piece.localRotation = Quaternion.Euler(0f, -90f * turns, 0f);
            piece.localScale = Vector3.one;
        }

        /// <summary>Quarter turns counter-clockwise (0-3) in a cell transform matrix's rotation about Z.</summary>
        public static int QuarterTurns(Matrix4x4 matrix)
        {
            Vector3 right = matrix.MultiplyVector(Vector3.right);
            float angle = Mathf.Atan2(right.y, right.x) * Mathf.Rad2Deg;
            return ((Mathf.RoundToInt(angle / 90f) % 4) + 4) % 4;
        }

        /// <summary>A rotation-0 footprint's size after <paramref name="turns"/> quarter turns.</summary>
        public static Vector2Int Rotated(Vector2Int size, int turns)
        {
            return (turns & 1) == 0 ? size : new Vector2Int(size.y, size.x);
        }

        /// <summary>The cell transform matrix for <paramref name="turns"/> quarter turns counter-clockwise.</summary>
        public static Matrix4x4 RotationMatrix(int turns)
        {
            return Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, 90f * (((turns % 4) + 4) % 4)));
        }
    }
}
