using UnityEngine;
using UnityEngine.Tilemaps;

namespace OpenCity.Tiles
{
    /// <summary>
    /// RuleTile for flat 3D road prefabs painted on a Grid with Cell Swizzle = XZY (lying on
    /// the world XZ ground plane). The stock RuleTile turns a matched "Rotated" rule into a
    /// rotation about Z, which is correct for sprites but stands a 3D road piece up on its
    /// edge. On an XZY grid, cell +Y is world +Z, so the same clockwise-from-above quarter
    /// turn is a rotation about world Y - this remaps it after the base placement.
    /// </summary>
    [CreateAssetMenu(fileName = "New Road Rule Tile", menuName = "2D/Tiles/Road Rule Tile (XZ Grid)")]
    public class RoadRuleTile : RuleTile
    {
        public override bool StartUp(Vector3Int position, ITilemap tilemap, GameObject instantiatedGameObject)
        {
            bool result = base.StartUp(position, tilemap, instantiatedGameObject);
            if (instantiatedGameObject != null)
            {
                // Base sets Euler(0, 0, -angle) for a rule matched at `angle` degrees clockwise.
                float z = instantiatedGameObject.transform.localEulerAngles.z;
                instantiatedGameObject.transform.localRotation = Quaternion.Euler(0f, -z, 0f);
            }
            return result;
        }

        /// <summary>
        /// A RoadCoverTile neighbor is road too - it sits under a multi-cell piece such as the 2x2
        /// curve intersection - so the pieces around it still connect into it.
        /// </summary>
        public override bool RuleMatch(int neighbor, TileBase other)
        {
            if (other is RoadCoverTile)
            {
                other = this;
            }
            return base.RuleMatch(neighbor, other);
        }
    }
}
