using UnityEngine;
using UnityEngine.Tilemaps;

namespace OpenCity.Tiles
{
    /// <summary>
    /// Marks a Buildings Tilemap cell covered by a multi-cell building whose <see cref="BuildingTile"/>
    /// sits on another cell (the footprint's south-west corner). Draws and spawns nothing; it only
    /// makes the cell read as taken, for the generator's Clear and for anyone painting by hand.
    /// </summary>
    [CreateAssetMenu(fileName = "Building Cover Tile", menuName = "2D/Tiles/Building Cover Tile")]
    public class BuildingCoverTile : TileBase
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
    }
}
