using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Plans the elevated expressway network of a BSP city by following the city's own road
    /// hierarchy. Every BSP cut splits a region, so its depth in the tree ranks it: the depth-0 cut
    /// crosses the whole city, the depth-1 cuts cross half of it, and so on. The expressways are
    /// simply the top <see cref="Settings.Levels"/> tiers of those avenues, decked over from end to
    /// end - like the elevated expressways built above the main avenues of Bangkok or Tokyo.
    ///
    /// That one rule gives the network its shape for free:
    /// 1. **Straight and readable.** A corridor is one cut, so it never turns, and it never leaves the
    ///    street it follows - no block land is taken and there is nothing to route.
    /// 2. **Connected like the city is.** A cut always ends on a road bounding its region, which is an
    ///    ancestor's cut (or the ring). When that ancestor is an expressway too, the two decks meet in
    ///    an elevated T or crossroad: the interchange.
    /// 3. **Access where traffic is.** A corridor end that doesn't meet another expressway comes down
    ///    onto its own avenue as a flyover ramp, and along the way the deck branches onto cross
    ///    streets every <see cref="Settings.RampSpacing"/> cells, so every part of the city is served.
    ///
    /// Ramps are straight kit pieces lying over a street, so their cells get a cover tile on the
    /// street layer (the street stays connected underneath). How well the blocks are served is
    /// measured per block as the street distance to the nearest ramp.
    ///
    /// Fully deterministic, no randomness, city-local cells of <see cref="CityBspLayout"/>. See
    /// Docs/CityHighwayGenerator.md.
    /// </summary>
    internal static class HighwayPlanner
    {
        /// <summary>The kit's ramp pieces cover one cell (road-slant-high) or two (road-slant-curve). Nothing longer exists.</summary>
        public const int MinRampLength = 1;
        public const int MaxRampLength = 2;

        /// <summary>Levels beyond this add nothing a city of at most 512 cells can use.</summary>
        public const int MaxLevels = 6;

        /// <summary>A corridor whose deck would be shorter than this once its end ramps are placed is dropped.</summary>
        private const int MinDeckLength = 3;

        /// <summary>Node index a corridor made from the perimeter ring gets, since the ring is not a BSP cut.</summary>
        public const int RingNodeIndex = -1;

        private static readonly Vector2Int[] Steps =
        {
            new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(0, -1), new Vector2Int(-1, 0),
        };

        public readonly struct Settings : IEquatable<Settings>
        {
            /// <summary>How many top tiers of BSP cuts become expressways. 0 = none.</summary>
            public readonly int Levels;
            /// <summary>Cuts shorter than this many cells are left as streets.</summary>
            public readonly int MinCorridorLength;
            /// <summary>A corridor closer than this to a parallel, overlapping one is dropped (the earlier, higher-level one wins).</summary>
            public readonly int MinCorridorSpacing;
            /// <summary>Access ramps are placed at least this many cells apart along a corridor.</summary>
            public readonly int RampSpacing;
            /// <summary>Cells each ramp covers, 1 or 2.</summary>
            public readonly int RampLength;
            /// <summary>Deck over the perimeter ring road too (only when the city has one).</summary>
            public readonly bool RingExpressway;

            public Settings(int levels, int minCorridorLength, int minCorridorSpacing, int rampSpacing, int rampLength,
                bool ringExpressway)
            {
                Levels = levels;
                MinCorridorLength = minCorridorLength;
                MinCorridorSpacing = minCorridorSpacing;
                RampSpacing = rampSpacing;
                RampLength = rampLength;
                RingExpressway = ringExpressway;
            }

            public bool Equals(Settings other)
            {
                return Levels == other.Levels && MinCorridorLength == other.MinCorridorLength &&
                       MinCorridorSpacing == other.MinCorridorSpacing && RampSpacing == other.RampSpacing &&
                       RampLength == other.RampLength && RingExpressway == other.RingExpressway;
            }

            public override bool Equals(object obj)
            {
                return obj is Settings other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(Levels, MinCorridorLength, MinCorridorSpacing, RampSpacing, RampLength, RingExpressway);
            }
        }

        public enum RampKind
        {
            /// <summary>Where a corridor that meets no other expressway comes down onto its own avenue.</summary>
            End,
            /// <summary>A branch off the deck, coming down onto a cross street.</summary>
            Access,
        }

        /// <summary>One on/off ramp: <see cref="Length"/> cells over a street, climbing from ground to deck height.</summary>
        public readonly struct Ramp
        {
            /// <summary>The lowest cell. Traffic leaves it onto <see cref="Continuation"/>.</summary>
            public readonly Vector2Int Foot;
            /// <summary>Direction the ramp climbs in, toward the deck. The piece therefore *descends* toward -Direction.</summary>
            public readonly Vector2Int Direction;
            public readonly int Length;
            public readonly RampKind Kind;

            public Ramp(Vector2Int foot, Vector2Int direction, int length, RampKind kind)
            {
                Foot = foot;
                Direction = direction;
                Length = length;
                Kind = kind;
            }

            /// <summary>The highest cell - the one next to the deck.</summary>
            public Vector2Int Top => Foot + Direction * (Length - 1);

            /// <summary>The deck cell the ramp joins.</summary>
            public Vector2Int DeckJoin => Foot + Direction * Length;

            /// <summary>The street cell traffic reaches the ground on.</summary>
            public Vector2Int Continuation => Foot - Direction;

            public IEnumerable<Vector2Int> Cells
            {
                get
                {
                    for (int i = 0; i < Length; i++)
                    {
                        yield return Foot + Direction * i;
                    }
                }
            }
        }

        /// <summary>One expressway: a BSP cut (or the ring) and the part of it that is decked over.</summary>
        public sealed class Corridor
        {
            /// <summary>Index into <see cref="CityBspLayout.Layout.Nodes"/> of the region the cut splits; <see cref="RingNodeIndex"/> for the ring.</summary>
            public readonly int NodeIndex;
            /// <summary>BSP depth of the cut; -1 for the ring.</summary>
            public readonly int Depth;
            /// <summary>Every cell of the cut in order (west to east, south to north), or the ring in order.</summary>
            public readonly List<Vector2Int> Cells;
            /// <summary>The decked-over cells, in the same order. Empty until planned; a dropped corridor keeps it empty.</summary>
            public readonly List<Vector2Int> Deck = new List<Vector2Int>();
            public readonly bool IsRing;

            public Corridor(int nodeIndex, int depth, List<Vector2Int> cells, bool isRing)
            {
                NodeIndex = nodeIndex;
                Depth = depth;
                Cells = cells;
                IsRing = isRing;
            }

            public bool Vertical => !IsRing && Cells.Count > 1 && Cells[0].x == Cells[1].x;
        }

        public sealed class Plan
        {
            /// <summary>The expressways that were built, parents before children.</summary>
            public readonly List<Corridor> Corridors;
            public readonly List<Ramp> Ramps;
            /// <summary>Every elevated cell.</summary>
            public readonly HashSet<Vector2Int> DeckCells;
            /// <summary>Deck cells in a stable (y, x) order - what the builder walks.</summary>
            public readonly List<Vector2Int> DeckCellsSorted;
            /// <summary>Every ramp cell. They all lie over streets, which get a cover tile under them.</summary>
            public readonly HashSet<Vector2Int> RampCells;
            public readonly List<Vector2Int> RampCellsSorted;
            /// <summary>BSP node indices whose cut became an expressway.</summary>
            public readonly HashSet<int> ExpresswayNodes;
            /// <summary>Places where two expressways meet.</summary>
            public readonly int Interchanges;
            /// <summary>Cuts that qualified by level and length.</summary>
            public readonly int CandidateCorridors;
            /// <summary>Candidates dropped for being too close to a parallel expressway.</summary>
            public readonly int TooCloseCorridors;
            /// <summary>Candidates dropped because no end ramp fit (every spot was a junction, or the deck got too short).</summary>
            public readonly int UnrampableCorridors;
            /// <summary>
            /// For each block of the layout, in <see cref="CityBspLayout.Layout.Blocks"/> order: the
            /// street distance in cells from the block's edge to the nearest ramp. -1 when there are
            /// no ramps (or the block is unreachable).
            /// </summary>
            public readonly List<int> BlockDistances;
            public readonly int MaxBlockDistance;
            public readonly float MeanBlockDistance;

            public Plan(List<Corridor> corridors, List<Ramp> ramps, HashSet<Vector2Int> deckCells, HashSet<Vector2Int> rampCells,
                HashSet<int> expresswayNodes, int interchanges, int candidates, int tooClose, int unrampable,
                List<int> blockDistances)
            {
                Corridors = corridors;
                Ramps = ramps;
                DeckCells = deckCells;
                RampCells = rampCells;
                ExpresswayNodes = expresswayNodes;
                Interchanges = interchanges;
                CandidateCorridors = candidates;
                TooCloseCorridors = tooClose;
                UnrampableCorridors = unrampable;
                BlockDistances = blockDistances;
                DeckCellsSorted = Sorted(deckCells);
                RampCellsSorted = Sorted(rampCells);

                int max = -1;
                long sum = 0;
                int count = 0;
                foreach (int distance in blockDistances)
                {
                    if (distance < 0)
                    {
                        continue;
                    }
                    max = Mathf.Max(max, distance);
                    sum += distance;
                    count++;
                }
                MaxBlockDistance = max;
                MeanBlockDistance = count > 0 ? (float)sum / count : -1f;
            }

            public bool IsEmpty => Corridors.Count == 0;

            private static List<Vector2Int> Sorted(HashSet<Vector2Int> cells)
            {
                var list = new List<Vector2Int>(cells);
                list.Sort((p, q) => p.y != q.y ? p.y.CompareTo(q.y) : p.x.CompareTo(q.x));
                return list;
            }
        }

        /// <summary>Plans the whole network. Levels 0 (or a city with no cuts) gives an empty plan.</summary>
        public static Plan Build(CityBspLayout.Layout layout, Settings settings)
        {
            var built = new List<Corridor>();
            var ramps = new List<Ramp>();
            var deck = new HashSet<Vector2Int>();
            var rampCells = new HashSet<Vector2Int>();
            var expresswayNodes = new HashSet<int>();
            HashSet<Vector2Int> streets = layout.RoadCells;
            int rampLength = Mathf.Clamp(settings.RampLength, MinRampLength, MaxRampLength);

            List<Corridor> candidates = Candidates(layout, settings);
            int candidateCount = candidates.Count;
            int tooClose = 0;
            int unrampable = 0;
            int interchanges = 0;

            // Parents before children: a cut can only end on an ancestor's cut (or the ring), and
            // those come first in this order, so whether a corridor end meets another expressway is
            // already settled when the corridor is planned.
            foreach (Corridor corridor in candidates)
            {
                if (!corridor.IsRing && IsTooClose(corridor, built, settings.MinCorridorSpacing))
                {
                    tooClose++;
                    continue;
                }
                if (corridor.IsRing)
                {
                    corridor.Deck.AddRange(corridor.Cells);
                }
                else if (!PlanStraightCorridor(corridor, streets, deck, rampCells, rampLength, ramps, ref interchanges))
                {
                    unrampable++;
                    continue;
                }
                built.Add(corridor);
                deck.UnionWith(corridor.Deck);
                expresswayNodes.Add(corridor.NodeIndex);
            }

            foreach (Corridor corridor in built)
            {
                PlaceAccessRamps(corridor, streets, deck, rampCells, rampLength, Mathf.Max(1, settings.RampSpacing), ramps);
            }

            List<int> distances = BlockDistances(layout, ramps);
            return new Plan(built, ramps, deck, rampCells, expresswayNodes, interchanges, candidateCount, tooClose, unrampable,
                distances);
        }

        // ---------------------------------------------------------------
        // Corridors
        // ---------------------------------------------------------------

        /// <summary>The ring (if asked for), then every cut of the top levels long enough, by depth, longest first, then position.</summary>
        private static List<Corridor> Candidates(CityBspLayout.Layout layout, Settings settings)
        {
            var result = new List<Corridor>();
            int levels = Mathf.Clamp(settings.Levels, 0, MaxLevels);
            if (levels == 0 || layout.SideLength < 3)
            {
                return result;
            }
            if (settings.RingExpressway && layout.PerimeterCellCount > 0)
            {
                result.Add(new Corridor(RingNodeIndex, -1, RingCells(layout.SideLength), true));
            }

            var cuts = new List<Corridor>();
            for (int i = 0; i < layout.Nodes.Count; i++)
            {
                CityBspLayout.Node node = layout.Nodes[i];
                if (node.IsLeaf || node.Depth >= levels)
                {
                    continue;
                }
                RectInt cut = node.Cut;
                int length = Mathf.Max(cut.width, cut.height);
                if (length < Mathf.Max(1, settings.MinCorridorLength))
                {
                    continue;
                }
                var cells = new List<Vector2Int>(length);
                for (int k = 0; k < length; k++)
                {
                    cells.Add(node.CutVertical ? new Vector2Int(cut.x, cut.y + k) : new Vector2Int(cut.x + k, cut.y));
                }
                cuts.Add(new Corridor(i, node.Depth, cells, false));
            }
            cuts.Sort((p, q) =>
            {
                if (p.Depth != q.Depth) return p.Depth.CompareTo(q.Depth);
                if (p.Cells.Count != q.Cells.Count) return q.Cells.Count.CompareTo(p.Cells.Count);
                return p.NodeIndex.CompareTo(q.NodeIndex);
            });
            result.AddRange(cuts);
            return result;
        }

        private static List<Vector2Int> RingCells(int side)
        {
            var cells = new List<Vector2Int>();
            int last = side - 1;
            for (int x = 0; x < last; x++) cells.Add(new Vector2Int(x, 0));
            for (int y = 0; y < last; y++) cells.Add(new Vector2Int(last, y));
            for (int x = last; x > 0; x--) cells.Add(new Vector2Int(x, last));
            for (int y = last; y > 0; y--) cells.Add(new Vector2Int(0, y));
            return cells;
        }

        /// <summary>
        /// True if <paramref name="corridor"/> runs parallel to an expressway already built, closer than
        /// <paramref name="spacing"/>, over a stretch where the two overlap. The ring counts as its four sides.
        /// </summary>
        private static bool IsTooClose(Corridor corridor, List<Corridor> built, int spacing)
        {
            if (spacing <= 0)
            {
                return false;
            }
            bool vertical = corridor.Vertical;
            int position = vertical ? corridor.Cells[0].x : corridor.Cells[0].y;
            int from = vertical ? corridor.Cells[0].y : corridor.Cells[0].x;
            int to = from + corridor.Cells.Count - 1;
            foreach (Corridor other in built)
            {
                if (other.IsRing)
                {
                    int last = other.Cells.Count / 4; // side - 1
                    if (Mathf.Abs(position - 0) < spacing || Mathf.Abs(position - last) < spacing)
                    {
                        return true;
                    }
                    continue;
                }
                if (other.Vertical != vertical || other.Deck.Count == 0)
                {
                    continue;
                }
                int otherPosition = vertical ? other.Deck[0].x : other.Deck[0].y;
                int otherFrom = vertical ? other.Deck[0].y : other.Deck[0].x;
                int otherTo = otherFrom + other.Deck.Count - 1;
                if (Mathf.Abs(position - otherPosition) < spacing && from <= otherTo && otherFrom <= to)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Settles a straight corridor's two ends. An end whose next cell out is already deck is an
        /// interchange: the deck runs right to it. Any other end gets a flyover ramp coming down onto
        /// the avenue, as close to the end as it fits. Returns false if an end has nowhere for a ramp.
        /// </summary>
        private static bool PlanStraightCorridor(Corridor corridor, HashSet<Vector2Int> streets, HashSet<Vector2Int> deck,
            HashSet<Vector2Int> rampCells, int rampLength, List<Ramp> ramps, ref int interchanges)
        {
            List<Vector2Int> cells = corridor.Cells;
            int count = cells.Count;
            Vector2Int axis = count > 1 ? cells[1] - cells[0] : Vector2Int.right;

            bool startMeets = deck.Contains(cells[0] - axis);
            bool endMeets = deck.Contains(cells[count - 1] + axis);

            int first = 0;
            int last = count - 1;
            Ramp? startRamp = null;
            Ramp? endRamp = null;
            // Each end may search at most half the corridor, so the two ramps can never overlap.
            int searchLimit = Mathf.Max(0, (count - MinDeckLength) / 2 - rampLength);
            if (!startMeets)
            {
                if (!TryFindEndRamp(cells, 0, 1, axis, rampLength, searchLimit, streets, deck, rampCells, out Ramp ramp))
                {
                    return false;
                }
                startRamp = ramp;
                first = IndexOf(cells, ramp.DeckJoin);
            }
            if (!endMeets)
            {
                if (!TryFindEndRamp(cells, count - 1, -1, -axis, rampLength, searchLimit, streets, deck, rampCells, out Ramp ramp))
                {
                    return false;
                }
                endRamp = ramp;
                last = IndexOf(cells, ramp.DeckJoin);
            }
            if (last - first + 1 < MinDeckLength)
            {
                return false;
            }

            for (int i = first; i <= last; i++)
            {
                corridor.Deck.Add(cells[i]);
            }
            foreach (Ramp? ramp in new[] { startRamp, endRamp })
            {
                if (ramp.HasValue)
                {
                    ramps.Add(ramp.Value);
                    rampCells.UnionWith(ramp.Value.Cells);
                }
            }
            if (startMeets) interchanges++;
            if (endMeets) interchanges++;
            return true;
        }

        /// <summary>
        /// Walks inward from the end at <paramref name="endIndex"/> for the first spot a ramp fits:
        /// <paramref name="rampLength"/> plain street cells (no cross street) with a street beyond the
        /// foot for traffic to reach the ground on, and a deck cell above the top.
        /// </summary>
        private static bool TryFindEndRamp(List<Vector2Int> cells, int endIndex, int inward, Vector2Int climb, int rampLength,
            int searchLimit, HashSet<Vector2Int> streets, HashSet<Vector2Int> deck, HashSet<Vector2Int> rampCells, out Ramp ramp)
        {
            for (int offset = 0; offset <= searchLimit; offset++)
            {
                int footIndex = endIndex + inward * offset;
                int joinIndex = footIndex + inward * rampLength;
                if (joinIndex < 0 || joinIndex >= cells.Count)
                {
                    break;
                }
                var candidate = new Ramp(cells[footIndex], climb, rampLength, RampKind.End);
                if (RampFits(candidate, streets, deck, rampCells))
                {
                    ramp = candidate;
                    return true;
                }
            }
            ramp = default;
            return false;
        }

        // ---------------------------------------------------------------
        // Access ramps
        // ---------------------------------------------------------------

        /// <summary>
        /// Walks the corridor's deck and branches a ramp onto a cross street wherever one fits at least
        /// <paramref name="spacing"/> cells from the previous ramp. End ramps count as the first and last
        /// ramps, so a short corridor that already has both may need no access ramp at all. A junction
        /// right beside an interchange or another branch is skipped, so no two elevated junctions touch.
        /// </summary>
        private static void PlaceAccessRamps(Corridor corridor, HashSet<Vector2Int> streets, HashSet<Vector2Int> deck,
            HashSet<Vector2Int> rampCells, int rampLength, int spacing, List<Ramp> ramps)
        {
            List<Vector2Int> cells = corridor.Deck;
            int count = cells.Count;
            if (count == 0)
            {
                return;
            }

            // An end ramp joins the deck at its first or last cell.
            int lastRamp = HasRampJoining(cells[0], ramps) ? 0 : int.MinValue / 2;
            bool endHasRamp = HasRampJoining(cells[count - 1], ramps);
            int firstChosen = -1;

            for (int i = 0; i < count; i++)
            {
                if (i - lastRamp < spacing)
                {
                    continue;
                }
                if (!corridor.IsRing && endHasRamp && (count - 1) - i < spacing)
                {
                    break;
                }
                // Around the ring the gap back to the first ramp counts too.
                if (corridor.IsRing && firstChosen >= 0 && firstChosen + count - i < spacing)
                {
                    break;
                }
                Vector2Int cell = cells[i];
                if (!corridor.IsRing && (i == 0 || i == count - 1))
                {
                    continue; // corridor ends are where interchanges and end ramps join
                }
                Vector2Int along = corridor.IsRing ? cells[(i + 1) % count] - cell : cells[Mathf.Min(i + 1, count - 1)] - cells[Mathf.Max(i - 1, 0)];
                if (corridor.IsRing && cells[(i + count - 1) % count] - cell != -along)
                {
                    continue; // a ring corner
                }
                along = new Vector2Int(Math.Sign(along.x), Math.Sign(along.y));
                if (BranchesNearby(cell, along, deck, rampCells))
                {
                    continue;
                }
                var lateral = new Vector2Int(along.y, along.x);
                foreach (Vector2Int side in new[] { lateral, -lateral })
                {
                    Vector2Int down = cell + side;
                    if (!streets.Contains(down))
                    {
                        continue;
                    }
                    var candidate = new Ramp(cell + side * rampLength, -side, rampLength, RampKind.Access);
                    if (!RampFits(candidate, streets, deck, rampCells))
                    {
                        continue;
                    }
                    ramps.Add(candidate);
                    rampCells.UnionWith(candidate.Cells);
                    lastRamp = i;
                    if (firstChosen < 0)
                    {
                        firstChosen = i;
                    }
                    break;
                }
            }
        }

        private static bool HasRampJoining(Vector2Int cell, List<Ramp> ramps)
        {
            foreach (Ramp ramp in ramps)
            {
                if (ramp.DeckJoin == cell)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// True if this deck cell, or the deck cell either side of it along the corridor, already has an
        /// elevated neighbour off the corridor (another expressway or a ramp), so a branch here would
        /// touch or double up on that junction.
        /// </summary>
        private static bool BranchesNearby(Vector2Int cell, Vector2Int along, HashSet<Vector2Int> deck, HashSet<Vector2Int> rampCells)
        {
            var lateral = new Vector2Int(along.y, along.x);
            for (int k = -1; k <= 1; k++)
            {
                Vector2Int c = cell + along * k;
                foreach (Vector2Int n in new[] { c + lateral, c - lateral })
                {
                    if (deck.Contains(n) || rampCells.Contains(n))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // ---------------------------------------------------------------
        // Ramp rules
        // ---------------------------------------------------------------

        /// <summary>
        /// Whether a ramp can stand here. Every ramp cell must be a plain street cell running the
        /// ramp's way - no cross street, since the ramp would drop onto it - and not already elevated,
        /// with nothing elevated beside it (on the highway's own Tilemap a ramp foot is recognised by
        /// having exactly one highway neighbour). The cell the foot lands on must be a street that isn't
        /// elevated either, and the cell above the top must be deck or about to be.
        /// </summary>
        private static bool RampFits(Ramp ramp, HashSet<Vector2Int> streets, HashSet<Vector2Int> deck, HashSet<Vector2Int> rampCells)
        {
            Vector2Int lateral = new Vector2Int(ramp.Direction.y, ramp.Direction.x);
            foreach (Vector2Int cell in ramp.Cells)
            {
                if (!streets.Contains(cell) || deck.Contains(cell) || rampCells.Contains(cell) ||
                    streets.Contains(cell + lateral) || streets.Contains(cell - lateral) ||
                    !streets.Contains(cell + ramp.Direction) || !streets.Contains(cell - ramp.Direction))
                {
                    return false;
                }
                foreach (Vector2Int n in new[] { cell + lateral, cell - lateral })
                {
                    if (deck.Contains(n) || rampCells.Contains(n))
                    {
                        return false;
                    }
                }
            }
            Vector2Int landing = ramp.Continuation;
            if (!streets.Contains(landing) || deck.Contains(landing) || rampCells.Contains(landing))
            {
                return false;
            }
            // Nothing elevated may touch the landing from the sides either, or the foot would read as deck.
            foreach (Vector2Int step in Steps)
            {
                Vector2Int n = landing + step;
                if (n != ramp.Foot && rampCells.Contains(n))
                {
                    return false;
                }
            }
            return true;
        }

        private static int IndexOf(List<Vector2Int> cells, Vector2Int cell)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i] == cell)
                {
                    return i;
                }
            }
            return -1;
        }

        // ---------------------------------------------------------------
        // Coverage
        // ---------------------------------------------------------------

        /// <summary>
        /// Street distance from each block to the nearest ramp: a breadth-first search over the street
        /// grid from every ramp's landing cell, read at the streets bordering each block.
        /// </summary>
        private static List<int> BlockDistances(CityBspLayout.Layout layout, List<Ramp> ramps)
        {
            var result = new List<int>(layout.Blocks.Count);
            if (ramps.Count == 0)
            {
                foreach (RectInt _ in layout.Blocks)
                {
                    result.Add(-1);
                }
                return result;
            }

            int side = layout.SideLength;
            var distance = new int[side * side];
            for (int i = 0; i < distance.Length; i++)
            {
                distance[i] = -1;
            }
            var queue = new Queue<Vector2Int>();
            foreach (Ramp ramp in ramps)
            {
                foreach (Vector2Int source in new[] { ramp.Continuation, ramp.Foot })
                {
                    int index = source.y * side + source.x;
                    if (InCity(source, side) && distance[index] < 0)
                    {
                        distance[index] = 0;
                        queue.Enqueue(source);
                    }
                }
            }
            while (queue.Count > 0)
            {
                Vector2Int cell = queue.Dequeue();
                int d = distance[cell.y * side + cell.x];
                foreach (Vector2Int step in Steps)
                {
                    Vector2Int next = cell + step;
                    if (!InCity(next, side) || !layout.RoadCells.Contains(next))
                    {
                        continue;
                    }
                    int index = next.y * side + next.x;
                    if (distance[index] < 0)
                    {
                        distance[index] = d + 1;
                        queue.Enqueue(next);
                    }
                }
            }

            foreach (RectInt block in layout.Blocks)
            {
                int best = -1;
                void Consider(int x, int y)
                {
                    var cell = new Vector2Int(x, y);
                    if (!InCity(cell, side))
                    {
                        return;
                    }
                    int d = distance[y * side + x];
                    if (d >= 0 && (best < 0 || d < best))
                    {
                        best = d;
                    }
                }
                for (int x = block.xMin; x < block.xMax; x++)
                {
                    Consider(x, block.yMin - 1);
                    Consider(x, block.yMax);
                }
                for (int y = block.yMin; y < block.yMax; y++)
                {
                    Consider(block.xMin - 1, y);
                    Consider(block.xMax, y);
                }
                result.Add(best);
            }
            return result;
        }

        private static bool InCity(Vector2Int cell, int side)
        {
            return cell.x >= 0 && cell.y >= 0 && cell.x < side && cell.y < side;
        }
    }
}
