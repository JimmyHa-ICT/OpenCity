using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace OpenCity.Tiles
{
    /// <summary>Why a BSP region became a block instead of being cut further.</summary>
    public enum BspLeafReason
    {
        /// <summary>The region was cut - it is not a block.</summary>
        NotLeaf,
        /// <summary>Too small (or no road to connect to) along both axes to fit a cut.</summary>
        TooSmall,
        /// <summary>Reached Max Split Depth.</summary>
        MaxDepth,
        /// <summary>Stopped early by the Stop Probability roll.</summary>
        StopRoll,
        /// <summary>Every cut position would have put two intersections side by side.</summary>
        NoCutPosition,
    }

    /// <summary>
    /// The BSP tree a City Road Generator (BSP Tilemap) run produced, stored in the scene next to
    /// the Tilemap it painted: every region the city was split into, from the whole city down to
    /// the blocks between the streets, with the cut that divided each one. Written by Generate and
    /// removed by Clear City Area, so it always describes the city that is actually painted. The
    /// Inspector shows it as a tree and highlights the selected region in the Scene view. Rects are
    /// city-local (0,0 = the city's south-west cell); <see cref="ToTilemapRect"/> converts them.
    /// </summary>
    [DisallowMultipleComponent]
    public class CityBspTree : MonoBehaviour
    {
        /// <summary>
        /// One region of the tree. <see cref="nodes"/> is in preorder, so a parent always comes
        /// before its children and index 0 is the root. Child and parent fields are indices into
        /// <see cref="nodes"/>, -1 for none.
        /// </summary>
        [Serializable]
        public struct Node
        {
            [Tooltip("The region between the roads that bound it, in city-local cells.")]
            public RectInt rect;
            [Tooltip("0 for the root region.")]
            public int depth;
            public int parent;
            [Tooltip("The west (vertical cut) or south (horizontal cut) child; -1 for a block.")]
            public int childA;
            [Tooltip("The east (vertical cut) or north (horizontal cut) child; -1 for a block.")]
            public int childB;
            [Tooltip("The 1-cell-wide road that splits this region, in city-local cells. Empty for a block.")]
            public RectInt cut;
            [Tooltip("True if the cut runs north-south (a column), false if it runs east-west (a row).")]
            public bool cutVertical;
            [Tooltip("Position of this block in generation order; -1 if the region was cut.")]
            public int blockIndex;
            public BspLeafReason leafReason;
            [Tooltip("True if this region's cut was decked over as an elevated expressway.")]
            public bool expressway;

            public bool IsBlock => childA < 0;
        }

        [Tooltip("The street Tilemap the city was painted on.")]
        public Tilemap tilemap;

        [Tooltip("Tilemap cell of the city's south-west corner.")]
        public Vector2Int originCell;

        [Tooltip("The city is a square of this many cells per side.")]
        public int sideLength;

        [Tooltip("The seed the layout was generated with.")]
        public int seed;

        [Tooltip("Every BSP region in preorder; index 0 is the root.")]
        public List<Node> nodes = new List<Node>();

        public int BlockCount
        {
            get
            {
                int count = 0;
                foreach (Node node in nodes)
                {
                    if (node.IsBlock)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        /// <summary>The city square in Tilemap cells.</summary>
        public BoundsInt CityArea => new BoundsInt(originCell.x, originCell.y, 0, sideLength, sideLength, 1);

        /// <summary>True if the city square covers any cell of <paramref name="area"/> on the X/Y plane.</summary>
        public bool Overlaps(BoundsInt area)
        {
            BoundsInt city = CityArea;
            return city.xMin < area.xMax && area.xMin < city.xMax &&
                   city.yMin < area.yMax && area.yMin < city.yMax;
        }

        /// <summary>A city-local rect in Tilemap cells.</summary>
        public RectInt ToTilemapRect(RectInt cityRect)
        {
            return new RectInt(cityRect.x + originCell.x, cityRect.y + originCell.y, cityRect.width, cityRect.height);
        }

        /// <summary>Every block, in generation order, in Tilemap cells.</summary>
        public List<RectInt> GetBlocks()
        {
            var blocks = new List<RectInt>();
            foreach (Node node in nodes)
            {
                if (node.IsBlock)
                {
                    blocks.Add(ToTilemapRect(node.rect));
                }
            }
            return blocks;
        }
    }
}
