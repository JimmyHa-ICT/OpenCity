using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// Plumbing shared by every "fill a closed main-road loop's interior with a sub-road
    /// network" tool - currently VoronoiSubRoadWindow and BspSubRoadWindow. These two differ
    /// only in how they decide the network's *geometry* (Voronoi dual of scattered sites vs.
    /// recursive rectangle splitting); everything on either side of that decision - sampling
    /// the loop spline into a polygon, rasterizing the loop itself onto the grid as the
    /// main-road footprint, clipping and rasterizing a continuous segment onto the grid, and
    /// the whole main-road connection mechanic - is identical, and lives here so the two
    /// tools can never drift apart on any of it.
    ///
    /// Every method is a pure function of its arguments (the two that touch the scene do so
    /// only through explicitly-passed names/roots), so a caller's "the live preview always
    /// shows exactly what Generate will produce" guarantee survives being routed through
    /// here. See Docs/VoronoiSubRoadGenerator.md and Docs/BspSubRoadGenerator.md.
    /// </summary>
    internal static class SubRoadNetworkShared
    {
        /// <summary>
        /// What FindMainRoadConnections resolved: which candidate connections exist at all,
        /// and which of them could actually be wired into a real placed Road tile. Returned
        /// as a value rather than written into a caller's own result struct, since each
        /// window's network-result type is private to that window.
        /// </summary>
        public struct MainRoadConnectionResult
        {
            public GameObject MainRoadRoot;
            public int CandidateConnectionCount;
            /// <summary>Extra open-direction bits to OR into each connecting sub-road cell's own mask.</summary>
            public Dictionary<Vector2Int, RoadGridPlacer.Direction> SubConnectionExtra;
            /// <summary>Extra open-direction bits to OR into each connecting main-road cell's own mask.</summary>
            public Dictionary<Vector2Int, RoadGridPlacer.Direction> RoadConnectionExtra;
            /// <summary>The actual placed Road tile instance to replace at each connecting main-road cell.</summary>
            public Dictionary<Vector2Int, GameObject> RoadConnectionTarget;
        }

        /// <summary>
        /// Samples a closed spline at a fixed arc-length step into a continuous world-XZ
        /// polygon - the same SplineUtility.Evaluate/GetPointAtLinearDistance technique the
        /// spline Road Generator uses to rasterize onto a grid, but collecting continuous
        /// points here since this polygon is used for geometric tests (point-in-polygon,
        /// distance-to-boundary), not placed directly.
        /// </summary>
        public static List<Vector2> SamplePolygon(Spline spline, float step, Transform splineTransform)
        {
            var polygon = new List<Vector2>();
            float totalLength = spline.GetLength();
            if (totalLength <= 0.0001f)
            {
                return polygon;
            }

            float curT = 0f;
            AppendPolygonSample(spline, curT, polygon, splineTransform);

            float remaining = totalLength;
            while (remaining > step)
            {
                SplineUtility.GetPointAtLinearDistance<Spline>(spline, curT, step, out float nextT);
                curT = nextT;
                remaining -= step;
                AppendPolygonSample(spline, curT, polygon, splineTransform);
            }

            return polygon;
        }

        private static void AppendPolygonSample(Spline spline, float t, List<Vector2> polygon, Transform splineTransform)
        {
            SplineUtility.Evaluate<Spline>(spline, t, out float3 posLocal, out _, out _);
            Vector3 world = splineTransform.TransformPoint((Vector3)posLocal);
            polygon.Add(new Vector2(world.x, world.z));
        }

        /// <summary>
        /// Rasterizes the closed loop polygon onto the same integer grid the sub-road tools'
        /// CellToWorld/WorldToCell use - i.e. the set of cells the main spline-based Road
        /// Generator would occupy for this exact spline and cell size. Walks consecutive
        /// polygon samples (already sampled at Polygon Sample Step) plus the wrap-around edge
        /// back to the first point, bridging each pair with RoadGridPlacer.WalkOrthogonalBridged
        /// the same way RoadGeneratorWindow.Rasterize bridges consecutive spline samples - so
        /// this reproduces that tool's own occupied-cell set without needing a live "Road"
        /// object to read from.
        /// </summary>
        public static HashSet<Vector2Int> RasterizeLoopCells(List<Vector2> polygon, float cellSize)
        {
            var cells = new List<Vector2Int>();
            if (polygon.Count == 0)
            {
                return new HashSet<Vector2Int>();
            }

            Vector2Int? previousCell = null;
            for (int i = 0; i < polygon.Count; i++)
            {
                RoadGridPlacer.WalkOrthogonalBridged(cells, ref previousCell, WorldToCell(polygon[i], cellSize));
            }
            RoadGridPlacer.WalkOrthogonalBridged(cells, ref previousCell, WorldToCell(polygon[0], cellSize));

            return new HashSet<Vector2Int>(cells);
        }

        private static Vector2Int WorldToCell(Vector2 xz, float cellSize)
        {
            return new Vector2Int(Mathf.RoundToInt(xz.x / cellSize), Mathf.RoundToInt(xz.y / cellSize));
        }

        /// <summary>
        /// Samples a segment at fixed world-space steps and rasterizes only the maximal
        /// sub-runs of samples that stay inside the loop polygon by at least
        /// <paramref name="boundaryMargin"/>. A run never bridges across a sample that falls
        /// outside that interior band - a real gap (the segment briefly leaving the safe zone)
        /// must not be silently stitched over with a staircase that would cut through the
        /// excluded margin band.
        /// </summary>
        public static void RasterizeInteriorSegment(
            Vector2 p0, Vector2 p1, List<Vector2> polygon, float cellSize, float sampleStep,
            float boundaryMargin, List<Vector2Int> outCells)
        {
            float length = Vector2.Distance(p0, p1);
            int sampleCount = Mathf.Max(1, Mathf.CeilToInt(length / sampleStep));

            List<Vector2Int> currentRun = null;
            Vector2Int? previousCell = null;

            for (int i = 0; i <= sampleCount; i++)
            {
                float t = (float)i / sampleCount;
                Vector2 sample = Vector2.Lerp(p0, p1, t);
                bool interior = VoronoiGeometry.PointInPolygon(sample, polygon) &&
                                 VoronoiGeometry.DistanceToPolygonBoundary(sample, polygon) >= boundaryMargin;

                if (interior)
                {
                    if (currentRun == null)
                    {
                        currentRun = new List<Vector2Int>();
                        previousCell = null;
                    }
                    var cell = new Vector2Int(Mathf.RoundToInt(sample.x / cellSize), Mathf.RoundToInt(sample.y / cellSize));
                    RoadGridPlacer.WalkOrthogonalBridged(currentRun, ref previousCell, cell);
                }
                else if (currentRun != null)
                {
                    outCells.AddRange(currentRun);
                    currentRun = null;
                }
            }

            if (currentRun != null)
            {
                outCells.AddRange(currentRun);
            }
        }

        /// <summary>
        /// Wherever a final sub-road cell is orthogonally adjacent to a main-road cell, that's
        /// a deliberate connection point rather than something Boundary Margin is meant to
        /// prevent (only an exact-cell collision is hard-excluded, by the caller). For each
        /// candidate connection this looks up the actual placed Road tile - by nearest
        /// position under <paramref name="mainRoadParentName"/>/<paramref name="mainRoadParentUnder"/>,
        /// not just assumed from the spline - so a candidate with no matching Road tile found
        /// (Road not generated yet, wrong parent name, ...) is silently left un-applied on
        /// both sides rather than half-wired. Callers do this lookup inside their network-build
        /// pass rather than only at Generate time, which is what keeps the preview and Generate
        /// in agreement about exactly which connections will land.
        /// </summary>
        public static MainRoadConnectionResult FindMainRoadConnections(
            List<Vector2Int> subRoadCells, HashSet<Vector2Int> mainRoadCells, float cellSize,
            string mainRoadParentName, Transform mainRoadParentUnder, Func<Vector2Int, Vector3> cellToWorld)
        {
            var candidateSubExtra = new Dictionary<Vector2Int, RoadGridPlacer.Direction>();
            var candidateRoadExtra = new Dictionary<Vector2Int, RoadGridPlacer.Direction>();
            foreach (Vector2Int subCell in subRoadCells)
            {
                foreach (KeyValuePair<RoadGridPlacer.Direction, Vector2Int> dir in RoadGridPlacer.DirectionDeltas)
                {
                    Vector2Int neighbor = subCell + dir.Value;
                    if (!mainRoadCells.Contains(neighbor))
                    {
                        continue;
                    }
                    candidateSubExtra[subCell] = candidateSubExtra.TryGetValue(subCell, out RoadGridPlacer.Direction subMask)
                        ? subMask | dir.Key : dir.Key;
                    RoadGridPlacer.Direction back = RoadGridPlacer.Opposite(dir.Key);
                    candidateRoadExtra[neighbor] = candidateRoadExtra.TryGetValue(neighbor, out RoadGridPlacer.Direction roadMask)
                        ? roadMask | back : back;
                }
            }

            var result = new MainRoadConnectionResult
            {
                CandidateConnectionCount = candidateRoadExtra.Count,
            };

            var appliedSubExtra = new Dictionary<Vector2Int, RoadGridPlacer.Direction>();
            var appliedRoadExtra = new Dictionary<Vector2Int, RoadGridPlacer.Direction>();
            var roadConnectionTarget = new Dictionary<Vector2Int, GameObject>();

            GameObject roadRoot = RoadGridPlacer.FindGeneratedRoot(mainRoadParentName, mainRoadParentUnder);
            result.MainRoadRoot = roadRoot;

            if (roadRoot != null && candidateRoadExtra.Count > 0)
            {
                foreach (KeyValuePair<Vector2Int, RoadGridPlacer.Direction> entry in candidateRoadExtra)
                {
                    Transform nearest = FindNearestChild(roadRoot.transform, cellToWorld(entry.Key), cellSize);
                    if (nearest == null)
                    {
                        continue;
                    }
                    appliedRoadExtra[entry.Key] = entry.Value;
                    roadConnectionTarget[entry.Key] = nearest.gameObject;
                }

                foreach (KeyValuePair<Vector2Int, RoadGridPlacer.Direction> entry in candidateSubExtra)
                {
                    RoadGridPlacer.Direction keep = RoadGridPlacer.Direction.None;
                    foreach (KeyValuePair<RoadGridPlacer.Direction, Vector2Int> dir in RoadGridPlacer.DirectionDeltas)
                    {
                        if ((entry.Value & dir.Key) != 0 && appliedRoadExtra.ContainsKey(entry.Key + dir.Value))
                        {
                            keep |= dir.Key;
                        }
                    }
                    if (keep != RoadGridPlacer.Direction.None)
                    {
                        appliedSubExtra[entry.Key] = keep;
                    }
                }
            }

            result.SubConnectionExtra = appliedSubExtra;
            result.RoadConnectionExtra = appliedRoadExtra;
            result.RoadConnectionTarget = roadConnectionTarget;
            return result;
        }

        /// <summary>
        /// Finds the child under <paramref name="parent"/> whose position is closest to
        /// <paramref name="expectedWorldPos"/> in XZ, within a generous per-cell tolerance.
        /// Placed tiles aren't always exactly at their cell center - road-bend (and siblings)
        /// have an off-center pivot corrected at placement time, shifting the instance's actual
        /// position by up to roughly half a cell - so nearest-within-tolerance is used instead
        /// of an exact position match.
        /// </summary>
        private static Transform FindNearestChild(Transform parent, Vector3 expectedWorldPos, float cellSize)
        {
            Transform best = null;
            float bestDistSqr = float.MaxValue;
            float toleranceSqr = (cellSize * 0.75f) * (cellSize * 0.75f);
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                float dx = child.position.x - expectedWorldPos.x;
                float dz = child.position.z - expectedWorldPos.z;
                float distSqr = dx * dx + dz * dz;
                if (distSqr < bestDistSqr)
                {
                    bestDistSqr = distSqr;
                    best = child;
                }
            }
            return (best != null && bestDistSqr <= toleranceSqr) ? best : null;
        }

        /// <summary>
        /// Replaces each connecting main-road cell's placed tile (found earlier by
        /// FindMainRoadConnections) with whatever the shared grammar resolves for its base mask
        /// (self-only connectivity among <paramref name="mainRoadCells"/>, i.e. what the spline
        /// Road Generator would have classified it as) OR'd with the extra direction(s) facing
        /// the sub-road. Uses the *calling tool's* own Shapes prefabs, since a connector piece
        /// needs to visually match the sub-road kit for the join to read as one continuous
        /// network. Returns how many tiles were actually replaced.
        /// </summary>
        public static int ReplaceMainRoadConnectionTiles(
            HashSet<Vector2Int> mainRoadCells,
            Dictionary<Vector2Int, RoadGridPlacer.Direction> roadConnectionExtra,
            Dictionary<Vector2Int, GameObject> roadConnectionTarget,
            GameObject mainRoadRoot,
            Dictionary<RoadGridPlacer.ShapeCategory, RoadGridPlacer.RoadShapeConfig> configByCategory,
            Func<Vector2Int, Vector3> cellToWorld,
            string undoLabel,
            string logPrefix)
        {
            if (roadConnectionExtra == null || roadConnectionExtra.Count == 0)
            {
                return 0;
            }
            if (mainRoadRoot == null)
            {
                // Locked network snapshot referenced a Road root that no longer exists (deleted
                // or regenerated since Lock) - connections computed at lock/preview time can't be
                // applied against a live object that isn't there anymore.
                Debug.LogWarning($"[{logPrefix}] Main Road object referenced by the locked/previewed network " +
                    "no longer exists - connections to it were skipped. Unlock and re-preview to pick up the change.");
                return 0;
            }

            Dictionary<RoadGridPlacer.ShapeCategory, RoadGridPlacer.Direction> openAtZeroByCategory =
                RoadGridPlacer.BuildOpenAtZeroByCategory();
            Dictionary<Vector2Int, RoadGridPlacer.Direction> roadBaseGraph = RoadGridPlacer.BuildGraph(mainRoadCells);
            int replaced = 0;

            foreach (KeyValuePair<Vector2Int, RoadGridPlacer.Direction> entry in roadConnectionExtra)
            {
                Vector2Int roadCell = entry.Key;
                RoadGridPlacer.Direction baseMask = roadBaseGraph.TryGetValue(roadCell, out RoadGridPlacer.Direction bm) ? bm : RoadGridPlacer.Direction.None;
                RoadGridPlacer.Direction newMask = baseMask | entry.Value;

                if (!RoadGridPlacer.TryClassify(newMask, out RoadGridPlacer.ShapeCategory category))
                {
                    Debug.LogWarning($"[{logPrefix}] Main-road cell {roadCell} has an unrecognized connectivity " +
                        $"mask ({newMask}) after connecting - left unchanged.");
                    continue;
                }
                RoadGridPlacer.RoadShapeConfig config = configByCategory[category];
                if (config.prefab == null)
                {
                    Debug.LogWarning($"[{logPrefix}] Main-road cell {roadCell} needs a {category} prefab to " +
                        "connect, which is not assigned in Shapes - left unchanged.");
                    continue;
                }
                if (!RoadGridPlacer.TryFindRotationSteps(openAtZeroByCategory[category], newMask, out int steps))
                {
                    Debug.LogWarning($"[{logPrefix}] Main-road cell {roadCell} ({category}, mask {newMask}) has " +
                        "no matching rotation after connecting - left unchanged.");
                    continue;
                }

                GameObject oldInstance = roadConnectionTarget[roadCell];
                if (oldInstance == null)
                {
                    Debug.LogWarning($"[{logPrefix}] Main-road tile at cell {roadCell} referenced by the " +
                        "locked/previewed network no longer exists - connection skipped.");
                    continue;
                }
                Quaternion rotation = Quaternion.Euler(0f, steps * 90f, 0f);
                Vector3 worldPos = cellToWorld(roadCell) + rotation * RoadGridPlacer.ComputePivotOffset(config.prefab);

                Undo.DestroyObjectImmediate(oldInstance);
                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(config.prefab, mainRoadRoot.transform);
                instance.transform.SetPositionAndRotation(worldPos, rotation);
                instance.name = $"{config.prefab.name}_junction_{roadCell.x}_{roadCell.y}";
                Undo.RegisterCreatedObjectUndo(instance, undoLabel);
                replaced++;
            }

            return replaced;
        }

        /// <summary>
        /// The self-only sub-road connectivity graph (RoadGridPlacer.BuildGraph over
        /// <paramref name="cells"/>) with each applied main-road connection's extra direction
        /// bit merged in. Used by every place that classifies/rotates a sub-road cell - the
        /// stats box, the Scene view gizmos, and Generate itself - so a connecting cell is
        /// never classified one way in the preview and placed another way by Generate.
        /// </summary>
        public static Dictionary<Vector2Int, RoadGridPlacer.Direction> BuildAugmentedGraph(
            List<Vector2Int> cells, Dictionary<Vector2Int, RoadGridPlacer.Direction> subConnectionExtra)
        {
            Dictionary<Vector2Int, RoadGridPlacer.Direction> graph = RoadGridPlacer.BuildGraph(cells);
            if (subConnectionExtra != null)
            {
                foreach (KeyValuePair<Vector2Int, RoadGridPlacer.Direction> entry in subConnectionExtra)
                {
                    graph[entry.Key] = graph.TryGetValue(entry.Key, out RoadGridPlacer.Direction mask)
                        ? mask | entry.Value : entry.Value;
                }
            }
            return graph;
        }
    }
}
