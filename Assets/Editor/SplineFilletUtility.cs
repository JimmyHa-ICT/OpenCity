using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine.Splines;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Pure geometry core: turns a polyline of waypoints into a Spline made of straight
    /// segments joined by rounded (filleted) corners at a single global radius. No
    /// UnityEditor dependency - shared by RoundedSplinePathWindow's two modes so the
    /// fillet math exists exactly once. See Docs/RoundedSplinePath.md.
    /// </summary>
    public static class SplineFilletUtility
    {
        public const float MinCornerAngleDegrees = 0.5f;

        private const float DuplicateEpsilon = 1e-4f;
        private const float OneThird = 1f / 3f;

        public readonly struct FilletPreviewInfo
        {
            public readonly int WaypointCount;
            public readonly int CornerCount;
            public readonly int ClampedCornerCount;
            public readonly int ResultingKnotCount;

            public FilletPreviewInfo(int waypointCount, int cornerCount, int clampedCornerCount, int resultingKnotCount)
            {
                WaypointCount = waypointCount;
                CornerCount = cornerCount;
                ClampedCornerCount = clampedCornerCount;
                ResultingKnotCount = resultingKnotCount;
            }
        }

        public static void BuildRoundedSpline(Spline targetSpline, IReadOnlyList<float3> localWaypoints, float cornerRadius, bool closed)
        {
            List<BezierKnot> knots = Plan(localWaypoints, cornerRadius, closed, out _, out _);

            targetSpline.Clear();
            foreach (BezierKnot knot in knots)
            {
                targetSpline.Add(knot, TangentMode.Broken);
            }
            targetSpline.Closed = closed;
        }

        public static FilletPreviewInfo ComputePreview(IReadOnlyList<float3> localWaypoints, float cornerRadius, bool closed)
        {
            List<BezierKnot> knots = Plan(localWaypoints, cornerRadius, closed, out int cornerCount, out int clampedCornerCount);
            return new FilletPreviewInfo(localWaypoints?.Count ?? 0, cornerCount, clampedCornerCount, knots.Count);
        }

        private static List<BezierKnot> Plan(IReadOnlyList<float3> waypointsIn, float cornerRadius, bool closed,
            out int cornerCount, out int clampedCornerCount)
        {
            cornerCount = 0;
            clampedCornerCount = 0;

            var knots = new List<BezierKnot>();
            if (waypointsIn == null || waypointsIn.Count == 0)
            {
                return knots;
            }

            // Pass 0: drop consecutive near-duplicate waypoints.
            var waypoints = new List<float3>(waypointsIn.Count);
            foreach (float3 p in waypointsIn)
            {
                if (waypoints.Count == 0 || math.distance(waypoints[waypoints.Count - 1], p) > DuplicateEpsilon)
                {
                    waypoints.Add(p);
                }
            }
            if (closed && waypoints.Count > 1 && math.distance(waypoints[0], waypoints[waypoints.Count - 1]) <= DuplicateEpsilon)
            {
                waypoints.RemoveAt(waypoints.Count - 1);
            }

            int n = waypoints.Count;
            if (n == 0)
            {
                return knots;
            }

            // Pass 1: classify each waypoint and place corner tangent points.
            // (n == 1, and n == 2 with closed == false, fall out naturally here: every
            // index is an open endpoint or a degenerate zero-length neighbor, both handled
            // by the branches below without a separate early-out.)
            // Track, per emitted knot, whether each side still needs Pass 2 resolution.
            var pendingIn = new List<bool>();
            var pendingOut = new List<bool>();

            for (int i = 0; i < n; i++)
            {
                bool isOpenEndpoint = !closed && (i == 0 || i == n - 1);
                if (isOpenEndpoint)
                {
                    knots.Add(new BezierKnot(waypoints[i], float3.zero, float3.zero, quaternion.identity));
                    pendingIn.Add(i != 0);       // first knot has no "in" side at all
                    pendingOut.Add(i != n - 1);  // last knot has no "out" side at all
                    continue;
                }

                int prevIdx = (i - 1 + n) % n;
                int nextIdx = (i + 1) % n;
                float3 prev = waypoints[prevIdx];
                float3 corner = waypoints[i];
                float3 next = waypoints[nextIdx];

                float3 toCorner = corner - prev;
                float3 fromCorner = next - corner;
                float segInLen = math.length(toCorner);
                float segOutLen = math.length(fromCorner);
                if (segInLen <= DuplicateEpsilon || segOutLen <= DuplicateEpsilon)
                {
                    knots.Add(new BezierKnot(corner, float3.zero, float3.zero, quaternion.identity));
                    pendingIn.Add(true);
                    pendingOut.Add(true);
                    continue;
                }

                float3 dirIn = toCorner / segInLen;
                float3 dirOut = fromCorner / segOutLen;
                float dot = math.clamp(math.dot(dirIn, dirOut), -1f, 1f);
                float phi = math.acos(dot); // deflection angle, radians

                if (math.degrees(phi) < MinCornerAngleDegrees)
                {
                    // Effectively straight through - single pass-through knot, no fillet.
                    knots.Add(new BezierKnot(corner, float3.zero, float3.zero, quaternion.identity));
                    pendingIn.Add(true);
                    pendingOut.Add(true);
                    continue;
                }

                cornerCount++;
                float halfPhi = phi * 0.5f;
                float rawT = cornerRadius * math.tan(halfPhi);
                float maxT = math.min(segInLen * 0.5f, segOutLen * 0.5f);
                float t = math.min(rawT, maxT);
                bool clamped = t < rawT;
                if (clamped)
                {
                    clampedCornerCount++;
                }

                float effectiveRadius = clamped ? t / math.tan(halfPhi) : cornerRadius;
                float handleLen = effectiveRadius * (4f / 3f) * math.tan(phi * 0.25f);

                float3 a = corner - dirIn * t;
                float3 b = corner + dirOut * t;

                knots.Add(new BezierKnot(a, float3.zero, dirIn * handleLen, quaternion.identity));
                pendingIn.Add(true);   // straight-facing side, resolved in Pass 2
                pendingOut.Add(false); // arc-facing side, already set

                knots.Add(new BezierKnot(b, -dirOut * handleLen, float3.zero, quaternion.identity));
                pendingIn.Add(false);  // arc-facing side, already set
                pendingOut.Add(true);  // straight-facing side, resolved in Pass 2
            }

            ResolveStraightTangents(knots, closed, pendingIn, pendingOut);
            return knots;
        }

        private static void ResolveStraightTangents(List<BezierKnot> knots, bool closed, List<bool> pendingIn, List<bool> pendingOut)
        {
            int count = knots.Count;
            if (count == 0)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                BezierKnot knot = knots[i];

                if (pendingOut[i])
                {
                    int nextIdx = (i + 1) % count;
                    if (closed || i != count - 1)
                    {
                        float3 toNext = knots[nextIdx].Position - knot.Position;
                        float dist = math.length(toNext);
                        knot.TangentOut = dist > DuplicateEpsilon ? (toNext / dist) * (dist * OneThird) : float3.zero;
                    }
                    else
                    {
                        knot.TangentOut = float3.zero;
                    }
                }

                if (pendingIn[i])
                {
                    int prevIdx = (i - 1 + count) % count;
                    if (closed || i != 0)
                    {
                        float3 toPrev = knots[prevIdx].Position - knot.Position;
                        float dist = math.length(toPrev);
                        knot.TangentIn = dist > DuplicateEpsilon ? (toPrev / dist) * (dist * OneThird) : float3.zero;
                    }
                    else
                    {
                        knot.TangentIn = float3.zero;
                    }
                }

                knots[i] = knot;
            }
        }
    }
}
