using UnityEngine;
using UnityEngine.Tilemaps;

namespace OpenCity.Tiles
{
    /// <summary>
    /// RuleTile for the elevated highway, painted on its own Tilemap layer above the street
    /// Tilemap. It resolves deck shapes from its four neighbours exactly like RoadRuleTile, and
    /// adds one neighbour kind so the two ramp lengths can be told apart:
    /// a highway dead end is always a ramp foot, and the foot of a two-cell ramp is the one whose
    /// neighbour is a <see cref="RoadCoverTile"/> (the ramp's top cell, which the long ramp piece
    /// spawned from the foot already covers). A cover tile still counts as highway for every
    /// other rule, so the deck beyond the ramp connects into it.
    /// </summary>
    [CreateAssetMenu(fileName = "Highway Rule Tile", menuName = "2D/Tiles/Highway Rule Tile (XZ Grid)")]
    public class HighwayRuleTile : RoadRuleTile
    {
        /// <summary>Neighbour code: the cell holds a <see cref="RoadCoverTile"/> - the top of a two-cell ramp.</summary>
        public const int RampTop = 3;

        public override bool RuleMatch(int neighbor, TileBase other)
        {
            if (neighbor == RampTop)
            {
                return other is RoadCoverTile;
            }
            return base.RuleMatch(neighbor, other);
        }
    }
}
