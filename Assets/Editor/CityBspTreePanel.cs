using System;
using System.Collections.Generic;
using OpenCity.Tiles;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace OpenCity.EditorTools
{
    /// <summary>
    /// The BSP tree of a city as a collapsible tree: one row per region, from the whole city down
    /// to its blocks, each labelled with the cut that split it or why it stayed a block. Shared by
    /// the City Road Generator (BSP Tilemap) window, which shows the live preview's tree, and the
    /// CityBspTree Inspector, which shows the tree stored by Generate. Selecting a row highlights
    /// that region in the Scene view (see <see cref="DrawSceneSelection"/>); double-clicking frames it.
    /// </summary>
    internal sealed class CityBspTreePanel
    {
        private const float TreeHeight = 240f;

        private static readonly Color RegionColor = new Color(1f, 0.9f, 0.2f);
        private static readonly Color CutColor = new Color(1f, 0.45f, 0.1f);
        private static readonly Color BlockColor = new Color(0.25f, 0.8f, 0.35f);

        private readonly TreeViewState<int> state;
        private NodeTreeView view;
        private IReadOnlyList<CityBspTree.Node> shownNodes;

        /// <summary>Called when the selection changes or a row is double-clicked, so the host can repaint the Scene view.</summary>
        public event Action Changed;

        /// <param name="state">Pass a [SerializeField] state to keep expansion and selection across reloads.</param>
        public CityBspTreePanel(TreeViewState<int> state)
        {
            this.state = state ?? new TreeViewState<int>();
        }

        /// <summary>The selected node's index, or -1.</summary>
        public int SelectedIndex
        {
            get
            {
                if (shownNodes == null || state.selectedIDs.Count == 0)
                {
                    return -1;
                }
                int index = state.selectedIDs[0];
                return index >= 0 && index < shownNodes.Count ? index : -1;
            }
        }

        /// <summary>
        /// Draws the tree for <paramref name="nodes"/>. Rebuilds the rows only when a different list is
        /// passed, so pass the same list instance for as long as the tree is unchanged. When
        /// <paramref name="frame"/> is given, double-clicking a row calls it with that node.
        /// </summary>
        public void OnGUI(IReadOnlyList<CityBspTree.Node> nodes, Action<CityBspTree.Node> frame)
        {
            if (nodes == null || nodes.Count == 0)
            {
                EditorGUILayout.HelpBox("The tree is empty - the city has no BSP regions.", MessageType.None);
                return;
            }
            if (view == null)
            {
                view = new NodeTreeView(state, this);
            }
            if (!ReferenceEquals(shownNodes, nodes))
            {
                bool firstBuild = shownNodes == null && state.expandedIDs.Count == 0;
                shownNodes = nodes;
                view.Reload();
                if (firstBuild)
                {
                    view.SetExpanded(0, true);
                }
                if (state.selectedIDs.Count > 0 && SelectedIndex < 0)
                {
                    view.SetSelection(new List<int>());
                }
            }
            view.Frame = frame;

            using (new EditorGUILayout.HorizontalScope())
            {
                int blocks = 0;
                int maxDepth = 0;
                foreach (CityBspTree.Node node in nodes)
                {
                    if (node.IsBlock)
                    {
                        blocks++;
                    }
                    maxDepth = Mathf.Max(maxDepth, node.depth);
                }
                EditorGUILayout.LabelField($"{nodes.Count} region(s), {blocks} block(s), depth {maxDepth}", EditorStyles.miniLabel);
                if (GUILayout.Button("Expand All", EditorStyles.miniButtonLeft, GUILayout.Width(75)))
                {
                    view.ExpandAll();
                }
                if (GUILayout.Button("Collapse All", EditorStyles.miniButtonRight, GUILayout.Width(75)))
                {
                    view.CollapseAll();
                }
            }
            Rect rect = GUILayoutUtility.GetRect(0f, 10000f, TreeHeight, TreeHeight);
            view.OnGUI(rect);

            int selected = SelectedIndex;
            EditorGUILayout.HelpBox(
                selected >= 0
                    ? Describe(nodes, selected)
                    : "Select a region to highlight it in the Scene view (yellow = region, orange = its cut, green = " +
                      "its blocks). Double-click to frame it.",
                MessageType.None);
        }

        /// <summary>Highlights the selected region, its cut and its blocks. Call from a Scene view repaint.</summary>
        public void DrawSceneSelection(Tilemap tilemap, Vector2Int originCell)
        {
            int selected = SelectedIndex;
            if (selected < 0 || tilemap == null)
            {
                return;
            }
            IReadOnlyList<CityBspTree.Node> nodes = shownNodes;
            CityBspTree.Node node = nodes[selected];
            // Above the preview's own gizmos and the street pieces, below the highway deck.
            const float lift = 0.12f;

            var blockFill = new Color(BlockColor.r, BlockColor.g, BlockColor.b, 0.3f);
            foreach (int index in SubtreeIndices(nodes, selected))
            {
                if (nodes[index].IsBlock)
                {
                    Handles.DrawSolidRectangleWithOutline(Corners(tilemap, originCell, nodes[index].rect, lift), blockFill, BlockColor);
                }
            }
            if (!node.IsBlock)
            {
                var cutFill = new Color(CutColor.r, CutColor.g, CutColor.b, 0.6f);
                Handles.DrawSolidRectangleWithOutline(Corners(tilemap, originCell, node.cut, lift * 1.5f), cutFill, CutColor);
            }
            Vector3[] outline = Corners(tilemap, originCell, node.rect, lift * 2f);
            Handles.color = RegionColor;
            Handles.DrawAAPolyLine(4f, outline[0], outline[1], outline[2], outline[3], outline[0]);
        }

        /// <summary>Frames <paramref name="rect"/> (city-local cells) in the last active Scene view.</summary>
        public static void FrameInSceneView(Tilemap tilemap, Vector2Int originCell, RectInt rect)
        {
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null || tilemap == null)
            {
                return;
            }
            Vector3[] corners = Corners(tilemap, originCell, rect, 0f);
            var bounds = new Bounds(corners[0], Vector3.zero);
            for (int i = 1; i < corners.Length; i++)
            {
                bounds.Encapsulate(corners[i]);
            }
            sceneView.Frame(bounds, false);
        }

        /// <summary>
        /// The layout's regions as the serializable nodes CityBspTree stores, in the same preorder.
        /// <paramref name="expresswayNodes"/> (may be null) marks the regions whose cut became an expressway.
        /// </summary>
        public static List<CityBspTree.Node> ToTreeNodes(CityBspLayout.Layout layout, HashSet<int> expresswayNodes)
        {
            var nodes = new List<CityBspTree.Node>(layout.Nodes.Count);
            for (int i = 0; i < layout.Nodes.Count; i++)
            {
                CityBspLayout.Node node = layout.Nodes[i];
                nodes.Add(new CityBspTree.Node
                {
                    rect = node.Rect,
                    depth = node.Depth,
                    parent = node.Parent,
                    childA = node.ChildA,
                    childB = node.ChildB,
                    cut = node.Cut,
                    cutVertical = node.CutVertical,
                    blockIndex = node.BlockIndex,
                    leafReason = node.LeafReason,
                    expressway = expresswayNodes != null && expresswayNodes.Contains(i),
                });
            }
            return nodes;
        }

        /// <summary>One row's label: which side of its parent it is, then the split or block.</summary>
        private static string Label(IReadOnlyList<CityBspTree.Node> nodes, int index)
        {
            CityBspTree.Node node = nodes[index];
            string size = $"{node.rect.width}x{node.rect.height}";
            string side = SideName(nodes, index);
            if (node.IsBlock)
            {
                return $"{side} - Block #{node.blockIndex}  {size}";
            }
            string cut = node.cutVertical ? $"cut N-S at x={node.cut.x}" : $"cut E-W at y={node.cut.y}";
            return $"{side}  {size}  ({cut}{(node.expressway ? ", expressway" : "")})";
        }

        private static string SideName(IReadOnlyList<CityBspTree.Node> nodes, int index)
        {
            int parentIndex = nodes[index].parent;
            if (parentIndex < 0)
            {
                return "City";
            }
            CityBspTree.Node parent = nodes[parentIndex];
            bool isA = parent.childA == index;
            return parent.cutVertical ? (isA ? "West" : "East") : (isA ? "South" : "North");
        }

        private static string Describe(IReadOnlyList<CityBspTree.Node> nodes, int index)
        {
            CityBspTree.Node node = nodes[index];
            RectInt r = node.rect;
            string text = $"{SideName(nodes, index)} region, depth {node.depth}\n" +
                          $"Cells x {r.xMin}..{r.xMax - 1}, y {r.yMin}..{r.yMax - 1} ({r.width}x{r.height}, city-local)";
            if (node.IsBlock)
            {
                return text + $"\nBlock #{node.blockIndex}: {LeafReasonText(node.leafReason)}";
            }
            int blocks = 0;
            foreach (int i in SubtreeIndices(nodes, index))
            {
                if (nodes[i].IsBlock)
                {
                    blocks++;
                }
            }
            string cut = node.cutVertical
                ? $"North-south {(node.expressway ? "expressway" : "road")} at x={node.cut.x} splits it into West {nodes[node.childA].rect.width} + East {nodes[node.childB].rect.width} cells"
                : $"East-west {(node.expressway ? "expressway" : "road")} at y={node.cut.y} splits it into South {nodes[node.childA].rect.height} + North {nodes[node.childB].rect.height} cells";
            return text + $"\n{cut}\nContains {blocks} block(s)";
        }

        private static string LeafReasonText(BspLeafReason reason)
        {
            switch (reason)
            {
                case BspLeafReason.TooSmall: return "too small to cut (or no road for a cut to connect to).";
                case BspLeafReason.MaxDepth: return "reached Max Split Depth.";
                case BspLeafReason.StopRoll: return "stopped early by Stop Probability.";
                case BspLeafReason.NoCutPosition: return "every cut would have put two intersections side by side.";
                default: return "not cut.";
            }
        }

        /// <summary>The node and all its descendants. Preorder makes a subtree a contiguous run of indices.</summary>
        private static IEnumerable<int> SubtreeIndices(IReadOnlyList<CityBspTree.Node> nodes, int index)
        {
            int depth = nodes[index].depth;
            yield return index;
            for (int i = index + 1; i < nodes.Count && nodes[i].depth > depth; i++)
            {
                yield return i;
            }
        }

        private static Vector3[] Corners(Tilemap tilemap, Vector2Int originCell, RectInt rect, float lift)
        {
            var origin = new Vector3Int(originCell.x + rect.x, originCell.y + rect.y, 0);
            Vector3 up = Vector3.up * lift;
            return new[]
            {
                tilemap.CellToWorld(origin) + up,
                tilemap.CellToWorld(origin + new Vector3Int(rect.width, 0, 0)) + up,
                tilemap.CellToWorld(origin + new Vector3Int(rect.width, rect.height, 0)) + up,
                tilemap.CellToWorld(origin + new Vector3Int(0, rect.height, 0)) + up,
            };
        }

        private sealed class NodeTreeView : TreeView<int>
        {
            private readonly CityBspTreePanel panel;
            public Action<CityBspTree.Node> Frame;

            public NodeTreeView(TreeViewState<int> state, CityBspTreePanel panel) : base(state)
            {
                this.panel = panel;
                showAlternatingRowBackgrounds = true;
            }

            protected override TreeViewItem<int> BuildRoot()
            {
                var root = new TreeViewItem<int>(-1, -1, "Root");
                IReadOnlyList<CityBspTree.Node> nodes = panel.shownNodes;
                var rows = new List<TreeViewItem<int>>(nodes.Count);
                for (int i = 0; i < nodes.Count; i++)
                {
                    rows.Add(new TreeViewItem<int>(i, nodes[i].depth, Label(nodes, i)));
                }
                SetupParentsAndChildrenFromDepths(root, rows);
                return root;
            }

            protected override bool CanMultiSelect(TreeViewItem<int> item)
            {
                return false;
            }

            protected override void SelectionChanged(IList<int> selectedIds)
            {
                panel.Changed?.Invoke();
            }

            protected override void DoubleClickedItem(int id)
            {
                if (id >= 0 && id < panel.shownNodes.Count)
                {
                    Frame?.Invoke(panel.shownNodes[id]);
                }
                panel.Changed?.Invoke();
            }
        }
    }
}
