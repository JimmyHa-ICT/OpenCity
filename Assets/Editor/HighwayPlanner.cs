using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Plans the elevated highway network of a BSP city: groups the city into clusters by reading
    /// the BSP tree back at a chosen depth (<see cref="CityBspLayout.Districts"/>), gives each
    /// cluster one on/off ramp, picks which *far apart* clusters deserve a direct link, and routes
    /// each link as an orthogonal run of deck cells. The result is a set of deck and ramp cells -
    /// the builder paints those onto the highway's own Tilemap, this class never touches Unity
    /// objects.
    ///
    /// The ideas that make the result look like a real elevated expressway:
    /// 1. **Right on top of a street, or well clear of one.** A deck cell is either over a street
    ///    (following it, or flying straight across it) or has no street running alongside it. A
    ///    viaduct one cell off a road, parallel to it, is the thing that never happens.
    /// 2. **Turns happen over the road they follow, or far from any road.** Over a street the deck
    ///    only turns onto, off or along it; off the street grid only where no street touches the cell.
    /// 3. **Ramps stand clear of the street grid** except for the single cell where they touch
    ///    down beside their hub, which becomes the interchange.
    ///
    /// Fully deterministic: no randomness at all, and every loop runs over a sorted list, so the
    /// same layout and settings always give the same highways. Coordinates are the city-local
    /// cells of <see cref="CityBspLayout"/>. See Docs/CityHighwayGenerator.md.
    /// </summary>
    internal static class HighwayPlanner
    {
        /// <summary>The kit's ramp pieces cover one cell (road-slant-high) or two (road-slant-curve). Nothing longer exists.</summary>
        public const int MinRampLength = 1;
        public const int MaxRampLength = 2;

        // North, East, South, West - the same clockwise order RoadGridPlacer rotates through.
        private static readonly Vector2Int[] Steps =
        {
            new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(0, -1), new Vector2Int(-1, 0),
        };

        /// <summary>A step onto a fresh cell. Costs are doubled so a reused cell can be worth half a step without fractions.</summary>
        private const int StepCost = 2;

        /// <summary>A step onto a cell an earlier link already decked. Cheaper, so links merge into shared trunks instead of running side by side.</summary>
        private const int ReuseStepCost = 1;

        public readonly struct Settings : IEquatable<Settings>
        {
            /// <summary>Depth of the BSP tree the clusters are read off at: up to 2^depth of them.</summary>
            public readonly int ClusterDepth;
            /// <summary>Two clusters count as far apart, and so worth a highway, at this Manhattan cell distance between their hubs.</summary>
            public readonly int MinSpanCells;
            public readonly int MaxLinks;
            /// <summary>Cells each ramp climbs over, 1 or 2.</summary>
            public readonly int RampLength;
            /// <summary>What one change of direction costs the router, in cells. High = long straight runs.</summary>
            public readonly int TurnCost;
            /// <summary>A step over a street costs half a step, so routes ride on top of the street grid where they can.</summary>
            public readonly bool FollowStreets;

            public Settings(int clusterDepth, int minSpanCells, int maxLinks, int rampLength, int turnCost, bool followStreets)
            {
                ClusterDepth = clusterDepth;
                MinSpanCells = minSpanCells;
                MaxLinks = maxLinks;
                RampLength = rampLength;
                TurnCost = turnCost;
                FollowStreets = followStreets;
            }

            public bool Equals(Settings other)
            {
                return ClusterDepth == other.ClusterDepth && MinSpanCells == other.MinSpanCells &&
                       MaxLinks == other.MaxLinks && RampLength == other.RampLength && TurnCost == other.TurnCost &&
                       FollowStreets == other.FollowStreets;
            }

            public override bool Equals(object obj)
            {
                return obj is Settings other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(ClusterDepth, MinSpanCells, MaxLinks, RampLength, TurnCost, FollowStreets);
            }
        }

        /// <summary>
        /// One cluster's connection to the highway: a street cell, and the direction in which a
        /// ramp can climb away from it over free block cells.
        /// </summary>
        public readonly struct Hub
        {
            /// <summary>The cluster this hub serves.</summary>
            public readonly RectInt District;
            /// <summary>The street cell the ramp touches down beside. Stays an ordinary road cell; it just gains an arm.</summary>
            public readonly Vector2Int Cell;
            /// <summary>Direction the ramp climbs in, away from <see cref="Cell"/>.</summary>
            public readonly Vector2Int Direction;

            public Hub(RectInt district, Vector2Int cell, Vector2Int direction)
            {
                District = district;
                Cell = cell;
                Direction = direction;
            }
        }

        /// <summary>One on/off ramp: <see cref="Length"/> cells climbing from ground beside a hub up to deck height.</summary>
        public readonly struct Ramp
        {
            /// <summary>The lowest cell, orthogonally adjacent to the hub street cell.</summary>
            public readonly Vector2Int Foot;
            /// <summary>Direction the ramp climbs in. The piece therefore *descends* toward -Direction.</summary>
            public readonly Vector2Int Direction;
            public readonly int Length;

            public Ramp(Vector2Int foot, Vector2Int direction, int length)
            {
                Foot = foot;
                Direction = direction;
                Length = length;
            }

            /// <summary>The highest cell - the one the deck continues from.</summary>
            public Vector2Int Top => Foot + Direction * (Length - 1);

            /// <summary>The first deck cell beyond the ramp.</summary>
            public Vector2Int DeckStart => Foot + Direction * Length;

            /// <summary>The hub street cell this ramp lands beside.</summary>
            public Vector2Int Hub => Foot - Direction;

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

        public sealed class Link
        {
            public readonly Ramp RampA;
            public readonly Ramp RampB;
            /// <summary>Deck cells in travel order, from RampA.DeckStart to RampB.DeckStart inclusive. Never empty.</summary>
            public readonly List<Vector2Int> Deck;
            /// <summary>Manhattan distance between the two hubs - what "far apart" was measured on.</summary>
            public readonly int Span;

            public Link(Ramp rampA, Ramp rampB, List<Vector2Int> deck, int span)
            {
                RampA = rampA;
                RampB = rampB;
                Deck = deck;
                Span = span;
            }
        }

        public sealed class Plan
        {
            /// <summary>The clusters the city was cut into, whether or not they ended up on the highway.</summary>
            public readonly List<RectInt> Districts;
            public readonly List<Hub> Hubs;
            public readonly List<Link> Links;
            public readonly List<Ramp> Ramps;
            /// <summary>Every elevated cell, once, however many links run over it.</summary>
            public readonly HashSet<Vector2Int> DeckCells;
            /// <summary>Deck cells in a stable (y, x) order - what the builder walks.</summary>
            public readonly List<Vector2Int> DeckCellsSorted;
            public readonly HashSet<Vector2Int> RampCells;
            /// <summary>Ramp cells in a stable order.</summary>
            public readonly List<Vector2Int> RampCellsSorted;
            /// <summary>
            /// The bottom cell of each ramp, in a stable order - the only highway cells that get a
            /// cover tile. Painting the rest of a ramp too would hand a road arm to whatever street
            /// happens to lie beyond its top, which the highway passes over rather than joins.
            /// </summary>
            public readonly List<Vector2Int> FootCells;
            /// <summary>Clusters with nowhere to put a ramp (no street with enough clear block cells beside it).</summary>
            public readonly int DistrictsWithoutHub;
            /// <summary>Links the router could not find a legal corridor for.</summary>
            public readonly int UnroutableLinks;
            /// <summary>Deck cells right on top of a street.</summary>
            public readonly int BridgeCells;

            public Plan(List<RectInt> districts, List<Hub> hubs, List<Link> links, List<Ramp> ramps,
                HashSet<Vector2Int> deckCells, HashSet<Vector2Int> rampCells,
                int districtsWithoutHub, int unroutableLinks, int bridgeCells)
            {
                Districts = districts;
                Hubs = hubs;
                Links = links;
                Ramps = ramps;
                DeckCells = deckCells;
                RampCells = rampCells;
                DistrictsWithoutHub = districtsWithoutHub;
                UnroutableLinks = unroutableLinks;
                BridgeCells = bridgeCells;
                DeckCellsSorted = Sorted(deckCells);
                RampCellsSorted = Sorted(rampCells);
                var feet = new HashSet<Vector2Int>();
                foreach (Ramp ramp in ramps)
                {
                    feet.Add(ramp.Foot);
                }
                FootCells = Sorted(feet);
            }

            public bool IsEmpty => Links.Count == 0;

            private static List<Vector2Int> Sorted(HashSet<Vector2Int> cells)
            {
                var list = new List<Vector2Int>(cells);
                list.Sort((p, q) => p.y != q.y ? p.y.CompareTo(q.y) : p.x.CompareTo(q.x));
                return list;
            }
        }

        /// <summary>
        /// Plans the whole network. <paramref name="reservedCells"/> are cells the highway must keep
        /// off entirely - the caller passes the roundabout footprints. A street can carry the deck
        /// because its supports land on the kerbs; a roundabout is three cells wide and a support
        /// would come down inside it, so the deck goes round instead.
        /// </summary>
        public static Plan Build(CityBspLayout.Layout layout, HashSet<Vector2Int> reservedCells, Settings settings)
        {
            var districts = CityBspLayout.Districts(layout, settings.ClusterDepth);
            var hubs = new List<Hub>();
            var links = new List<Link>();
            var ramps = new List<Ramp>();
            var deckCells = new HashSet<Vector2Int>();
            var rampCells = new HashSet<Vector2Int>();
            var rampFeet = new HashSet<Vector2Int>();

            int rampLength = Mathf.Clamp(settings.RampLength, MinRampLength, MaxRampLength);
            var blocked = reservedCells ?? new HashSet<Vector2Int>();
            // The deck may run on top of streets or well clear of them; reserved cells are out of
            // bounds altogether. Aliased, not copied - nothing below writes to it, and the layout it
            // belongs to is cached and shared with the preview.
            HashSet<Vector2Int> streets = layout.RoadCells;

            // Preorder is already deterministic, but sorting by position makes the hub each cluster
            // ends up with independent of how the tree happened to be walked.
            districts.Sort((p, q) => p.y != q.y ? p.y.CompareTo(q.y) : p.x.CompareTo(q.x));

            if (settings.MaxLinks <= 0)
            {
                // Nothing to build, and finding hubs is the expensive part - skip it entirely so
                // turning highways off costs a big city nothing.
                return new Plan(districts, hubs, links, ramps, deckCells, rampCells, 0, 0, 0);
            }

            // Two ramps must never touch: on the highway's own Tilemap a ramp foot is recognised by
            // having exactly one highway neighbour, so a second ramp beside it would turn both into
            // deck pieces. rampZone is every accepted ramp's cells and their neighbours (which
            // includes its hub); a new ramp's hub, cells and deck start must all stay out of it.
            var rampZone = new HashSet<Vector2Int>();
            int withoutHub = 0;
            foreach (RectInt district in districts)
            {
                if (TryFindHub(district, layout, blocked, rampZone, rampLength, out Hub hub))
                {
                    hubs.Add(hub);
                    foreach (Vector2Int cell in new Ramp(hub.Cell + hub.Direction, hub.Direction, rampLength).Cells)
                    {
                        rampZone.Add(cell);
                        for (int d = 0; d < Steps.Length; d++)
                        {
                            rampZone.Add(cell + Steps[d]);
                        }
                    }
                }
                else
                {
                    withoutHub++;
                }
            }

            var router = new Router(layout.SideLength, streets, blocked, Mathf.Max(0, settings.TurnCost),
                settings.FollowStreets);
            // Every hub's ramp corridor is reserved before anything is routed, not as each link is
            // committed. A ramp's cells were fixed when its hub was chosen, so a deck laid down
            // first could otherwise sit exactly where a later link's ramp has to climb - with no
            // way to reroute the ramp afterwards.
            foreach (Hub hub in hubs)
            {
                router.Reserve(new Ramp(hub.Cell + hub.Direction, hub.Direction, rampLength));
            }

            int unroutable = 0;
            foreach ((int a, int b, int span) in SelectLinks(hubs, settings))
            {
                Ramp rampA = new Ramp(hubs[a].Cell + hubs[a].Direction, hubs[a].Direction, rampLength);
                Ramp rampB = new Ramp(hubs[b].Cell + hubs[b].Direction, hubs[b].Direction, rampLength);
                List<Vector2Int> deck = router.Route(rampA, rampB);
                if (deck == null)
                {
                    unroutable++;
                    continue;
                }
                links.Add(new Link(rampA, rampB, deck, span));
                // The links form a spanning tree, so a cluster served by more than one of them
                // shares the one ramp: its foot identifies it, since a hub has a single direction.
                if (rampFeet.Add(rampA.Foot))
                {
                    ramps.Add(rampA);
                }
                if (rampFeet.Add(rampB.Foot))
                {
                    ramps.Add(rampB);
                }
                rampCells.UnionWith(rampA.Cells);
                rampCells.UnionWith(rampB.Cells);
                deckCells.UnionWith(deck);
                router.Commit(rampA, rampB, deck);
            }

            int bridgeCells = 0;
            foreach (Vector2Int cell in deckCells)
            {
                if (streets.Contains(cell))
                {
                    bridgeCells++;
                }
            }

            return new Plan(districts, hubs, links, ramps, deckCells, rampCells,
                withoutHub, unroutable, bridgeCells);
        }

        // ---------------------------------------------------------------
        // Hubs
        // ---------------------------------------------------------------

        /// <summary>
        /// The street cell of <paramref name="district"/> nearest its center that a ramp can climb
        /// away from. A candidate needs, in the direction the ramp would climb,
        /// <paramref name="rampLength"/> free block cells in a row plus one more cell inside the
        /// city for the deck to start on. The ramp's cells must have no road neighbour of their own
        /// (other than the hub itself) so the cover tile under the ramp can only ever create the
        /// one intended junction, and the hub must not already sit beside a junction, so painting
        /// that junction can't break the city's "no two intersections side by side" guarantee.
        /// Neither the hub, the ramp nor the deck start may touch a ramp another cluster already has.
        ///
        /// The district rect covers the ground *between* the roads bounding it, so it is grown by
        /// one cell here: that brings in those bounding roads, and it is the only thing that gives
        /// a cluster which is a single uncut block any street to start from at all.
        /// </summary>
        private static bool TryFindHub(RectInt district, CityBspLayout.Layout layout, HashSet<Vector2Int> reserved,
            HashSet<Vector2Int> rampZone, int rampLength, out Hub hub)
        {
            hub = default;
            int side = layout.SideLength;
            var area = new RectInt(district.x - 1, district.y - 1, district.width + 2, district.height + 2);
            var center = new Vector2(district.x + district.width * 0.5f, district.y + district.height * 0.5f);

            float bestScore = float.MaxValue;
            bool found = false;
            foreach (Vector2Int cell in layout.RoadCellsSorted)
            {
                if (!area.Contains(cell) || rampZone.Contains(cell) || reserved.Contains(cell) ||
                    IsBesideJunction(cell, layout.RoadCells))
                {
                    continue;
                }
                float score = (new Vector2(cell.x + 0.5f, cell.y + 0.5f) - center).sqrMagnitude;
                if (score >= bestScore)
                {
                    continue; // strict, so the first cell of the sorted list wins a tie
                }
                for (int d = 0; d < Steps.Length; d++)
                {
                    if (!RampFits(cell, Steps[d], side, reserved, layout.RoadCells, rampLength) ||
                        TouchesRampZone(cell, Steps[d], rampLength, rampZone))
                    {
                        continue;
                    }
                    hub = new Hub(district, cell, Steps[d]);
                    bestScore = score;
                    found = true;
                    break; // Steps is in a fixed order, so the tie between two directions is too
                }
            }
            return found;
        }

        private static bool TouchesRampZone(Vector2Int street, Vector2Int step, int rampLength, HashSet<Vector2Int> rampZone)
        {
            for (int i = 1; i <= rampLength + 1; i++)
            {
                if (rampZone.Contains(street + step * i))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Whether a ramp climbing from <paramref name="street"/> in <paramref name="step"/> has the clear run it needs.</summary>
        private static bool RampFits(Vector2Int street, Vector2Int step, int side,
            HashSet<Vector2Int> reserved, HashSet<Vector2Int> roads, int rampLength)
        {
            for (int i = 1; i <= rampLength; i++)
            {
                Vector2Int cell = street + step * i;
                if (!InCity(cell, side) || roads.Contains(cell) || reserved.Contains(cell))
                {
                    return false;
                }
                // Every road neighbour of a ramp cell would gain an arm once the cover tile goes
                // down. Only the hub may do that, and only from the first cell.
                for (int d = 0; d < Steps.Length; d++)
                {
                    Vector2Int neighbor = cell + Steps[d];
                    if (neighbor != street + step * (i - 1) && neighbor != street + step * (i + 1) &&
                        roads.Contains(neighbor))
                    {
                        return false;
                    }
                }
            }
            // The deck leaves the ramp dead straight, so its first cell is either a street it rides
            // straight onto or a cell with no street running alongside it.
            Vector2Int deckStart = street + step * (rampLength + 1);
            return InCity(deckStart, side) && !reserved.Contains(deckStart) &&
                   (roads.Contains(deckStart) || !HasLateralStreet(deckStart, DirectionIndexOf(step), roads));
        }

        private static int DirectionIndexOf(Vector2Int step)
        {
            for (int i = 0; i < Steps.Length; i++)
            {
                if (Steps[i] == step)
                {
                    return i;
                }
            }
            return 0;
        }

        /// <summary>True if any orthogonal neighbour of <paramref name="cell"/> is already a T-junction or crossroad.</summary>
        private static bool IsBesideJunction(Vector2Int cell, HashSet<Vector2Int> roads)
        {
            for (int d = 0; d < Steps.Length; d++)
            {
                Vector2Int neighbor = cell + Steps[d];
                if (!roads.Contains(neighbor))
                {
                    continue;
                }
                int degree = 0;
                for (int e = 0; e < Steps.Length; e++)
                {
                    if (roads.Contains(neighbor + Steps[e]))
                    {
                        degree++;
                    }
                }
                if (degree >= 3)
                {
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------
        // Which clusters get linked
        // ---------------------------------------------------------------

        /// <summary>
        /// The links worth building, shortest first. Only pairs at least
        /// <see cref="Settings.MinSpanCells"/> apart are candidates - that is the whole point of a
        /// highway, and it is what keeps neighbouring clusters off it, since they already have
        /// streets. Kruskal over those candidates then keeps a link only when it joins two parts of
        /// the network that were not connected yet, so the result is a minimum spanning forest: no
        /// redundant parallel routes, and a cluster with no far partner simply stays off the
        /// highway and is reached through its neighbour's hub.
        /// </summary>
        private static List<(int a, int b, int span)> SelectLinks(List<Hub> hubs, Settings settings)
        {
            var candidates = new List<(int a, int b, int span)>();
            for (int i = 0; i < hubs.Count; i++)
            {
                for (int j = i + 1; j < hubs.Count; j++)
                {
                    int span = Mathf.Abs(hubs[i].Cell.x - hubs[j].Cell.x) + Mathf.Abs(hubs[i].Cell.y - hubs[j].Cell.y);
                    if (span >= Mathf.Max(1, settings.MinSpanCells))
                    {
                        candidates.Add((i, j, span));
                    }
                }
            }
            candidates.Sort((p, q) =>
            {
                if (p.span != q.span) return p.span.CompareTo(q.span);
                if (p.a != q.a) return p.a.CompareTo(q.a);
                return p.b.CompareTo(q.b);
            });

            var parent = new int[hubs.Count];
            for (int i = 0; i < parent.Length; i++)
            {
                parent[i] = i;
            }

            var chosen = new List<(int a, int b, int span)>();
            int limit = Mathf.Max(0, settings.MaxLinks);
            foreach ((int a, int b, int span) in candidates)
            {
                if (chosen.Count >= limit)
                {
                    break;
                }
                int rootA = Find(parent, a);
                int rootB = Find(parent, b);
                if (rootA == rootB)
                {
                    continue;
                }
                parent[rootA] = rootB;
                chosen.Add((a, b, span));
            }
            return chosen;
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        private static bool InCity(Vector2Int cell, int side)
        {
            return cell.x >= 0 && cell.y >= 0 && cell.x < side && cell.y < side;
        }

        // ---------------------------------------------------------------
        // Routing
        // ---------------------------------------------------------------

        /// <summary>
        /// A* over (cell, direction we entered it by) states, which is what lets a turn be priced
        /// and lets a cell be legal to pass through but illegal to turn on. Kept alive across all
        /// the links of one plan so each route can see - and share - the ones already committed.
        /// </summary>
        private sealed class Router
        {
            private readonly int side;
            private readonly HashSet<Vector2Int> streets;
            /// <summary>Cells the highway may not occupy at all - the roundabout footprints.</summary>
            private readonly HashSet<Vector2Int> reserved;
            private readonly HashSet<Vector2Int> deck = new HashSet<Vector2Int>();
            /// <summary>Committed ramp cells and every cell touching one, minus the cells the deck legitimately uses.</summary>
            private readonly HashSet<Vector2Int> rampKeepOut = new HashSet<Vector2Int>();

            private readonly int turnPenalty;
            private readonly bool followStreets;
            private readonly int[] gScore;
            private readonly int[] cameFrom;
            private readonly int[] stamp;
            private readonly Heap open = new Heap();
            private int generation;

            public Router(int sideLength, HashSet<Vector2Int> streetCells, HashSet<Vector2Int> reservedCells,
                int turnCost, bool preferStreets)
            {
                side = Mathf.Max(1, sideLength);
                streets = streetCells;
                reserved = reservedCells;
                turnPenalty = turnCost;
                followStreets = preferStreets;
                int states = side * side * 4;
                gScore = new int[states];
                cameFrom = new int[states];
                stamp = new int[states];
            }

            /// <summary>
            /// The deck cells from <paramref name="rampA"/> to <paramref name="rampB"/>, or null if
            /// no legal corridor exists. The ramps themselves are not part of the result: the route
            /// starts on the first cell past A's ramp and ends on the first cell before B's, which
            /// is exactly the run that has to be held at deck height.
            /// </summary>
            public List<Vector2Int> Route(Ramp rampA, Ramp rampB)
            {
                Vector2Int start = rampA.DeckStart;
                Vector2Int goal = rampB.DeckStart;
                if (!InCity(start, side) || !InCity(goal, side))
                {
                    return null;
                }

                // The two ramps of *this* link get the same clearance as the committed ones, so the
                // deck can't run up alongside its own ramp and pick up a phantom junction there.
                var keepOut = new HashSet<Vector2Int>(rampKeepOut);
                AddRampKeepOut(keepOut, rampA);
                AddRampKeepOut(keepOut, rampB);
                keepOut.Remove(start);
                keepOut.Remove(goal);

                // The route has to leave the goal straight down B's ramp, so arriving any other way
                // means turning on the goal cell.
                int goalHeading = DirectionIndexOf(-rampB.Direction);
                if (!IsPassable(start, keepOut) || !IsPassable(goal, keepOut))
                {
                    return null;
                }
                // A ramp is the one part of a link with nothing to reroute, so if its cells have
                // been decked over by an earlier link, this one simply isn't buildable.
                if (!RampCellsAreFree(rampA) || !RampCellsAreFree(rampB))
                {
                    return null;
                }

                generation++;
                open.Clear();

                // The ramp's top cell is a ramp piece, so the deck leaves it dead straight: the
                // search can only enter `start` along the ramp's own direction.
                int startHeading = DirectionIndexOf(rampA.Direction);
                if (!CanEnter(start, startHeading))
                {
                    return null;
                }
                int startState = StateOf(start, startHeading);
                stamp[startState] = generation;
                gScore[startState] = 0;
                cameFrom[startState] = -1;
                open.Push(new HeapItem { F = Heuristic(start, goal), G = 0, Key = startState });

                while (open.Count > 0)
                {
                    HeapItem item = open.Pop();
                    int state = item.Key;
                    if (stamp[state] != generation || gScore[state] != item.G)
                    {
                        continue; // stale entry, already expanded with a better score
                    }
                    Vector2Int cell = CellOf(state);
                    int heading = state & 3;
                    if (cell == goal && (heading == goalHeading || CanTurn(goal, heading, goalHeading)))
                    {
                        List<Vector2Int> route = Rebuild(state);
                        // A route can still double back through a cell it had already decked, which a
                        // per-step rule can't see since the state only knows where it came from. Rare
                        // enough to drop the link over rather than complicate the search.
                        return VisitsEachCellOnce(route) ? route : null;
                    }

                    for (int next = 0; next < Steps.Length; next++)
                    {
                        if (next == ((heading + 2) & 3))
                        {
                            continue; // no U-turn
                        }
                        bool turning = next != heading;
                        if (turning && !CanTurn(cell, heading, next))
                        {
                            continue;
                        }
                        Vector2Int neighbor = cell + Steps[next];
                        if (!IsPassable(neighbor, keepOut) || !CanEnter(neighbor, next))
                        {
                            continue;
                        }
                        int cost = deck.Contains(neighbor) || (followStreets && streets.Contains(neighbor))
                            ? ReuseStepCost
                            : StepCost;
                        if (turning)
                        {
                            cost += StepCost * turnPenalty;
                        }
                        int tentative = item.G + cost;
                        int neighborState = StateOf(neighbor, next);
                        if (stamp[neighborState] == generation && gScore[neighborState] <= tentative)
                        {
                            continue;
                        }
                        stamp[neighborState] = generation;
                        gScore[neighborState] = tentative;
                        cameFrom[neighborState] = state;
                        open.Push(new HeapItem
                        {
                            F = tentative + Heuristic(neighbor, goal),
                            G = tentative,
                            Key = neighborState,
                        });
                    }
                }
                return null;
            }

            /// <summary>Puts a hub's ramp, and the cells flanking it, permanently out of the deck's reach.</summary>
            public void Reserve(Ramp ramp)
            {
                AddRampKeepOut(rampKeepOut, ramp);
            }

            private bool RampCellsAreFree(Ramp ramp)
            {
                foreach (Vector2Int cell in ramp.Cells)
                {
                    if (deck.Contains(cell))
                    {
                        return false;
                    }
                }
                return true;
            }

            public void Commit(Ramp rampA, Ramp rampB, List<Vector2Int> route)
            {
                deck.UnionWith(route);
                AddRampKeepOut(rampKeepOut, rampA);
                AddRampKeepOut(rampKeepOut, rampB);
                foreach (Vector2Int cell in route)
                {
                    rampKeepOut.Remove(cell);
                }
            }

            /// <summary>
            /// A ramp's cells and everything touching them, except the one cell the deck continues
            /// from. The hub street stays out too: on the highway's own Tilemap a deck cell there
            /// would be a second neighbour of the ramp's foot, and the foot would stop reading as a
            /// ramp.
            /// </summary>
            private static void AddRampKeepOut(HashSet<Vector2Int> set, Ramp ramp)
            {
                foreach (Vector2Int cell in ramp.Cells)
                {
                    set.Add(cell);
                    for (int d = 0; d < Steps.Length; d++)
                    {
                        set.Add(cell + Steps[d]);
                    }
                }
                set.Remove(ramp.DeckStart);
            }

            private bool IsPassable(Vector2Int cell, HashSet<Vector2Int> keepOut)
            {
                return InCity(cell, side) && !keepOut.Contains(cell) && !reserved.Contains(cell);
            }

            /// <summary>
            /// Whether the deck may enter <paramref name="cell"/> travelling in
            /// <paramref name="heading"/>. Over a street, always - the highway is right on top of
            /// it, whether following it or flying across. Off the street grid, only if no street
            /// runs alongside: a street beside the cell is fine straight ahead or behind (the deck is
            /// approaching or leaving a crossing), but one to either side would mean a viaduct
            /// running parallel one cell off the road, which is what looks wrong.
            /// </summary>
            private bool CanEnter(Vector2Int cell, int heading)
            {
                return streets.Contains(cell) || !HasLateralStreet(cell, heading, streets);
            }

            /// <summary>
            /// Whether the deck may turn from <paramref name="from"/> to <paramref name="to"/> on
            /// <paramref name="cell"/>. Over a street it may only turn onto, off or along the street -
            /// at least one of the two arms runs along the road below - so it never kinks across the
            /// middle of a street. Off the street grid it may turn only where no street touches the
            /// cell at all, i.e. well clear of the roads.
            /// </summary>
            private bool CanTurn(Vector2Int cell, int from, int to)
            {
                if (streets.Contains(cell))
                {
                    return streets.Contains(cell - Steps[from]) || streets.Contains(cell + Steps[to]);
                }
                for (int d = 0; d < Steps.Length; d++)
                {
                    if (streets.Contains(cell + Steps[d]))
                    {
                        return false;
                    }
                }
                return true;
            }

            private static bool VisitsEachCellOnce(List<Vector2Int> route)
            {
                var visited = new HashSet<Vector2Int>();
                foreach (Vector2Int cell in route)
                {
                    if (!visited.Add(cell))
                    {
                        return false;
                    }
                }
                return true;
            }

            private static int Heuristic(Vector2Int from, Vector2Int to)
            {
                // Every step costs at least ReuseStepCost, so this never overestimates.
                return (Mathf.Abs(from.x - to.x) + Mathf.Abs(from.y - to.y)) * ReuseStepCost;
            }

            private int StateOf(Vector2Int cell, int heading)
            {
                return ((cell.y * side) + cell.x) * 4 + heading;
            }

            private Vector2Int CellOf(int state)
            {
                int cellIndex = state >> 2;
                return new Vector2Int(cellIndex % side, cellIndex / side);
            }

            private List<Vector2Int> Rebuild(int state)
            {
                var route = new List<Vector2Int>();
                for (int s = state; s >= 0; s = cameFrom[s])
                {
                    route.Add(CellOf(s));
                }
                route.Reverse();
                return route;
            }
        }

        /// <summary>True if a street touches <paramref name="cell"/> on either side of travel in <paramref name="heading"/>.</summary>
        private static bool HasLateralStreet(Vector2Int cell, int heading, HashSet<Vector2Int> streets)
        {
            Vector2Int lateral = Steps[(heading + 1) & 3];
            return streets.Contains(cell + lateral) || streets.Contains(cell - lateral);
        }

        private struct HeapItem
        {
            public int F;
            public int G;
            public int Key;
        }

        /// <summary>
        /// Minimal binary min-heap. Unity's netstandard2.1 profile has no PriorityQueue, and the
        /// ordering has to be fully specified anyway - ties break on the deeper node first, then on
        /// the state key, which encodes (y, x, heading) - or the route would depend on heap
        /// internals and stop being reproducible.
        /// </summary>
        private sealed class Heap
        {
            private HeapItem[] items = new HeapItem[256];
            private int count;

            public int Count => count;

            public void Clear()
            {
                count = 0;
            }

            public void Push(HeapItem item)
            {
                if (count == items.Length)
                {
                    Array.Resize(ref items, count * 2);
                }
                int i = count++;
                items[i] = item;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (!Less(items[i], items[parent]))
                    {
                        break;
                    }
                    (items[i], items[parent]) = (items[parent], items[i]);
                    i = parent;
                }
            }

            public HeapItem Pop()
            {
                HeapItem top = items[0];
                items[0] = items[--count];
                int i = 0;
                while (true)
                {
                    int left = i * 2 + 1;
                    int right = left + 1;
                    int smallest = i;
                    if (left < count && Less(items[left], items[smallest])) smallest = left;
                    if (right < count && Less(items[right], items[smallest])) smallest = right;
                    if (smallest == i)
                    {
                        break;
                    }
                    (items[i], items[smallest]) = (items[smallest], items[i]);
                    i = smallest;
                }
                return top;
            }

            private static bool Less(in HeapItem a, in HeapItem b)
            {
                if (a.F != b.F) return a.F < b.F;
                if (a.G != b.G) return a.G > b.G;
                return a.Key < b.Key;
            }
        }
    }
}
