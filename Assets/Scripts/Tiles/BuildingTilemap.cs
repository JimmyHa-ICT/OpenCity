using UnityEngine;
using UnityEngine.Tilemaps;

namespace OpenCity.Tiles
{
    /// <summary>
    /// Goes on a Tilemap painted with <see cref="BuildingTile"/>s. When a scene loads, the Tilemap spawns
    /// each building before the cell's rotation is visible to <see cref="BuildingTile.StartUp"/>, so a
    /// rotated or multi-cell building would come up turned to face south and off its footprint. Once
    /// everything has spawned (Start runs after every OnEnable), this puts each one back on its rotated
    /// footprint. Runs in the Editor too. The City Road Generator adds it to the Buildings layer.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Tilemap))]
    public class BuildingTilemap : MonoBehaviour
    {
        private void Start()
        {
            Realign();
        }

        /// <summary>Re-places every spawned building from its cell's current rotation.</summary>
        public void Realign()
        {
            var map = GetComponent<Tilemap>();
            if (map == null)
            {
                return;
            }
            foreach (Vector3Int cell in map.cellBounds.allPositionsWithin)
            {
                if (map.GetTile(cell) is BuildingTile tile)
                {
                    GameObject spawned = map.GetInstantiatedObject(cell);
                    if (spawned != null)
                    {
                        tile.Place(spawned.transform, cell, map, map.GetTransformMatrix(cell));
                    }
                }
            }
        }
    }
}
