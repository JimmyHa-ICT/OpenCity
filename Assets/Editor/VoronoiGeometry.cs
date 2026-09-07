using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Pure planar-geometry utilities for the Voronoi sub-road generator: polygon tests,
    /// deterministic site scattering, Bowyer-Watson Delaunay triangulation, and the
    /// Voronoi dual restricted to interior (non-hull) edges. Deliberately has no
    /// dependency on UnityEditor or UnityEngine.Splines so it stays unit-testable and
    /// reusable in isolation, the same separation VoronoiSubRoadWindow keeps from
    /// RoadGridPlacer. Every point here is "planar XZ": a Vector2(worldX, worldZ), matching
    /// RoadGridPlacer's Vector2Int(x, z) cell convention. See Docs/VoronoiSubRoadGenerator.md.
    /// </summary>
    internal static class VoronoiGeometry
    {
        public struct Triangle
        {
            public int A;
            public int B;
            public int C;
            public Vector2 Circumcenter;
            public float CircumradiusSq;
        }

        public readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            public readonly int Lo;
            public readonly int Hi;

            public EdgeKey(int u, int v)
            {
                if (u <= v) { Lo = u; Hi = v; }
                else { Lo = v; Hi = u; }
            }

            public bool Equals(EdgeKey other) => Lo == other.Lo && Hi == other.Hi;
            public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode() => (Lo * 397) ^ Hi;
        }

        /// <summary>Winding-number point-in-polygon test - correct for the non-convex, rounded loop shapes this tool expects, unlike even-odd ray casting.</summary>
        public static bool PointInPolygon(Vector2 p, IReadOnlyList<Vector2> poly)
        {
            int windingNumber = 0;
            int count = poly.Count;
            for (int i = 0; i < count; i++)
            {
                Vector2 a = poly[i];
                Vector2 b = poly[(i + 1) % count];
                float isLeft = (b.x - a.x) * (p.y - a.y) - (p.x - a.x) * (b.y - a.y);
                if (a.y <= p.y)
                {
                    if (b.y > p.y && isLeft > 0f) windingNumber++;
                }
                else
                {
                    if (b.y <= p.y && isLeft < 0f) windingNumber--;
                }
            }
            return windingNumber != 0;
        }

        public static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float sqrLen = ab.sqrMagnitude;
            if (sqrLen < 1e-12f)
            {
                return Vector2.Distance(p, a);
            }
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / sqrLen);
            Vector2 closest = a + t * ab;
            return Vector2.Distance(p, closest);
        }

        public static float DistanceToPolygonBoundary(Vector2 p, IReadOnlyList<Vector2> poly)
        {
            float min = float.MaxValue;
            int count = poly.Count;
            for (int i = 0; i < count; i++)
            {
                float d = DistancePointToSegment(p, poly[i], poly[(i + 1) % count]);
                if (d < min) min = d;
            }
            return min;
        }

        public static Vector2 PolygonCentroid(IReadOnlyList<Vector2> poly)
        {
            Vector2 sum = Vector2.zero;
            for (int i = 0; i < poly.Count; i++) sum += poly[i];
            return poly.Count > 0 ? sum / poly.Count : Vector2.zero;
        }

        public static float PolygonBoundingRadius(IReadOnlyList<Vector2> poly, Vector2 centroid)
        {
            float maxSqr = 0f;
            for (int i = 0; i < poly.Count; i++)
            {
                float sqr = (poly[i] - centroid).sqrMagnitude;
                if (sqr > maxSqr) maxSqr = sqr;
            }
            return Mathf.Sqrt(maxSqr);
        }

        /// <summary>
        /// Rejection-sampling site scatter: candidates are drawn from the polygon's AABB
        /// and kept only if inside the polygon, at least <paramref name="margin"/> from its
        /// boundary, and at least <paramref name="minSpacing"/> from every already-accepted
        /// site. Stops at <paramref name="requestedCount"/> accepted sites or
        /// requestedCount * maxAttemptsPerSite attempts, whichever comes first - a partial
        /// result (fewer than requested) is valid, not an error, since a tight
        /// margin/spacing relative to the polygon's area can make the full count
        /// unreachable. Uses a System.Random seeded fresh from <paramref name="seed"/> -
        /// never UnityEngine.Random, whose global state is shared editor-wide and would
        /// break the "same inputs -> same output" guarantee this tool relies on for its
        /// live preview to match Generate exactly.
        /// </summary>
        public static List<Vector2> ScatterSites(
            IReadOnlyList<Vector2> polygon, int requestedCount, float minSpacing, float margin,
            int seed, out int attemptsUsed, int maxAttemptsPerSite = 200)
        {
            var accepted = new List<Vector2>();
            attemptsUsed = 0;

            if (polygon.Count < 3 || requestedCount <= 0)
            {
                return accepted;
            }

            Vector2 min = polygon[0];
            Vector2 max = polygon[0];
            for (int i = 1; i < polygon.Count; i++)
            {
                min = Vector2.Min(min, polygon[i]);
                max = Vector2.Max(max, polygon[i]);
            }

            var rng = new System.Random(seed);
            int attemptCap = requestedCount * Mathf.Max(1, maxAttemptsPerSite);
            float width = max.x - min.x;
            float height = max.y - min.y;

            while (accepted.Count < requestedCount && attemptsUsed < attemptCap)
            {
                attemptsUsed++;
                var candidate = new Vector2(
                    min.x + (float)rng.NextDouble() * width,
                    min.y + (float)rng.NextDouble() * height);

                if (!PointInPolygon(candidate, polygon)) continue;
                if (DistanceToPolygonBoundary(candidate, polygon) < margin) continue;

                bool tooClose = false;
                for (int i = 0; i < accepted.Count; i++)
                {
                    if (Vector2.Distance(candidate, accepted[i]) < minSpacing)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;

                accepted.Add(candidate);
            }

            return accepted;
        }

        /// <summary>
        /// Standard Bowyer-Watson incremental Delaunay triangulation. Builds an oversized
        /// super-triangle around the sites, inserts each site in list order (remove every
        /// triangle whose circumcircle contains it, re-triangulate the resulting hole from
        /// its boundary edges), then strips every triangle still touching a super-triangle
        /// vertex. Near-duplicate sites are pre-merged defensively (a misconfigured
        /// Min Site Spacing of 0 could otherwise feed two coincident points in, which makes
        /// any triangle containing both numerically degenerate). Near-collinear triples
        /// simply fail to produce a triangle (see MakeTriangle) rather than being specially
        /// handled - the resulting gap gets covered by later insertions, which is standard
        /// Bowyer-Watson degenerate-handling.
        /// </summary>
        public static List<Triangle> Triangulate(IReadOnlyList<Vector2> sites)
        {
            var triangles = new List<Triangle>();

            var deduped = new List<Vector2>(sites.Count);
            foreach (Vector2 s in sites)
            {
                bool duplicate = false;
                for (int i = 0; i < deduped.Count; i++)
                {
                    if ((deduped[i] - s).sqrMagnitude < 1e-8f) { duplicate = true; break; }
                }
                if (!duplicate) deduped.Add(s);
            }

            int n = deduped.Count;
            if (n < 3)
            {
                return triangles;
            }

            Vector2 min = deduped[0];
            Vector2 max = deduped[0];
            for (int i = 1; i < n; i++)
            {
                min = Vector2.Min(min, deduped[i]);
                max = Vector2.Max(max, deduped[i]);
            }
            float dx = max.x - min.x;
            float dy = max.y - min.y;
            float deltaMax = Mathf.Max(dx, dy);
            if (deltaMax <= 0f) deltaMax = 1f;
            Vector2 mid = (min + max) * 0.5f;

            var points = new List<Vector2>(deduped)
            {
                mid + new Vector2(-20f * deltaMax, -deltaMax),
                mid + new Vector2(0f, 20f * deltaMax),
                mid + new Vector2(20f * deltaMax, -deltaMax),
            };
            int p0 = n, p1 = n + 1, p2 = n + 2;

            Triangle? superTriangle = MakeTriangle(points, p0, p1, p2);
            if (superTriangle == null)
            {
                return triangles;
            }
            triangles.Add(superTriangle.Value);

            for (int i = 0; i < n; i++)
            {
                Vector2 p = points[i];

                var badIndices = new List<int>();
                for (int ti = 0; ti < triangles.Count; ti++)
                {
                    if (InCircumcircle(p, triangles[ti]))
                    {
                        badIndices.Add(ti);
                    }
                }

                var edgeCounts = new Dictionary<EdgeKey, int>();
                var edgeList = new List<EdgeKey>();
                foreach (int ti in badIndices)
                {
                    Triangle t = triangles[ti];
                    AddEdgeOccurrence(edgeCounts, edgeList, new EdgeKey(t.A, t.B));
                    AddEdgeOccurrence(edgeCounts, edgeList, new EdgeKey(t.B, t.C));
                    AddEdgeOccurrence(edgeCounts, edgeList, new EdgeKey(t.C, t.A));
                }

                for (int bi = badIndices.Count - 1; bi >= 0; bi--)
                {
                    triangles.RemoveAt(badIndices[bi]);
                }

                foreach (EdgeKey edge in edgeList)
                {
                    if (edgeCounts[edge] != 1) continue; // shared by two bad triangles - interior to the hole, not its boundary
                    Triangle? newTriangle = MakeTriangle(points, edge.Lo, edge.Hi, i);
                    if (newTriangle != null)
                    {
                        triangles.Add(newTriangle.Value);
                    }
                }
            }

            triangles.RemoveAll(t => t.A >= n || t.B >= n || t.C >= n);
            return triangles;
        }

        private static void AddEdgeOccurrence(Dictionary<EdgeKey, int> counts, List<EdgeKey> uniqueEdges, EdgeKey key)
        {
            if (counts.TryGetValue(key, out int c))
            {
                counts[key] = c + 1;
            }
            else
            {
                counts[key] = 1;
                uniqueEdges.Add(key);
            }
        }

        private static Triangle? MakeTriangle(List<Vector2> points, int a, int b, int c)
        {
            Vector2 A = points[a];
            Vector2 B = points[b];
            Vector2 C = points[c];

            float d = 2f * (A.x * (B.y - C.y) + B.x * (C.y - A.y) + C.x * (A.y - B.y));
            if (Mathf.Abs(d) < 1e-7f)
            {
                return null; // near-collinear triple - degenerate, skip
            }

            float aSq = A.x * A.x + A.y * A.y;
            float bSq = B.x * B.x + B.y * B.y;
            float cSq = C.x * C.x + C.y * C.y;
            float ux = (aSq * (B.y - C.y) + bSq * (C.y - A.y) + cSq * (A.y - B.y)) / d;
            float uy = (aSq * (C.x - B.x) + bSq * (A.x - C.x) + cSq * (B.x - A.x)) / d;
            var circumcenter = new Vector2(ux, uy);

            return new Triangle
            {
                A = a,
                B = b,
                C = c,
                Circumcenter = circumcenter,
                CircumradiusSq = (circumcenter - A).sqrMagnitude,
            };
        }

        private static bool InCircumcircle(Vector2 p, Triangle t)
        {
            // Relative epsilon (scaled to the triangle's own circumradius) rather than a
            // fixed constant, since a fixed epsilon would be simultaneously too loose for a
            // tightly-packed low-margin scattering and too tight for a large sparse one.
            float epsilon = Mathf.Max(t.CircumradiusSq * 1e-6f, 1e-9f);
            return (p - t.Circumcenter).sqrMagnitude <= t.CircumradiusSq + epsilon;
        }

        public static Dictionary<EdgeKey, List<int>> BuildEdgeToTriangleMap(List<Triangle> triangles)
        {
            var map = new Dictionary<EdgeKey, List<int>>();
            for (int ti = 0; ti < triangles.Count; ti++)
            {
                Triangle t = triangles[ti];
                AddTriangleEdge(map, new EdgeKey(t.A, t.B), ti);
                AddTriangleEdge(map, new EdgeKey(t.B, t.C), ti);
                AddTriangleEdge(map, new EdgeKey(t.C, t.A), ti);
            }
            return map;
        }

        private static void AddTriangleEdge(Dictionary<EdgeKey, List<int>> map, EdgeKey key, int triangleIndex)
        {
            if (!map.TryGetValue(key, out List<int> list))
            {
                list = new List<int>();
                map[key] = list;
            }
            list.Add(triangleIndex);
        }

        /// <summary>
        /// Builds the Voronoi dual restricted to interior edges: a triangulation edge
        /// shared by exactly 2 triangles yields a segment between their circumcenters. An
        /// edge belonging to only 1 triangle is a Delaunay hull-boundary edge - it would
        /// correspond to an unbounded Voronoi ray, which this tool has no use for (sub-roads
        /// must stay inset from the loop boundary regardless), so it's dropped outright
        /// rather than extended. The runaway-distance check discards pathological
        /// obtuse-sliver circumcenters (which can land far outside the polygon) before
        /// they'd otherwise get finely sampled for nothing in the caller's clipping pass -
        /// it changes no correct output (the interior/margin clip downstream would reject
        /// those samples anyway), it only bounds worst-case cost.
        /// </summary>
        public static void BuildVoronoiSegments(
            List<Triangle> triangles,
            Dictionary<EdgeKey, List<int>> edgeMap,
            Vector2 polygonCentroid,
            float boundingRadius,
            float runawayMultiplier,
            out List<(Vector2 a, Vector2 b)> kept,
            out int droppedHullCount,
            out int droppedRunawayCount)
        {
            kept = new List<(Vector2, Vector2)>();
            droppedHullCount = 0;
            droppedRunawayCount = 0;
            float runawayDistance = boundingRadius * runawayMultiplier;

            foreach (KeyValuePair<EdgeKey, List<int>> entry in edgeMap)
            {
                List<int> adjacentTriangles = entry.Value;
                if (adjacentTriangles.Count == 1)
                {
                    droppedHullCount++;
                    continue;
                }
                if (adjacentTriangles.Count != 2)
                {
                    continue;
                }

                Vector2 c0 = triangles[adjacentTriangles[0]].Circumcenter;
                Vector2 c1 = triangles[adjacentTriangles[1]].Circumcenter;

                if (Vector2.Distance(c0, polygonCentroid) > runawayDistance ||
                    Vector2.Distance(c1, polygonCentroid) > runawayDistance)
                {
                    droppedRunawayCount++;
                    continue;
                }

                kept.Add((c0, c1));
            }
        }
    }
}
