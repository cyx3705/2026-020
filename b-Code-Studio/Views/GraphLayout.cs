using HistoryJanus.Git;

namespace HistoryJanus.Views;

/// <summary>
/// 提交图谱泳道布局。X 由 Git 父边决定（左祖右孙），Y 优先贴主线并按水平占用复用空行。
/// 已合并平行行保留合并前节点，右侧不画 tip。纯计算：不引用 WPF，不发指令。
/// </summary>
internal static class GraphLayout
{
    internal const double NodeWidth = 148;
    internal const double NodeHeight = 40;
    internal const double LaneHeight = 88;
    internal const double PaddingX = 20;
    internal const double PaddingY = 16;
    internal const double MinGap = 18;

    internal sealed record LaneInfo(int Index, string Title, bool IsOpen);

    internal sealed record PlacedNode(
        GraphCommitNode Node,
        int LaneIndex,
        string LaneTitle,
        bool LaneIsOpen,
        bool IsOpenTip,
        double X,
        double Y);

    internal sealed record PlacedEdge(
        GraphEdge Edge,
        double X1,
        double Y1,
        double X2,
        double Y2);

    internal sealed record Result(
        IReadOnlyList<PlacedNode> Nodes,
        IReadOnlyList<PlacedEdge> Edges,
        IReadOnlyList<LaneInfo> Lanes,
        double Width,
        double Height);

    internal static double LaneTop(int index)
        => PaddingY + index * LaneHeight;

    internal static double LaneCenterY(int index)
        => LaneTop(index) + LaneHeight / 2;

    internal static Result Arrange(GraphCommitsReport report, string projectName)
    {
        var source = report.Nodes ?? [];
        var edges = report.Edges ?? [];
        var laneRefs = report.Lanes is { Count: > 0 }
            ? report.Lanes
            : [new GraphRef { Name = projectName, Kind = BranchKind.Mainline, IsOpen = true }];
        if (source.Count == 0)
        {
            var emptyLanes = laneRefs
                .Select((item, index) => ToLaneInfo(index, item, projectName))
                .ToList();
            var emptyHeight = PaddingY * 2 + LaneHeight * Math.Max(1, emptyLanes.Count);
            return new Result([], [], emptyLanes, PaddingX * 2 + 240, emptyHeight);
        }

        var bySha = new Dictionary<string, GraphCommitNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in source)
        {
            if (!string.IsNullOrWhiteSpace(node.Sha))
                bySha[node.Sha] = node;
        }

        var assignment = AssignLanes(source, bySha, laneRefs, projectName);
        var columns = AssignColumns(source, bySha, assignment.IndexBySha);
        var used = PackTowardMainline(assignment.IndexBySha, assignment.Meta, columns);
        var logicalMeta = assignment.Meta.ToDictionary(item => item.Index);

