using System.Collections.Generic;
using UnityEngine;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Pure planar recursive rectangle splitting (Binary Space Partitioning) for the BSP
    /// sub-road generator: repeatedly cut a rectangle in two with an axis-aligned line until
    /// the pieces get small enough, and return every cut line as a road segment. Because
    /// every cut is exactly horizontal or vertical by construction, the resulting network
    /// rasterizes onto the road grid with no diagonal staircasing at all - the deliberate
    /// contrast with the Voronoi sub-road tool, whose Delaunay-dual edges land at arbitrary
    /// angles. Deliberately has no dependency on UnityEditor, UnityEngine.Splines, or even
    /// polygon tests, the same separation VoronoiGeometry keeps: clipping the cut lines to
    /// the actual loop interior is the caller's job (SubRoadNetworkShared.RasterizeInteriorSegment),
    /// exactly as it already is for Voronoi edges. Every point here is "planar XZ": a
    /// Vector2(worldX, worldZ), matching RoadGridPlacer's Vector2Int(x, z) cell convention.
    /// See Docs/BspSubRoadGenerator.md.
    /// </summary>
    internal static class BspGeometry
    {
        /// <summary>Which way the cut line runs, i.e. which axis the region is divided along.</summary>
        private enum SplitAxis
        {
            /// <summary>A vertical cut line (constant X) dividing the region into left/right children.</summary>
            Vertical,
            /// <summary>A horizontal cut line (constant Y) dividing the region into bottom/top children.</summary>
            Horizontal,
        }

        /// <summary>
        /// Recursively splits <paramref name="bounds"/> and returns every cut line as a
        /// segment spanning the full extent of the region it divided. Only the cut lines are
        /// returned - the leaf rectangles (the "city blocks" between the roads) are never
        /// materialized, since nothing downstream needs them: placement happens entirely
        /// through the shared grid-cell pipeline. This mirrors VoronoiGeometry's own choice
        /// not to construct bounded Voronoi cell polygons when only the edges matter.
        ///
        /// Pure function of its arguments: the same inputs always produce the same segments
        /// in the same order. Uses a System.Random seeded fresh from <paramref name="seed"/> -
        /// never UnityEngine.Random, whose global state is shared editor-wide and would break
        /// the "same inputs -&gt; same output" guarantee the live preview relies on to match
        /// Generate exactly (see VoronoiGeometry.ScatterSites' doc comment for the same point).
        /// </summary>
        /// <param name="bounds">Region to partition - the caller passes the loop polygon's bounding rectangle.</param>
        /// <param name="maxDepth">Recursion depth cap. 1 = at most a single cut.</param>
        /// <param name="minRegionSize">Smallest world-space width/height a *resulting child* region may have.</param>
        /// <param name="splitJitter">0 = always cut at the exact middle; higher values let the cut wander that fraction of the region's extent either side of center.</param>
        /// <param name="splitAxisBias">Probability of cutting across the region's longer side. 0.5 = coin flip (most varied), 1 = always the longer side (most regular).</param>
        /// <param name="stopProbability">Chance a splittable non-root region stops early anyway, for block-size variety.</param>
        public static List<(Vector2 a, Vector2 b)> BuildSplits(
            Rect bounds, int seed, int maxDepth, float minRegionSize,
            float splitJitter, float splitAxisBias, float stopProbability)
        {
            var segments = new List<(Vector2, Vector2)>();
            if (maxDepth <= 0 || minRegionSize <= 0f || bounds.width <= 0f || bounds.height <= 0f)
            {
                return segments;
            }

            var rng = new System.Random(seed);
            SplitRegion(bounds, 0, maxDepth, minRegionSize, splitJitter, splitAxisBias, stopProbability, rng, segments);
            return segments;
        }

        private static void SplitRegion(
            Rect region, int depth, int maxDepth, float minRegionSize, float splitJitter,
            float splitAxisBias, float stopProbability, System.Random rng,
            List<(Vector2, Vector2)> segments)
        {
            if (depth >= maxDepth)
            {
                return;
            }

            // A region is splittable along an axis only if BOTH halves would still clear
            // minRegionSize - i.e. minRegionSize bounds the resulting *child*, not the parent.
            // This is also what makes the position clamp below a guaranteed-valid range, so no
            // child can ever come out degenerate or negatively sized.
            bool canSplitVertical = region.width >= minRegionSize * 2f;
            bool canSplitHorizontal = region.height >= minRegionSize * 2f;
            if (!canSplitVertical && !canSplitHorizontal)
            {
                return;
            }

            // The root region is exempt from the early-stop roll, so an unlucky first draw can
            // never leave the whole network empty for an otherwise perfectly splittable loop.
            if (depth > 0 && rng.NextDouble() < stopProbability)
            {
                return;
            }

            bool preferVertical = region.width >= region.height; // cut across the longer side
            bool followBias = rng.NextDouble() < Mathf.Clamp01(splitAxisBias);
            bool wantVertical = followBias ? preferVertical : !preferVertical;
            SplitAxis axis =
                wantVertical && canSplitVertical ? SplitAxis.Vertical :
                !wantVertical && canSplitHorizontal ? SplitAxis.Horizontal :
                canSplitVertical ? SplitAxis.Vertical : SplitAxis.Horizontal;

            float jitter = Mathf.Clamp01(splitJitter);
            float t = 0.5f + (float)(rng.NextDouble() * 2.0 - 1.0) * jitter;

            Rect childA;
            Rect childB;
            Vector2 cutFrom;
            Vector2 cutTo;
            if (axis == SplitAxis.Vertical)
            {
                float offset = Mathf.Clamp(t * region.width, minRegionSize, region.width - minRegionSize);
                float splitX = region.xMin + offset;
                childA = Rect.MinMaxRect(region.xMin, region.yMin, splitX, region.yMax);
                childB = Rect.MinMaxRect(splitX, region.yMin, region.xMax, region.yMax);
                cutFrom = new Vector2(splitX, region.yMin);
                cutTo = new Vector2(splitX, region.yMax);
            }
            else
            {
                float offset = Mathf.Clamp(t * region.height, minRegionSize, region.height - minRegionSize);
                float splitY = region.yMin + offset;
                childA = Rect.MinMaxRect(region.xMin, region.yMin, region.xMax, splitY);
                childB = Rect.MinMaxRect(region.xMin, splitY, region.xMax, region.yMax);
                cutFrom = new Vector2(region.xMin, splitY);
                cutTo = new Vector2(region.xMax, splitY);
            }

            segments.Add((cutFrom, cutTo));

            // Fixed child order: the emitted segment list (and every rng draw feeding it) is a
            // function of the traversal, so this order is part of the determinism guarantee.
            SplitRegion(childA, depth + 1, maxDepth, minRegionSize, splitJitter, splitAxisBias, stopProbability, rng, segments);
            SplitRegion(childB, depth + 1, maxDepth, minRegionSize, splitJitter, splitAxisBias, stopProbability, rng, segments);
        }
    }
}
