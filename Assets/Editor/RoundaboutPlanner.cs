using System.Collections.Generic;
using UnityEngine;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Picks which crossroads of a road layout get the 3x3 road-roundabout piece: only the large
    /// intersections, sparingly. For each crossroad it measures the unbroken straight run of road
    /// through it along each axis; the shorter of the two is the crossing's size, and only
    /// crossroads whose size reaches the threshold count as large. BSP cuts often line up, so a
    /// long straight run alone would still mark many crossroads (about half of them in a default
    /// city) - so the large ones are also ranked biggest first and kept a minimum distance apart,
    /// which leaves each part of the city at most one roundabout, at its biggest crossing. The
    /// piece itself is a roundabout centered on its middle cell with one exit in the middle of each
    /// side, so it also needs the crossroad's four neighbors (the arms) to be straight roads leading
    /// away from it and its four diagonal neighbors not to be road. It is symmetric under quarter
    /// turns, so it never needs rotating. Fully deterministic - no randomness at all - and works in
    /// the same city-local cells as CityBspLayout. See Docs/CityBspTilemapGenerator.md, "Roundabouts".
    /// </summary>
    internal static class RoundaboutPlanner
    {
        /// <summary>Roundabouts closer than this (along either axis) would overlap.</summary>
        private const int MinPossibleSpacing = 3;

        public readonly struct Placement
        {
            /// <summary>The crossroad cell the roundabout is centered on.</summary>
            public readonly Vector2Int Center;
            /// <summary>Length in cells of the shorter of the two straight roads crossing here.</summary>
            public readonly int CrossingLength;

            public Placement(Vector2Int center, int crossingLength)
            {
                Center = center;
                CrossingLength = crossingLength;
            }

            /// <summary>The five road cells under the roundabout: the center and its four arms.</summary>
            public Vector2Int[] RoadCells => new[]
            {
                Center, Center + Vector2Int.up, Center + Vector2Int.right, Center + Vector2Int.down, Center + Vector2Int.left,
            };

            public RectInt Footprint => new RectInt(Center.x - 1, Center.y - 1, 3, 3);
        }

        private static readonly Vector2Int[] Directions =
        {
            Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left,
        };

        /// <summary>
        /// Crossroads where both crossing roads are at least <paramref name="minCrossingLength"/>
        /// cells long, taken largest crossing first (then the longer of its two roads, then
        /// position). Each one gets a roundabout if the piece fits and no roundabout already placed
        /// is closer than <paramref name="minSpacing"/> cells along either axis (never less than 3,
        /// so two pieces can't overlap). A crossroad whose 3x3 footprint touches any cell of
        /// <paramref name="elevated"/> (expressway deck and ramps) is skipped: the deck's columns would
        /// stand inside the roundabout, so that crossing stays a flyover crossing.
        /// </summary>
        public static List<Placement> Plan(
            HashSet<Vector2Int> roads, IReadOnlyList<Vector2Int> sortedRoads, int minCrossingLength, int minSpacing,
            HashSet<Vector2Int> elevated = null)
        {
            var placements = new List<Placement>();
            if (roads == null || sortedRoads == null)
            {
                return placements;
            }
            int threshold = Mathf.Max(1, minCrossingLength);
            int spacing = Mathf.Max(MinPossibleSpacing, minSpacing);

            var candidates = new List<(Vector2Int cell, int shorter, int longer)>();
            foreach (Vector2Int cell in sortedRoads)
            {
                if (!IsCrossroad(cell, roads))
                {
                    continue;
                }
                int horizontal = RunLength(cell, Vector2Int.right, roads);
                int vertical = RunLength(cell, Vector2Int.up, roads);
                int shorter = Mathf.Min(horizontal, vertical);
                if (shorter >= threshold)
                {
                    candidates.Add((cell, shorter, Mathf.Max(horizontal, vertical)));
                }
            }
            candidates.Sort((p, q) =>
            {
                if (p.shorter != q.shorter) return q.shorter.CompareTo(p.shorter);
                if (p.longer != q.longer) return q.longer.CompareTo(p.longer);
                return p.cell.y != q.cell.y ? p.cell.y.CompareTo(q.cell.y) : p.cell.x.CompareTo(q.cell.x);
            });

            foreach ((Vector2Int center, int shorter, int _) in candidates)
            {
                if (!Fits(center, roads) || IsTooClose(center, placements, spacing) || UnderDeck(center, elevated))
                {
                    continue;
                }
                placements.Add(new Placement(center, shorter));
            }
            return placements;
        }

        private static bool UnderDeck(Vector2Int center, HashSet<Vector2Int> elevated)
        {
            if (elevated == null)
            {
                return false;
            }
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (elevated.Contains(center + new Vector2Int(dx, dy)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool IsTooClose(Vector2Int center, List<Placement> placed, int spacing)
        {
            foreach (Placement other in placed)
            {
                int distance = Mathf.Max(Mathf.Abs(other.Center.x - center.x), Mathf.Abs(other.Center.y - center.y));
                if (distance < spacing)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Number of road cells in the unbroken straight line through <paramref name="cell"/> along <paramref name="axis"/>.</summary>
        private static int RunLength(Vector2Int cell, Vector2Int axis, HashSet<Vector2Int> roads)
        {
            int length = 1;
            for (Vector2Int c = cell + axis; roads.Contains(c); c += axis)
            {
                length++;
            }
            for (Vector2Int c = cell - axis; roads.Contains(c); c -= axis)
            {
                length++;
            }
            return length;
        }

        private static bool IsCrossroad(Vector2Int cell, HashSet<Vector2Int> roads)
        {
            foreach (Vector2Int direction in Directions)
            {
                if (!roads.Contains(cell + direction))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Every arm is a straight road leading away (road beyond it, none either side of it), and every diagonal cell is not road.</summary>
        private static bool Fits(Vector2Int center, HashSet<Vector2Int> roads)
        {
            for (int dx = -1; dx <= 1; dx += 2)
            {
                for (int dy = -1; dy <= 1; dy += 2)
                {
                    if (roads.Contains(center + new Vector2Int(dx, dy)))
                    {
                        return false;
                    }
                }
            }
            foreach (Vector2Int direction in Directions)
            {
                Vector2Int arm = center + direction;
                var across = new Vector2Int(direction.y, direction.x);
                if (!roads.Contains(arm + direction) || roads.Contains(arm + across) || roads.Contains(arm - across))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
