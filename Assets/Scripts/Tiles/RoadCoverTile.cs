using UnityEngine;
using UnityEngine.Tilemaps;

namespace OpenCity.Tiles
{
    /// <summary>
    /// Marks a road cell whose model comes from a multi-cell piece placed on top of the Tilemap
    /// (e.g. the 3x3 road-roundabout, see MultiCellRoadPiece) instead of from the road
    /// RuleTile. It draws and spawns nothing itself, but RoadRuleTile treats it as road, so the
    /// road pieces around a multi-cell piece still connect into it.
    /// </summary>
    [CreateAssetMenu(fileName = "Road Cover Tile", menuName = "2D/Tiles/Road Cover Tile")]
    public class RoadCoverTile : TileBase
    {
        public override void GetTileData(Vector3Int position, ITilemap tilemap, ref TileData tileData)
        {
            tileData.sprite = null;
            tileData.gameObject = null;
            tileData.color = Color.white;
            tileData.transform = Matrix4x4.identity;
            tileData.flags = TileFlags.None;
            tileData.colliderType = Tile.ColliderType.None;
        }

        /// <summary>
        /// Also refresh the four neighbors: they are RuleTiles that pick their shape from whether
        /// this cell is road, and a plain TileBase would only refresh itself.
        /// </summary>
        public override void RefreshTile(Vector3Int position, ITilemap tilemap)
        {
            base.RefreshTile(position, tilemap);
            tilemap.RefreshTile(position + Vector3Int.up);
            tilemap.RefreshTile(position + Vector3Int.right);
            tilemap.RefreshTile(position + Vector3Int.down);
            tilemap.RefreshTile(position + Vector3Int.left);
        }
    }
}
