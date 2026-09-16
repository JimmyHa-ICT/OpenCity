using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Pure integer-grid Binary Space Partitioning for the City Road Generator (BSP Tilemap):
    /// lays out a square city of SideLength x SideLength cells as an optional perimeter ring
    /// road plus a recursive set of 1-cell-wide axis-aligned cuts, and returns the road cells
    /// and the leaf blocks between them. Works entirely in cells (never world units) so a cut
    /// is a whole column or row of cells by construction - there is no rasterization or
    /// rounding step, which is what lets it guarantee every road is exactly one cell wide and
    /// any two parallel roads have at least MinBlockSize block cells between them. Contrast
    /// BspGeometry, which cuts in continuous world space for the spline-bounded sub-road tool
    /// and has to rasterize afterwards. Coordinates are city-local: (0,0) is the bottom-left
    /// (south-west) cell, +X is east, +Y is north. Deliberately has no UnityEditor or Tilemap
    /// dependency. See Docs/CityBspTilemapGenerator.md.
    /// </summary>
    internal static class CityBspLayout
    {
        /// <summary>Which sides of a region are bounded by a road (as opposed to the bare city edge).</summary>
        [Flags]
        private enum Side
        {
            None = 0,
            South = 1,
            North = 2,
            West = 4,
            East = 8,
            All = South | North | West | East,
        }

        public readonly struct Settings : IEquatable<Settings>
        {
            public readonly int SideLength;
            public readonly bool PerimeterRoad;
            public readonly int MaxSplitDepth;
            public readonly int MinBlockSize;
            public readonly int MaxBlockSize;
            public readonly float SplitJitter;
            public readonly float SplitAxisBias;
            public readonly float StopProbability;
            public readonly int Seed;

            public Settings(int sideLength, bool perimeterRoad, int maxSplitDepth, int minBlockSize,
                int maxBlockSize, float splitJitter, float splitAxisBias, float stopProbability, int seed)
            {
                SideLength = sideLength;
                PerimeterRoad = perimeterRoad;
                MaxSplitDepth = maxSplitDepth;
                MinBlockSize = minBlockSize;
                MaxBlockSize = maxBlockSize;
                SplitJitter = splitJitter;
                SplitAxisBias = splitAxisBias;
                StopProbability = stopProbability;
                Seed = seed;
            }

            public bool Equals(Settings other)
            {
                return SideLength == other.SideLength &&
                       PerimeterRoad == other.PerimeterRoad &&
                       MaxSplitDepth == other.MaxSplitDepth &&
                       MinBlockSize == other.MinBlockSize &&
                       MaxBlockSize == other.MaxBlockSize &&
                       SplitJitter.Equals(other.SplitJitter) &&
                       SplitAxisBias.Equals(other.SplitAxisBias) &&
                       StopProbability.Equals(other.StopProbability) &&
                       Seed == other.Seed;
            }

            public override bool Equals(object obj)
            {
                return obj is Settings other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(
                    HashCode.Combine(SideLength, PerimeterRoad, MaxSplitDepth, MinBlockSize, MaxBlockSize),
                    SplitJitter, SplitAxisBias, StopProbability, Seed);
            }
        }

        /// <summary>
        /// One region of the BSP tree, in the order SplitRegion visited it (preorder). Kept so
        /// callers can read the tree back at a chosen depth - see <see cref="Districts"/> - which
        /// is how the highway planner groups the city into clusters. The rect is the region
        /// *between* the roads that bound it, so it includes every cut made inside it but not the
        /// roads on its edges.
        /// </summary>
        public readonly struct Node
        {
            public readonly RectInt Rect;
            /// <summary>0 for the root region (the whole city, or its interior with a ring road).</summary>
            public readonly int Depth;
            /// <summary>True if this region was never cut, i.e. it is one of <see cref="Layout.Blocks"/>.</summary>
            public readonly bool IsLeaf;

            public Node(RectInt rect, int depth, bool isLeaf)
            {
                Rect = rect;
                Depth = depth;
                IsLeaf = isLeaf;
            }
        }

        public sealed class Layout
        {
            public readonly int SideLength;
            public readonly HashSet<Vector2Int> RoadCells;
            /// <summary>RoadCells sorted by (y, x) - a stable order for painting and comparison.</summary>
            public readonly List<Vector2Int> RoadCellsSorted;
            /// <summary>The leaf regions between roads, in traversal order. Never overlap a road cell or each other.</summary>
            public readonly List<RectInt> Blocks;
            /// <summary>Every BSP cut as a 1-cell-wide run (width or height 1), in emission order. Excludes the perimeter ring.</summary>
            public readonly List<RectInt> Cuts;
            public readonly int PerimeterCellCount;
            /// <summary>Splits Max Block Size forced on a region that Max Split Depth or Stop Probability would otherwise have stopped.</summary>
            public readonly int ForcedSplitCount;
            /// <summary>Regions that wanted to split but were kept whole because every cut position would have put two intersections side by side.</summary>
            public readonly int SpacingStopCount;
            /// <summary>Every region of the BSP tree in preorder, leaves included. See <see cref="Node"/> and <see cref="Districts"/>.</summary>
            public readonly List<Node> Nodes;

            public Layout(int sideLength, HashSet<Vector2Int> roadCells, List<RectInt> blocks, List<RectInt> cuts,
                int perimeterCellCount, int forcedSplitCount, int spacingStopCount, List<Node> nodes)
            {
                SideLength = sideLength;
                RoadCells = roadCells;
                RoadCellsSorted = new List<Vector2Int>(roadCells);
                RoadCellsSorted.Sort((p, q) => p.y != q.y ? p.y.CompareTo(q.y) : p.x.CompareTo(q.x));
                Blocks = blocks;
                Cuts = cuts;
                PerimeterCellCount = perimeterCellCount;
                ForcedSplitCount = forcedSplitCount;
                SpacingStopCount = spacingStopCount;
                Nodes = nodes;
            }
        }

        /// <summary>
        /// The BSP tree's frontier at <paramref name="depth"/>: every region that was cut exactly
        /// <paramref name="depth"/> times, plus every leaf that stopped splitting before it got
        /// that far. Those regions tile the root region exactly once - no gaps, no overlaps - so
        /// they are a partition of the city into 1 (depth 0), up to 2 (depth 1), up to 4 (depth 2)
        /// ... clusters, following the same cuts the streets follow. Returned in the tree's
        /// preorder, which is deterministic.
        /// </summary>
        public static List<RectInt> Districts(Layout layout, int depth)
        {
            var result = new List<RectInt>();
            if (layout == null)
            {
                return result;
            }
            int wanted = Mathf.Max(0, depth);
            foreach (Node node in layout.Nodes)
            {
                if (node.Depth == wanted || (node.IsLeaf && node.Depth < wanted))
                {
                    result.Add(node.Rect);
                }
            }
            return result;
        }

        private sealed class Counters
        {
            public int ForcedSplits;
            public int SpacingStops;
        }

        /// <summary>
        /// Builds the whole layout. Pure function of <paramref name="settings"/>: the same
        /// settings always produce the same cells, blocks and cuts in the same order. Uses a
        /// System.Random seeded fresh from Settings.Seed - never UnityEngine.Random, whose global
        /// state is shared editor-wide and would let the live preview drift from Generate.
        /// </summary>
        public static Layout Build(Settings settings)
        {
            int n = Mathf.Max(0, settings.SideLength);
            var roads = new HashSet<Vector2Int>();
            var blocks = new List<RectInt>();
            var cuts = new List<RectInt>();
            var nodes = new List<Node>();
            var counters = new Counters();

            if (n == 0)
            {
                return new Layout(0, roads, blocks, cuts, 0, 0, 0, nodes);
            }

            RectInt root;
            Side rootRoadSides;
            if (settings.PerimeterRoad)
            {
                for (int i = 0; i < n; i++)
                {
                    roads.Add(new Vector2Int(i, 0));
                    roads.Add(new Vector2Int(i, n - 1));
                    roads.Add(new Vector2Int(0, i));
                    roads.Add(new Vector2Int(n - 1, i));
                }
                root = n > 2 ? new RectInt(1, 1, n - 2, n - 2) : new RectInt(0, 0, 0, 0);
                rootRoadSides = Side.All;
            }
            else
            {
                root = new RectInt(0, 0, n, n);
                rootRoadSides = Side.None;
            }
            int perimeterCells = roads.Count;

            if (root.width > 0 && root.height > 0)
            {
                var rng = new System.Random(settings.Seed);
                SplitRegion(root, rootRoadSides, 0, settings, rng, roads, blocks, cuts, nodes, counters);
            }

            return new Layout(n, roads, blocks, cuts, perimeterCells, counters.ForcedSplits, counters.SpacingStops, nodes);
        }

        private static void SplitRegion(
            RectInt region, Side roadSides, int depth, Settings s, System.Random rng,
            HashSet<Vector2Int> roads, List<RectInt> blocks, List<RectInt> cuts, List<Node> nodes, Counters counters)
        {
            // Recorded as a leaf up front and rewritten in place if this region turns out to be
            // cut, so `nodes` ends up in preorder (a parent always before its two children)
            // whichever way the decisions below go.
            int nodeIndex = nodes.Count;
            nodes.Add(new Node(region, depth, true));

            int minBlock = Mathf.Max(1, s.MinBlockSize);

            // A cut consumes one column/row and must leave at least minBlock cells on BOTH sides,
            // so an axis is only wide enough if the region spans 2 * minBlock + 1 cells along it.
            // This is also what keeps every road separated from every parallel road.
            bool wideEnough = region.width >= minBlock * 2 + 1;
            bool tallEnough = region.height >= minBlock * 2 + 1;

            // Connectivity: a cut spans its whole region, so its two ends touch whatever bounds
            // the region there - a road, or (with no perimeter ring) the bare city edge. A cut
            // with the city edge at BOTH ends would touch no other road at all, so a vertical cut
            // needs a road south or north of the region, and a horizontal cut one west or east.
            // The only exemption is the very first cut of a ring-less city (no roads exist yet):
            // it becomes the road everything else attaches to.
            bool firstRoad = roadSides == Side.None;
            bool canSplitVertical = wideEnough && (firstRoad || (roadSides & (Side.South | Side.North)) != 0);
            bool canSplitHorizontal = tallEnough && (firstRoad || (roadSides & (Side.West | Side.East)) != 0);
            if (!canSplitVertical && !canSplitHorizontal)
            {
                blocks.Add(region);
                return;
            }

            // Max Block Size: an oversized side that is wide enough to cut forces a split. If the
            // connectivity rule blocks the axis that would fix it, cutting the other axis first
            // gives the children the road they need to fix it one level down.
            bool widthFixable = s.MaxBlockSize > 0 && region.width > s.MaxBlockSize && wideEnough;
            bool heightFixable = s.MaxBlockSize > 0 && region.height > s.MaxBlockSize && tallEnough;
            bool verticalFixesOversize = widthFixable && canSplitVertical;
            bool horizontalFixesOversize = heightFixable && canSplitHorizontal;
            bool mustSplit = widthFixable || heightFixable;

            // The root is exempt from the stop roll, so an unlucky first draw can never leave an
            // otherwise splittable city with no interior roads at all.
            bool stopByDepth = depth >= Mathf.Max(0, s.MaxSplitDepth);
            bool stopByRoll = !stopByDepth && depth > 0 && rng.NextDouble() < s.StopProbability;
            bool forced = stopByDepth || stopByRoll;
            if (forced && !mustSplit)
            {
                blocks.Add(region);
                return;
            }

            bool preferVertical = region.width >= region.height; // cut across the longer side
            bool followBias = rng.NextDouble() < Mathf.Clamp01(s.SplitAxisBias);
            bool wantVertical = followBias ? preferVertical : !preferVertical;
            if (mustSplit)
            {
                // Never let the axis roll pick a cut that leaves an oversized side oversized.
                if (wantVertical && !verticalFixesOversize && horizontalFixesOversize)
                {
                    wantVertical = false;
                }
                else if (!wantVertical && !horizontalFixesOversize && verticalFixesOversize)
                {
                    wantVertical = true;
                }
            }
            bool vertical = wantVertical ? canSplitVertical : !canSplitHorizontal;

            float t = 0.5f + (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Clamp01(s.SplitJitter);
            int offset = PickOffset(region, vertical, t, minBlock, roads);
            if (offset < 0 && (vertical ? canSplitHorizontal : canSplitVertical))
            {
                vertical = !vertical;
                offset = PickOffset(region, vertical, t, minBlock, roads);
            }
            if (offset < 0)
            {
                counters.SpacingStops++;
                blocks.Add(region);
                return;
            }
            if (forced)
            {
                counters.ForcedSplits++;
            }

            RectInt cut = CutAt(region, vertical, offset);
            RectInt childA;
            RectInt childB;
            Side childASides;
            Side childBSides;
            if (vertical)
            {
                childA = new RectInt(region.x, region.y, offset, region.height);
                childB = new RectInt(cut.x + 1, region.y, region.width - offset - 1, region.height);
                childASides = (roadSides & ~Side.East) | Side.East;
                childBSides = (roadSides & ~Side.West) | Side.West;
            }
            else
            {
                childA = new RectInt(region.x, region.y, region.width, offset);
                childB = new RectInt(region.x, cut.y + 1, region.width, region.height - offset - 1);
                childASides = (roadSides & ~Side.North) | Side.North;
                childBSides = (roadSides & ~Side.South) | Side.South;
            }

            nodes[nodeIndex] = new Node(region, depth, false);
            cuts.Add(cut);
            for (int x = cut.xMin; x < cut.xMax; x++)
            {
                for (int y = cut.yMin; y < cut.yMax; y++)
                {
                    roads.Add(new Vector2Int(x, y));
                }
            }

            // Fixed child order: every rng draw is a function of the traversal, so this order is
            // part of the determinism guarantee.
            SplitRegion(childA, childASides, depth + 1, s, rng, roads, blocks, cuts, nodes, counters);
            SplitRegion(childB, childBSides, depth + 1, s, rng, roads, blocks, cuts, nodes, counters);
        }

        /// <summary>
        /// The cut offset (cells between the region's start and the cut) closest to the jittered
        /// target <paramref name="t"/> that keeps both children at least minBlock cells and doesn't
        /// put two intersections side by side (see EndsBesideJunction). Searches outward from the
        /// target, lower offset first on ties. -1 if no offset qualifies.
        /// </summary>
        private static int PickOffset(RectInt region, bool vertical, float t, int minBlock, HashSet<Vector2Int> roads)
        {
            int extent = vertical ? region.width : region.height;
            int lo = minBlock;
            int hi = extent - 1 - minBlock;
            if (hi < lo)
            {
                return -1;
            }
            int target = Mathf.Clamp(Mathf.RoundToInt(t * (extent - 1)), lo, hi);
            for (int distance = 0; distance <= hi - lo; distance++)
            {
                int lower = target - distance;
                if (lower >= lo && !EndsBesideJunction(CutAt(region, vertical, lower), vertical, roads))
                {
                    return lower;
                }
                int upper = target + distance;
                if (distance > 0 && upper <= hi && !EndsBesideJunction(CutAt(region, vertical, upper), vertical, roads))
                {
                    return upper;
                }
            }
            return -1;
        }

        private static RectInt CutAt(RectInt region, bool vertical, int offset)
        {
            return vertical
                ? new RectInt(region.x + offset, region.y, 1, region.height)
                : new RectInt(region.x, region.y + offset, region.width, 1);
        }

        /// <summary>
        /// True if <paramref name="cut"/> would end on a road one cell away from where another
        /// road already meets that same road from the far side - i.e. its T-junction would sit
        /// right beside an existing T-junction or crossroad. A cut's end cell touches the road
        /// that bounds its region; the only way a junction can already exist right next to that
        /// spot is a road arriving from the OTHER side, which occupies the cell diagonally beyond
        /// the end. (A road arriving from the same side would be an adjacent parallel road, which
        /// minBlock already rules out; a road arriving at exactly the same spot is a crossroad,
        /// which is fine.) Because this looks across the road rather than at cuts made earlier,
        /// it catches the pair no matter which of the two cuts is placed second.
        /// </summary>
        private static bool EndsBesideJunction(RectInt cut, bool vertical, HashSet<Vector2Int> roads)
        {
            if (vertical)
            {
                int x = cut.x;
                int beyondSouth = cut.yMin - 2;
                int beyondNorth = cut.yMax + 1;
                return roads.Contains(new Vector2Int(x - 1, beyondSouth)) || roads.Contains(new Vector2Int(x + 1, beyondSouth)) ||
                       roads.Contains(new Vector2Int(x - 1, beyondNorth)) || roads.Contains(new Vector2Int(x + 1, beyondNorth));
            }
            int y = cut.y;
            int beyondWest = cut.xMin - 2;
            int beyondEast = cut.xMax + 1;
            return roads.Contains(new Vector2Int(beyondWest, y - 1)) || roads.Contains(new Vector2Int(beyondWest, y + 1)) ||
                   roads.Contains(new Vector2Int(beyondEast, y - 1)) || roads.Contains(new Vector2Int(beyondEast, y + 1));
        }
    }
}
