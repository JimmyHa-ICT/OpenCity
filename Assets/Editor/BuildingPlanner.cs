using System;
using System.Collections.Generic;
using OpenCity.Tiles;
using UnityEngine;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Fills the blocks of a BSP city with buildings, as Buildings Tilemap placements. Pure: it reads
    /// the layout (and optionally the expressway plan) and returns anchors, footprints and rotations;
    /// the window paints them.
    ///
    /// What makes the result read as a city rather than scattered boxes:
    /// 1. **Every building fronts a street.** Buildings are placed side by side along each block edge
    ///    that faces a road, turned to face it, flush with the edge - nothing is built in a block's
    ///    middle, where it would have no way in. The best street claims a block's corners first.
    /// 2. **Frontages are packed, not walked.** For each street edge every combination of footprints
    ///    (widths along the street, depths into the block) is scored and the one covering the most of
    ///    the block wins, so deep blocks get deep buildings and gaps get buildings that fit them
    ///    exactly. A street facing another street across the block only takes half the depth, leaving
    ///    the rest to the buildings facing that street.
    /// 3. **Height follows land value.** Each block gets a value from how central it is, how important
    ///    the avenues around it are (the BSP depth of the cuts bounding it: the depth-0 avenue is the
    ///    main street) and how close it is to an expressway ramp. The value picks the tier of every lot
    ///    in the block: towers downtown, high-rises along the avenues, low-rise on the outskirts.
    /// 4. **Variety without noise.** Among the buildings with the chosen footprint the same building is
    ///    rarely repeated next to itself along a street, and low-value streets keep the odd open lot.
    ///
    /// Deterministic: every block has its own random stream seeded from the seed and the block index,
    /// so changing one block's surroundings doesn't reshuffle the whole city. City-local cells.
    /// See Docs/CityBuildingGenerator.md.
    /// </summary>
    internal static class BuildingPlanner
    {
        public readonly struct Entry
        {
            /// <summary>Index of the building in the caller's catalog.</summary>
            public readonly int Id;
            /// <summary>Rotation-0 footprint: x along the street, y deep into the block.</summary>
            public readonly Vector2Int Footprint;
            public readonly BuildingTier Tier;
            public readonly float Weight;

            public Entry(int id, Vector2Int footprint, BuildingTier tier, float weight)
            {
                Id = id;
                Footprint = new Vector2Int(Mathf.Max(1, footprint.x), Mathf.Max(1, footprint.y));
                Tier = tier;
                Weight = Mathf.Max(0f, weight);
            }
        }

        public readonly struct Settings : IEquatable<Settings>
        {
            public readonly int Seed;
            /// <summary>0-1: the chance a lot on an ordinary street is built at all (downtown is always built).</summary>
            public readonly float Density;
            /// <summary>0-1: how much of the city counts as downtown. Higher = taller skyline over more of the city.</summary>
            public readonly float Downtown;

            public Settings(int seed, float density, float downtown)
            {
                Seed = seed;
                Density = density;
                Downtown = downtown;
            }

            public bool Equals(Settings other)
            {
                return Seed == other.Seed && Density.Equals(other.Density) && Downtown.Equals(other.Downtown);
            }

            public override bool Equals(object obj)
            {
                return obj is Settings other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(Seed, Density, Downtown);
            }
        }

        public readonly struct Placement
        {
            /// <summary>Catalog index of the building.</summary>
            public readonly int Id;
            /// <summary>South-west cell of the footprint - where the tile goes.</summary>
            public readonly Vector2Int Anchor;
            /// <summary>The footprint as it lies on the grid (already rotated).</summary>
            public readonly Vector2Int Size;
            /// <summary>Quarter turns counter-clockwise: 0 faces south, 1 east, 2 north, 3 west.</summary>
            public readonly int Turns;
            public readonly BuildingTier Tier;
            public readonly int BlockIndex;

            public Placement(int id, Vector2Int anchor, Vector2Int size, int turns, BuildingTier tier, int blockIndex)
            {
                Id = id;
                Anchor = anchor;
                Size = size;
                Turns = turns;
                Tier = tier;
                BlockIndex = blockIndex;
            }

            public RectInt Rect => new RectInt(Anchor, Size);

            public IEnumerable<Vector2Int> Cells
            {
                get
                {
                    for (int x = 0; x < Size.x; x++)
                    {
                        for (int y = 0; y < Size.y; y++)
                        {
                            yield return new Vector2Int(Anchor.x + x, Anchor.y + y);
                        }
                    }
                }
            }
        }

        public sealed class Plan
        {
            /// <summary>Buildings block by block, each block's best street first.</summary>
            public readonly List<Placement> Placements;
            /// <summary>Land value 0-1 of each block, in Layout.Blocks order.</summary>
            public readonly List<float> BlockValues;
            /// <summary>Buildings per tier, indexed by (int)BuildingTier.</summary>
            public readonly int[] TierCounts;
            /// <summary>Street-front lots deliberately left open.</summary>
            public readonly int OpenLots;
            /// <summary>Street-front cells no building in the catalog could fill.</summary>
            public readonly int UnfilledFrontage;
            /// <summary>Block cells in the city, and how many a building covers.</summary>
            public readonly int BlockCells;
            public readonly int BuiltCells;

            public Plan(List<Placement> placements, List<float> blockValues, int openLots, int unfilledFrontage, int blockCells)
            {
                Placements = placements;
                BlockValues = blockValues;
                OpenLots = openLots;
                UnfilledFrontage = unfilledFrontage;
                BlockCells = blockCells;
                TierCounts = new int[Enum.GetValues(typeof(BuildingTier)).Length];
                foreach (Placement placement in placements)
                {
                    TierCounts[(int)placement.Tier]++;
                    BuiltCells += placement.Size.x * placement.Size.y;
                }
            }

            public bool IsEmpty => Placements.Count == 0;
        }

        /// <summary>A block edge that fronts a street.</summary>
        private readonly struct Frontage
        {
            /// <summary>0 south, 1 east, 2 north, 3 west - also the quarter turns that face it.</summary>
            public readonly int Side;
            /// <summary>Lower is a more important street: BSP depth of the cut, a large number for the ring or city edge.</summary>
            public readonly int Rank;
            public readonly int Length;

            public Frontage(int side, int rank, int length)
            {
                Side = side;
                Rank = rank;
                Length = length;
            }
        }

        /// <summary>The street-front buildings of one footprint, as the packer sees them.</summary>
        private sealed class Shape
        {
            public readonly Vector2Int Footprint;
            public readonly List<Entry> Entries = new List<Entry>();

            public Shape(Vector2Int footprint)
            {
                Footprint = footprint;
            }
        }

        private const int RingRank = 100;

        /// <summary>
        /// Plans every block. <paramref name="reserved"/> are block cells nothing may be built on (the
        /// corners of a roundabout's 3x3 footprint). <paramref name="highways"/> may be null.
        /// </summary>
        public static Plan Build(CityBspLayout.Layout layout, HighwayPlanner.Plan highways, HashSet<Vector2Int> reserved,
            IReadOnlyList<Entry> catalog, Settings settings)
        {
            var placements = new List<Placement>();
            var values = new List<float>(layout.Blocks.Count);
            int openLots = 0;
            int unfilled = 0;
            int blockCells = 0;
            foreach (RectInt block in layout.Blocks)
            {
                blockCells += block.width * block.height;
            }

            Dictionary<Vector2Int, int> cutDepth = CutDepths(layout);
            for (int i = 0; i < layout.Blocks.Count; i++)
            {
                values.Add(BlockValue(layout, highways, cutDepth, i, settings.Downtown));
            }
            List<Shape> shapes = Shapes(catalog);
            if (shapes.Count == 0)
            {
                return new Plan(placements, values, 0, 0, blockCells);
            }

            var occupied = new HashSet<Vector2Int>();
            if (reserved != null)
            {
                occupied.UnionWith(reserved);
            }

            for (int blockIndex = 0; blockIndex < layout.Blocks.Count; blockIndex++)
            {
                RectInt block = layout.Blocks[blockIndex];
                var rng = new System.Random(unchecked(settings.Seed * 486187739 + blockIndex * 16777619 + 97));
                List<Frontage> frontages = Frontages(block, layout, cutDepth);
                var packed = new bool[4];
                foreach (Frontage frontage in frontages)
                {
                    int depth = frontage.Side == 0 || frontage.Side == 2 ? block.height : block.width;
                    int opposite = (frontage.Side + 2) % 4;
                    // The street across the block still to come gets the other half of the depth; one
                    // already packed, or no street at all, leaves whatever it didn't build on.
                    if (!packed[opposite] && frontages.Exists(f => f.Side == opposite))
                    {
                        depth = (depth + 1) / 2;
                    }
                    PackFrontage(shapes, block, frontage, depth, occupied, blockIndex, values[blockIndex], settings, rng,
                        placements, ref openLots, ref unfilled);
                    packed[frontage.Side] = true;
                }
            }

            return new Plan(placements, values, openLots, unfilled, blockCells);
        }

        // ---------------------------------------------------------------
        // Land value
        // ---------------------------------------------------------------

        /// <summary>BSP depth of the cut every cut cell belongs to (the shallowest, where two cross).</summary>
        private static Dictionary<Vector2Int, int> CutDepths(CityBspLayout.Layout layout)
        {
            var depths = new Dictionary<Vector2Int, int>();
            foreach (CityBspLayout.Node node in layout.Nodes)
            {
                if (node.IsLeaf)
                {
                    continue;
                }
                RectInt cut = node.Cut;
                for (int x = cut.xMin; x < cut.xMax; x++)
                {
                    for (int y = cut.yMin; y < cut.yMax; y++)
                    {
                        var cell = new Vector2Int(x, y);
                        if (!depths.TryGetValue(cell, out int existing) || node.Depth < existing)
                        {
                            depths[cell] = node.Depth;
                        }
                    }
                }
            }
            return depths;
        }

        /// <summary>
        /// 0-1. Mostly centrality, then the importance of the best avenue bounding the block, then
        /// expressway access. <paramref name="downtown"/> bends the curve: at 1 most of the city scores high.
        /// </summary>
        private static float BlockValue(CityBspLayout.Layout layout, HighwayPlanner.Plan highways,
            Dictionary<Vector2Int, int> cutDepth, int blockIndex, float downtown)
        {
            RectInt block = layout.Blocks[blockIndex];
            float half = layout.SideLength * 0.5f;
            Vector2 center = block.center;
            float centrality = 1f - Mathf.Clamp01(Vector2.Distance(center, new Vector2(half, half)) / (half * 1.2f));

            int bestDepth = int.MaxValue;
            foreach (Vector2Int cell in BorderCells(block))
            {
                if (cutDepth.TryGetValue(cell, out int depth) && depth < bestDepth)
                {
                    bestDepth = depth;
                }
            }
            float avenue = bestDepth == int.MaxValue ? 0f : 1f / (1 + bestDepth);

            float value;
            if (highways != null && !highways.IsEmpty && blockIndex < highways.BlockDistances.Count &&
                highways.BlockDistances[blockIndex] >= 0)
            {
                float access = 1f - Mathf.Clamp01(highways.BlockDistances[blockIndex] / half);
                value = 0.55f * centrality + 0.25f * avenue + 0.2f * access;
            }
            else
            {
                value = 0.65f * centrality + 0.35f * avenue;
            }
            // downtown 0.5 leaves the value as is; 1 lifts it (exponent 0.4), 0 flattens it (exponent 2.5).
            float exponent = Mathf.Lerp(2.5f, 0.4f, Mathf.Clamp01(downtown));
            return Mathf.Pow(Mathf.Clamp01(value), exponent);
        }

        private static BuildingTier TierFor(float value, System.Random rng)
        {
            float v = value + (float)(rng.NextDouble() - 0.5) * 0.16f;
            if (v >= 0.72f) return BuildingTier.Tower;
            if (v >= 0.5f) return BuildingTier.High;
            if (v >= 0.25f) return BuildingTier.Mid;
            return BuildingTier.Low;
        }

        // ---------------------------------------------------------------
        // Frontage
        // ---------------------------------------------------------------

        /// <summary>
        /// The block's edges that face a road along at least half their length, best street first (then
        /// the longer edge, then south/east/north/west), so the most important street gets the corners.
        /// </summary>
        private static List<Frontage> Frontages(RectInt block, CityBspLayout.Layout layout, Dictionary<Vector2Int, int> cutDepth)
        {
            var result = new List<Frontage>();
            for (int side = 0; side < 4; side++)
            {
                int length = side == 0 || side == 2 ? block.width : block.height;
                int roads = 0;
                int rank = int.MaxValue;
                for (int pos = 0; pos < length; pos++)
                {
                    Vector2Int outside = EdgeCell(block, side, pos) + Outward(side);
                    if (!layout.RoadCells.Contains(outside))
                    {
                        continue;
                    }
                    roads++;
                    rank = Mathf.Min(rank, cutDepth.TryGetValue(outside, out int depth) ? depth : RingRank);
                }
                if (roads * 2 >= length)
                {
                    result.Add(new Frontage(side, rank, length));
                }
            }
            result.Sort((p, q) =>
            {
                if (p.Rank != q.Rank) return p.Rank.CompareTo(q.Rank);
                if (p.Length != q.Length) return q.Length.CompareTo(p.Length);
                return p.Side.CompareTo(q.Side);
            });
            return result;
        }

        private static Vector2Int Outward(int side)
        {
            switch (side)
            {
                case 0: return Vector2Int.down;
                case 1: return Vector2Int.right;
                case 2: return Vector2Int.up;
                default: return Vector2Int.left;
            }
        }

        /// <summary>The block cell at <paramref name="pos"/> along an edge: west to east on south/north edges, south to north on east/west ones.</summary>
        private static Vector2Int EdgeCell(RectInt block, int side, int pos)
        {
            switch (side)
            {
                case 0: return new Vector2Int(block.xMin + pos, block.yMin);
                case 1: return new Vector2Int(block.xMax - 1, block.yMin + pos);
                case 2: return new Vector2Int(block.xMin + pos, block.yMax - 1);
                default: return new Vector2Int(block.xMin, block.yMin + pos);
            }
        }

        private static IEnumerable<Vector2Int> BorderCells(RectInt block)
        {
            for (int x = block.xMin; x < block.xMax; x++)
            {
                yield return new Vector2Int(x, block.yMin - 1);
                yield return new Vector2Int(x, block.yMax);
            }
            for (int y = block.yMin; y < block.yMax; y++)
            {
                yield return new Vector2Int(block.xMin - 1, y);
                yield return new Vector2Int(block.xMax, y);
            }
        }

        // ---------------------------------------------------------------
        // Packing a frontage
        // ---------------------------------------------------------------

        /// <summary>The catalog's street-front buildings grouped by footprint, largest area first.</summary>
        private static List<Shape> Shapes(IReadOnlyList<Entry> catalog)
        {
            var byFootprint = new Dictionary<Vector2Int, Shape>();
            var shapes = new List<Shape>();
            if (catalog == null)
            {
                return shapes;
            }
            foreach (Entry entry in catalog)
            {
                if (entry.Tier == BuildingTier.Filler || entry.Weight <= 0f)
                {
                    continue;
                }
                if (!byFootprint.TryGetValue(entry.Footprint, out Shape shape))
                {
                    shape = new Shape(entry.Footprint);
                    byFootprint.Add(entry.Footprint, shape);
                    shapes.Add(shape);
                }
                shape.Entries.Add(entry);
            }
            shapes.Sort((p, q) =>
            {
                int area = (q.Footprint.x * q.Footprint.y).CompareTo(p.Footprint.x * p.Footprint.y);
                if (area != 0) return area;
                if (p.Footprint.x != q.Footprint.x) return q.Footprint.x.CompareTo(p.Footprint.x);
                return p.Footprint.y.CompareTo(q.Footprint.y);
            });
            return shapes;
        }

        /// <summary>
        /// How well a building of <paramref name="building"/> tier suits a lot of <paramref name="lot"/>
        /// tier: 1 for the same, less a tier lower or higher, 0 for two or more tiers taller.
        /// </summary>
        private static float TierFactor(BuildingTier building, BuildingTier lot)
        {
            int taller = (int)building - (int)lot;
            if (taller == 0) return 1f;
            if (taller == -1) return 0.6f;
            if (taller == 1) return 0.4f;
            return taller < 0 ? 0.25f : 0f;
        }

        /// <summary>
        /// Packs one street edge of a block. Each cell along the edge rolls its tier and, on ordinary
        /// streets, whether it stays an open lot. Then a dynamic program over the edge picks the row of
        /// footprints (each no deeper than <paramref name="maxDepth"/> and clear of anything already
        /// built) that covers the most block cells, each cell weighted by how well the footprint's best
        /// building suits the lot's tier, with a little jitter so equal packings vary with the seed.
        /// Finally each chosen footprint gets a building by weight, avoiding a repeat of its neighbour.
        /// </summary>
        private static void PackFrontage(List<Shape> shapes, RectInt block, Frontage frontage, int maxDepth,
            HashSet<Vector2Int> occupied, int blockIndex, float value, Settings settings, System.Random rng,
            List<Placement> placements, ref int openLots, ref int unfilled)
        {
            int length = frontage.Length;
            var tiers = new BuildingTier[length];
            var blocked = new bool[length];
            var open = new bool[length];
            for (int pos = 0; pos < length; pos++)
            {
                tiers[pos] = TierFor(value, rng);
                blocked[pos] = occupied.Contains(EdgeCell(block, frontage.Side, pos));
                // Downtown is built solid; elsewhere Density decides, and the gaps are open lots.
                open[pos] = !blocked[pos] && tiers[pos] <= BuildingTier.Mid &&
                            rng.NextDouble() > Mathf.Clamp01(settings.Density);
            }

            // best[pos]: the highest score packing cells pos..length-1; choice[pos]: the shape put at pos, -1 for none.
            var best = new float[length + 1];
            var choice = new int[length];
            for (int pos = length - 1; pos >= 0; pos--)
            {
                best[pos] = best[pos + 1];
                choice[pos] = -1;
                if (blocked[pos] || open[pos])
                {
                    continue;
                }
                for (int s = 0; s < shapes.Count; s++)
                {
                    Vector2Int footprint = shapes[s].Footprint;
                    if (footprint.x > length - pos || footprint.y > maxDepth || SpansOpenLot(open, pos, footprint.x))
                    {
                        continue;
                    }
                    float factor = 0f;
                    foreach (Entry entry in shapes[s].Entries)
                    {
                        factor = Mathf.Max(factor, TierFactor(entry.Tier, tiers[pos]));
                    }
                    if (factor <= 0f || !Fits(Place(-1, footprint, BuildingTier.Low, block, frontage.Side, pos, -1), block, occupied))
                    {
                        continue;
                    }
                    float jitter = 0.9f + 0.2f * (float)rng.NextDouble();
                    float score = footprint.x * footprint.y * factor * jitter + best[pos + footprint.x];
                    if (score > best[pos])
                    {
                        best[pos] = score;
                        choice[pos] = s;
                    }
                }
            }

            int lastId = -1;
            for (int pos = 0; pos < length;)
            {
                if (choice[pos] < 0)
                {
                    if (open[pos])
                    {
                        openLots++;
                    }
                    else if (!blocked[pos])
                    {
                        unfilled++;
                    }
                    lastId = -1;
                    pos++;
                    continue;
                }

                Shape shape = shapes[choice[pos]];
                Entry entry = PickEntry(shape, tiers[pos], lastId, rng);
                Placement placement = Place(entry.Id, entry.Footprint, entry.Tier, block, frontage.Side, pos, blockIndex);
                placements.Add(placement);
                foreach (Vector2Int cell in placement.Cells)
                {
                    occupied.Add(cell);
                }
                lastId = entry.Id;
                pos += entry.Footprint.x;
            }
        }

        private static bool SpansOpenLot(bool[] open, int pos, int width)
        {
            for (int i = pos; i < pos + width; i++)
            {
                if (open[i])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Weighted pick among the buildings of one footprint: by the tile's weight and how well its tier
        /// suits the lot, and seldom the building just placed along this street.
        /// </summary>
        private static Entry PickEntry(Shape shape, BuildingTier lot, int lastId, System.Random rng)
        {
            float total = 0f;
            var weights = new float[shape.Entries.Count];
            for (int i = 0; i < weights.Length; i++)
            {
                Entry entry = shape.Entries[i];
                float weight = entry.Weight * TierFactor(entry.Tier, lot);
                if (entry.Id == lastId)
                {
                    weight *= 0.1f;
                }
                weights[i] = weight;
                total += weight;
            }
            double roll = rng.NextDouble() * total;
            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i] <= 0f)
                {
                    continue;
                }
                roll -= weights[i];
                if (roll <= 0)
                {
                    return shape.Entries[i];
                }
            }
            for (int i = weights.Length - 1; i >= 0; i--)
            {
                if (weights[i] > 0f)
                {
                    return shape.Entries[i];
                }
            }
            return shape.Entries[0];
        }

        private static bool Fits(Placement placement, RectInt block, HashSet<Vector2Int> occupied)
        {
            RectInt rect = placement.Rect;
            if (rect.xMin < block.xMin || rect.yMin < block.yMin || rect.xMax > block.xMax || rect.yMax > block.yMax)
            {
                return false;
            }
            foreach (Vector2Int cell in placement.Cells)
            {
                if (occupied.Contains(cell))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>A building of <paramref name="footprint"/> at <paramref name="pos"/> along an edge, flush with it and facing out of the block.</summary>
        private static Placement Place(int id, Vector2Int footprint, BuildingTier tier, RectInt block, int side, int pos, int blockIndex)
        {
            int w = footprint.x;
            int d = footprint.y;
            switch (side)
            {
                case 0:
                    return new Placement(id, new Vector2Int(block.xMin + pos, block.yMin), new Vector2Int(w, d), 0, tier, blockIndex);
                case 1:
                    return new Placement(id, new Vector2Int(block.xMax - d, block.yMin + pos), new Vector2Int(d, w), 1, tier, blockIndex);
                case 2:
                    return new Placement(id, new Vector2Int(block.xMin + pos, block.yMax - d), new Vector2Int(w, d), 2, tier, blockIndex);
                default:
                    return new Placement(id, new Vector2Int(block.xMin, block.yMin + pos), new Vector2Int(d, w), 3, tier, blockIndex);
            }
        }
    }
}