        var placed = new Dictionary<string, PlacedNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in source)
        {
            var logicalIndex = assignment.IndexBySha.TryGetValue(node.Sha, out var assigned)
                ? assigned
                : 0;
            var laneIndex = used.IndexBySha.TryGetValue(node.Sha, out var packed) ? packed : 0;
            var meta = logicalMeta.TryGetValue(logicalIndex, out var found) ? found : logicalMeta[0];
            var x = columns.TryGetValue(node.Sha, out var column) ? column : PaddingX;
            var y = LaneTop(laneIndex) + (LaneHeight - NodeHeight) / 2;
            placed[node.Sha] = new PlacedNode(
                node, laneIndex, meta.Title, meta.IsOpen, IsOpenTip: false, x, y);
        }

        foreach (var lane in assignment.Meta.Where(item => item.Index > 0 && item.IsOpen))
        {
            if (string.IsNullOrWhiteSpace(lane.TipSha) ||
                !placed.TryGetValue(lane.TipSha, out var tip))
                continue;
            placed[lane.TipSha] = tip with { IsOpenTip = true };
        }

        var width = PaddingX;
        foreach (var item in placed.Values)
            width = Math.Max(width, item.X + NodeWidth + PaddingX);

        var drawn = new List<PlacedEdge>(edges.Count);
        foreach (var edge in edges)
        {
            if (!placed.TryGetValue(edge.FromSha, out var from) ||
                !placed.TryGetValue(edge.ToSha, out var to))
                continue;
            drawn.Add(new PlacedEdge(
                edge,
                from.X + NodeWidth,
                from.Y + NodeHeight / 2,
                to.X,
                to.Y + NodeHeight / 2));
        }

        var lanes = new List<LaneInfo>(assignment.Meta.Count);
        foreach (var meta in assignment.Meta)
        {
            var physical = 0;
            foreach (var pair in assignment.IndexBySha)
            {
                if (pair.Value != meta.Index || !used.IndexBySha.TryGetValue(pair.Key, out physical))
                    continue;
                break;
            }

            lanes.Add(new LaneInfo(physical, meta.Title, meta.IsOpen));
        }

        var rowCount = used.Meta.Count == 0 ? 1 : used.Meta.Keys.Max() + 1;
        var height = PaddingY * 2 + LaneHeight * Math.Max(1, rowCount);
        return new Result([.. placed.Values], drawn, lanes, width, height);
    }

    internal static bool Intersects(
        double x,
        double y,
        double width,
        double height,
        double viewX,
        double viewY,
        double viewW,
        double viewH)
        => x < viewX + viewW && x + width > viewX && y < viewY + viewH && y + height > viewY;

    internal static string RefName(string raw)
    {
        var name = raw.Trim();
        const string heads = "refs/heads/";
        const string remotes = "refs/remotes/";
        if (name.StartsWith(heads, StringComparison.OrdinalIgnoreCase))
            return name[heads.Length..];
        if (name.StartsWith(remotes, StringComparison.OrdinalIgnoreCase))
        {
            var rest = name[remotes.Length..];
            var slash = rest.IndexOf('/');
            return slash >= 0 ? rest[(slash + 1)..] : rest;
        }

        return name.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
            ? name["origin/".Length..]
            : name;
    }

    private static (Dictionary<string, int> IndexBySha, List<LaneMeta> Meta) AssignLanes(
        IReadOnlyList<GraphCommitNode> nodes,
        IReadOnlyDictionary<string, GraphCommitNode> bySha,
        IReadOnlyList<GraphRef> laneRefs,
        string projectName)
    {
        var indexBySha = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var meta = new List<LaneMeta>
        {
            new(0, LaneTitle(laneRefs[0], projectName, mainline: true), true, laneRefs[0].TargetSha),
        };

        var mainTip = ResolveTip(nodes, laneRefs[0], projectName)
                      ?? nodes.MaxBy(node => node.CommittedAt);
        if (mainTip != null)
        {
            foreach (var sha in WalkFirstParent(mainTip.Sha, bySha))
                indexBySha.TryAdd(sha, 0);
        }

        for (var i = 1; i < laneRefs.Count; i++)
        {
            var lane = laneRefs[i];
            var laneIndex = meta.Count;
            var assigned = false;
            var tip = ResolveTip(nodes, lane, projectName);
            var start = tip?.Sha ?? lane.TargetSha;
            foreach (var sha in WalkFirstParent(start, bySha))
            {
                if (indexBySha.ContainsKey(sha))
                    break;
                indexBySha[sha] = laneIndex;
                assigned = true;
            }

            if (!assigned)
                continue;
            meta.Add(new LaneMeta(
                laneIndex,
                LaneTitle(lane, projectName, mainline: false),
                lane.IsOpen,
                lane.TargetSha));
        }

        foreach (var node in nodes)
        {
            if (node.Parents == null || node.Parents.Count < 2)
                continue;
            for (var i = 1; i < node.Parents.Count; i++)
            {
                var start = node.Parents[i];
                if (string.IsNullOrWhiteSpace(start) || !bySha.ContainsKey(start))
                    continue;
                if (indexBySha.TryGetValue(start, out var existing) && existing != 0)
                    continue;

                var laneIndex = meta.Count;
                var assigned = false;
                foreach (var sha in WalkFirstParent(start, bySha))
                {
                    if (indexBySha.TryGetValue(sha, out var occupied) && occupied == 0)
                        break;
                    if (indexBySha.ContainsKey(sha))
                        break;
                    indexBySha[sha] = laneIndex;
                    assigned = true;
                }

                if (!assigned)
                    continue;
                meta.Add(new LaneMeta(
                    laneIndex,
                    $"merged/{(start.Length <= 10 ? start : start[..10])}",
                    false,
                    start));
            }
        }

        foreach (var node in nodes)
            indexBySha.TryAdd(node.Sha, 0);

        return (indexBySha, meta);
    }

    /// <summary>
    /// Reuse rows nearest the mainline. A later branch only moves down when its
    /// X span collides with a branch already sitting on that row.
    /// </summary>
    private static (Dictionary<string, int> IndexBySha, Dictionary<int, LaneMeta> Meta) PackTowardMainline(
        Dictionary<string, int> logicalBySha,
        List<LaneMeta> logicalMeta,
        IReadOnlyDictionary<string, double> columns)
    {
        var byLogical = new Dictionary<int, List<string>>();
        foreach (var pair in logicalBySha)
        {
            if (!byLogical.TryGetValue(pair.Value, out var shas))
            {
                shas = [];
                byLogical[pair.Value] = shas;
            }

            shas.Add(pair.Key);
        }

        var physical = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var sha in byLogical.GetValueOrDefault(0) ?? [])
            physical[sha] = 0;

        var packedMeta = new Dictionary<int, LaneMeta>
        {
            [0] = logicalMeta[0] with { Index = 0 },
        };
        var taken = new Dictionary<int, List<(double Start, double End)>>();
        var branches = new List<(LaneMeta Meta, List<string> Shas, double MinX, double MaxX)>();
        foreach (var meta in logicalMeta)
        {
            if (meta.Index == 0)
                continue;
            var shas = byLogical.GetValueOrDefault(meta.Index) ?? [];
            var minX = PaddingX;
            var maxX = PaddingX;
            var first = true;
            foreach (var sha in shas)
            {
                var x = columns.TryGetValue(sha, out var column) ? column : PaddingX;
                if (first)
                {
                    minX = x;
                    maxX = x + NodeWidth;
                    first = false;
                }
                else
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x + NodeWidth);
                }
            }

            branches.Add((meta, shas, minX, maxX));
        }

        branches.Sort((left, right) =>
        {
            var byStart = left.MinX.CompareTo(right.MinX);
            return byStart != 0 ? byStart : left.Meta.Index.CompareTo(right.Meta.Index);
        });

        foreach (var branch in branches)
        {
            var row = 1;
            while (taken.TryGetValue(row, out var spans) &&
                   spans.Any(span => span.Start < branch.MaxX && branch.MinX < span.End))
                row++;

            if (!taken.TryGetValue(row, out var bucket))
            {
                bucket = [];
                taken[row] = bucket;
            }

            bucket.Add((branch.MinX, branch.MaxX));
            foreach (var sha in branch.Shas)
                physical[sha] = row;
            packedMeta.TryAdd(row, branch.Meta with { Index = row });
        }

        foreach (var pair in logicalBySha)
            physical.TryAdd(pair.Key, 0);
        return (physical, packedMeta);
    }

    /// <summary>
    /// Each commit sits immediately to the right of its rightmost parent and of
    /// the previous commit on the same lane. Wall-clock timestamps never set X:
    /// translating a whole lane to pin one end makes a longer historical row
    /// overshoot the fork or stretch across the global time axis.
    /// </summary>
    private static Dictionary<string, double> AssignColumns(
        IReadOnlyList<GraphCommitNode> nodes,
        IReadOnlyDictionary<string, GraphCommitNode> bySha,
        IReadOnlyDictionary<string, int> laneBySha)
    {
        var step = NodeWidth + MinGap;
        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var remaining = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            children[node.Sha] = [];
            remaining[node.Sha] = 0;
        }

        foreach (var node in nodes)
        {
            foreach (var parent in node.Parents ?? [])
            {
                if (!bySha.ContainsKey(parent))
                    continue;
                remaining[node.Sha]++;
                children[parent].Add(node.Sha);
            }
        }

        var ready = new List<GraphCommitNode>();
        foreach (var node in nodes)
        {
            if (remaining[node.Sha] == 0)
                ready.Add(node);
        }

        ready.Sort(CompareCommitOrder);
        var x = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var lastOnLane = new Dictionary<int, double>();
        while (ready.Count > 0)
        {
            var node = ready[0];
            ready.RemoveAt(0);
            var lane = laneBySha.TryGetValue(node.Sha, out var index) ? index : 0;
            var nodeX = PaddingX;
            if (lastOnLane.TryGetValue(lane, out var previous))
                nodeX = Math.Max(nodeX, previous + step);
            foreach (var parent in node.Parents ?? [])
            {
                if (x.TryGetValue(parent, out var parentX))
                    nodeX = Math.Max(nodeX, parentX + step);
            }

            x[node.Sha] = nodeX;
            lastOnLane[lane] = nodeX;

            var added = false;
            foreach (var childSha in children[node.Sha])
            {
                remaining[childSha]--;
                if (remaining[childSha] != 0)
                    continue;
                ready.Add(bySha[childSha]);
                added = true;
            }

            if (added)
                ready.Sort(CompareCommitOrder);
        }

        foreach (var node in nodes)
            x.TryAdd(node.Sha, PaddingX);
        return x;
    }

    private static int CompareCommitOrder(GraphCommitNode left, GraphCommitNode right)
    {
        var byTime = left.CommittedAt.CompareTo(right.CommittedAt);
        return byTime != 0
            ? byTime
            : StringComparer.OrdinalIgnoreCase.Compare(left.Sha, right.Sha);
    }

    private static GraphCommitNode? ResolveTip(
        IReadOnlyList<GraphCommitNode> nodes,
        GraphRef lane,
        string projectName)
    {
        if (!string.IsNullOrWhiteSpace(lane.TargetSha))
        {
            foreach (var node in nodes)
            {
                if (node.Sha.Equals(lane.TargetSha, StringComparison.OrdinalIgnoreCase))
                    return node;
            }
        }

        GraphCommitNode? tip = null;
        foreach (var node in nodes)
        {
            if (node.BranchRefs == null ||
                !node.BranchRefs.Any(raw =>
                    RefName(raw).Equals(lane.Name, StringComparison.OrdinalIgnoreCase) ||
                    (lane.Kind == BranchKind.Mainline &&
                     RefName(raw).Equals(projectName, StringComparison.OrdinalIgnoreCase))))
                continue;
            if (tip == null || node.CommittedAt > tip.CommittedAt)
                tip = node;
        }

        return tip;
    }

    private static IEnumerable<string> WalkFirstParent(
        string tipSha,
        IReadOnlyDictionary<string, GraphCommitNode> bySha)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = tipSha;
        while (!string.IsNullOrWhiteSpace(current) && seen.Add(current))
        {
            yield return current;
            if (!bySha.TryGetValue(current, out var node) ||
                node.Parents == null ||
                node.Parents.Count == 0)
                yield break;
            current = node.Parents[0];
        }
    }

    private static string LaneTitle(GraphRef lane, string projectName, bool mainline)
    {
        if (mainline || lane.Kind == BranchKind.Mainline)
            return "主线";
        var name = string.IsNullOrWhiteSpace(lane.Name) ? lane.FullName : lane.Name;
        var prefix = $"ai/{projectName}/";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? name[prefix.Length..]
            : name;
    }

    private static LaneInfo ToLaneInfo(int index, GraphRef lane, string projectName)
        => new(index, LaneTitle(lane, projectName, index == 0), index == 0 || lane.IsOpen);

    private sealed record LaneMeta(int Index, string Title, bool IsOpen, string TipSha);
}
