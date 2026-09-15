using UnityEngine;
using UnityEngine.Tilemaps;

namespace OpenCity.Tiles
{
    /// <summary>
    /// Added to a road prefab instance that covers more than one Tilemap cell (e.g. the 3x3
    /// road-roundabout). Records which Tilemap and which cells it sits on, so the City
    /// Road Generator can find and replace it when the area is regenerated or cleared. The road
    /// cells underneath hold a RoadCoverTile.
    /// </summary>
    [DisallowMultipleComponent]
    public class MultiCellRoadPiece : MonoBehaviour
    {
        [Tooltip("The Tilemap whose cells this piece covers.")]
        public Tilemap tilemap;

        [Tooltip("The Tilemap cells this piece covers (only x/y are used).")]
        public BoundsInt cells;

        /// <summary>True if this piece covers any cell of <paramref name="area"/> on the X/Y plane.</summary>
        public bool Overlaps(BoundsInt area)
        {
            return cells.xMin < area.xMax && area.xMin < cells.xMax &&
                   cells.yMin < area.yMax && area.yMin < cells.yMax;
        }
    }
}
