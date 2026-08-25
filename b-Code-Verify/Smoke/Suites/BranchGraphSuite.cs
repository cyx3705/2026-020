using System.Xml.Linq;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryJanus.Git;
using HistoryJanus.Views;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>只读提交 DAG：编号主线、平行泳道（含已合并历史）与独立图谱窗口。</summary>
internal static class BranchGraphSuite
{
    private const string baseBranch = "0000-000-Template";
    private const string parentBranch = "2026-001-Parent";
    private const string childBranch = "2026-002-Child";
    private const string aliasAiName = "ai/2026-002-Child/abcdef0-1-on-main";
    private const string mergedAiName = "ai/2026-002-Child/side-merged";
    private const string openAiName = "ai/2026-002-Child/open-fix";

    public static async Task RunAsync(string[] args)
    {
        VerifyGraphLayoutV1();
        var root = TemporaryDirectory("branch-graph");
        var template = Path.Combine(root, baseBranch);
        var parent = Path.Combine(root, parentBranch);
        var child = Path.Combine(root, childBranch);
        var other = Path.Combine(root, "2026-003-Other");
        Directory.CreateDirectory(root);

        try
        {
            await InitStandaloneRepo(template, "Branch Graph Smoke", "branch-graph@example.invalid");
            await WriteProjectManifest(template, "", isTemplate: true);
            await File.WriteAllTextAsync(Path.Combine(template, "README.md"), "template\n");
            Ensure(await GitRunner.RunAsync(template, ["add", "."]), "add template");
            Ensure(await GitRunner.RunAsync(template, ["commit", "-m", "template root"]), "commit template");
            var templateSha = Sha(await GitRunner.RunAsync(template, ["rev-parse", "HEAD"]));

            await InitStandaloneRepo(parent, "Branch Graph Smoke", "branch-graph@example.invalid");
            await WriteProjectManifest(parent, baseBranch);
            await File.WriteAllTextAsync(Path.Combine(parent, "README.md"), "template\n");
            await File.WriteAllTextAsync(Path.Combine(parent, "parent.txt"), "parent one\n");
            Ensure(await GitRunner.RunAsync(parent, ["add", "."]), "add parent");
            Ensure(await GitRunner.RunAsync(parent, ["commit", "-m", "parent one"]), "commit parent");
            var parentSha = Sha(await GitRunner.RunAsync(parent, ["rev-parse", "HEAD"]));

            await InitStandaloneRepo(child, "Branch Graph Smoke", "branch-graph@example.invalid");
            await WriteProjectManifest(child, parentBranch);
            await File.WriteAllTextAsync(Path.Combine(child, "README.md"), "child\n");
            Ensure(await GitRunner.RunAsync(child, ["add", "."]), "add child seed");
            Ensure(await GitRunner.RunAsync(child, ["commit", "-m", "child seed"]), "commit child seed");
            await CommitFile(child, "child.txt", "child one\n", "child one");
            var childOneSha = Sha(await GitRunner.RunAsync(child, ["rev-parse", "HEAD"]));

            Ensure(await GitRunner.RunAsync(child, ["checkout", "-b", "side-merge"]), "branch side");
            await CommitFile(child, "side.txt", "side change\n", "side change");
            var sideSha = Sha(await GitRunner.RunAsync(child, ["rev-parse", "HEAD"]));
            Ensure(await GitRunner.RunAsync(child, ["checkout", "main"]), "back to main");
            Ensure(await GitRunner.RunAsync(child, ["merge", "--no-ff", "side-merge", "-m", "merge side"]),
                "merge side into child");
            var mergeSha = Sha(await GitRunner.RunAsync(child, ["rev-parse", "HEAD"]));

            Ensure(await GitRunner.RunAsync(child,
                ["update-ref", $"refs/heads/{aliasAiName}", childOneSha]), "create mainline-alias ai ref");
            Ensure(await GitRunner.RunAsync(child,
                ["update-ref", $"refs/heads/{mergedAiName}", sideSha]), "create merged ai ref");

            Ensure(await GitRunner.RunAsync(child, ["checkout", "-b", openAiName]), "branch open ai");
            await CommitFile(child, "open.txt", "still open\n", "open work");
            var openSha = Sha(await GitRunner.RunAsync(child, ["rev-parse", "HEAD"]));
            Ensure(await GitRunner.RunAsync(child, ["checkout", "main"]), "return main");

            await InitStandaloneRepo(other, "Branch Graph Smoke", "branch-graph@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(other, "README.md"), "other\n");
            Ensure(await GitRunner.RunAsync(other, ["add", "."]), "add other");
            Ensure(await GitRunner.RunAsync(other, ["commit", "-m", "other root"]), "commit other");

            var settings = new MemorySettings();
            BindLibrary(settings, root, baseBranch);
            var projects = new ProjectService(settings, _ => true, root);
            var graph = new GraphService(projects);

            var missing = await graph.GetSummaryAsync("2026-999-Missing");
            True(!missing.Success && missing.Message.Contains("未找到", StringComparison.Ordinal),
                $"missing project fails clearly: {missing.Message}");

            var branches = await graph.GetBranchesAsync(childBranch);
            True(branches.Success && branches.Report != null, $"branches loaded: {branches.Message}");
            var branchReport = branches.Report!;
            Equal(ProjectService.MainlineBranch, branchReport.Mainline?.Name, "child mainline is main");
            True(branchReport.Inheritance.Count == 0, "inheritance parent chain is not a graph lane");
            True(branchReport.Parallels.Any(item => item.Name == mergedAiName && !item.IsOpen),
                "merged ai/ ref keeps a closed parallel lane");
            True(branchReport.Parallels.Any(item => item.Name == openAiName && item.IsOpen),
                "unmerged ai/ ref keeps an open parallel lane");
            True(!branchReport.Parallels.Any(item => item.Name == aliasAiName),
                "ai/ tip that only aliases mainline first-parent is not a lane");
            True(branchReport.AiWork.All(item => item.Kind == BranchKind.AiWork),
                "ai refs carry AiWork kind");
            True(branchReport.Mainline?.Kind == BranchKind.Mainline, "mainline kind");

            var commits = await graph.GetCommitsAsync(childBranch, limit: 50);
            True(commits.Success && commits.Report != null, $"commits loaded: {commits.Message}");
            var report = commits.Report!;
            True(!report.Nodes.Any(node => node.Sha == templateSha),
                "template history is cut off from numbered-project graph");
            True(!report.Nodes.Any(node => node.Sha == parentSha),
                "parent-branch history is cut off from numbered-project graph");
            True(report.Nodes.Any(node => node.Sha == childOneSha),
                "numbered-branch unique commit stays on the graph");
            True(report.Nodes.Any(node => node.Sha == sideSha),
                "merged parallel keeps pre-merge unique commits");
            True(report.Nodes.Any(node => node.Sha == openSha),
                "unmerged parallel tip is on the graph");
            True(report.Lanes.Count >= 3 && report.Lanes[0].Name == ProjectService.MainlineBranch,
                "lanes start with main then parallels");
            True(report.Lanes.Any(item => item.Name == mergedAiName && !item.IsOpen),
                "commit lanes mark merged parallel closed");
            True(report.Lanes.Any(item => item.Name == openAiName && item.IsOpen),
                "commit lanes mark unmerged parallel open");
            True(report.Edges.Any(edge =>
                    edge.ToSha == mergeSha && edge.IsMergeParent && edge.FromSha == sideSha),
                "merge commit exposes merge-parent edge");
            True(report.Edges.Any(edge =>
                    edge.ToSha == mergeSha && !edge.IsMergeParent),
                "merge commit exposes first-parent edge");

            var layout = GraphLayout.Arrange(report, childBranch);
            True(layout.Lanes.Any(lane => !lane.IsOpen && lane.Index > 0),
                "merged parallel still occupies a historical row");
            True(layout.Nodes.Any(node => node.Node.Sha == sideSha && node.LaneIndex > 0 && !node.IsOpenTip),
                "merged parallel row ends without a right-side tip");
            True(layout.Nodes.Any(node => node.Node.Sha == openSha && node.IsOpenTip),
                "unmerged parallel draws a right-side tip");
            True(layout.Nodes.Any(node => node.Node.Sha == mergeSha && node.LaneIndex == 0),
                "merge commit stays on the mainline");
            AssertAdjacent(layout, childOneSha, sideSha,
                "merged side starts immediately after its mainline fork");
            AssertAdjacent(layout, sideSha, mergeSha,
                "merged side ends immediately before the mainline merge");
            AssertAdjacent(layout, mergeSha, openSha,
                "open parallel starts immediately after the commit it forked from");
            Equal(1, Lane(layout, sideSha), "first parallel sits on the row nearest the mainline");
            Equal(Lane(layout, sideSha), Lane(layout, openSha),
                "non-overlapping merged and open rows reuse the nearest free lane");

            var node = await graph.GetNodeAsync(childBranch, mergeSha);
            True(node.Success && node.Detail != null, $"node detail loaded: {node.Message}");
            True(node.Detail!.ParentEdges.Any(edge => edge.IsMergeParent && edge.FromSha == sideSha),
                "node detail lists merge parent");

            var outside = await graph.GetNodeAsync(childBranch, templateSha);
            True(!outside.Success, $"template commit is not in this project's object store: {outside.Message}");

            var isolated = await GitRunner.RunAsync(other, ["cat-file", "-e", childOneSha]);
            True(!isolated.Success, "sibling repo cannot see a commit that only exists in the child object store");

            var created = await projects.CreateAsync("2026-004-Fresh", baseBranch, null);
            True(created.Success, $"create independent repo: {created.Message}");
            var imported = await GitRunner.RunAsync(
                Path.Combine(root, "2026-004-Fresh"), ["cat-file", "-e", templateSha]);
            True(!imported.Success, "create copies files then init; template objects stay out of the new store");

            Ensure(await GitRunner.RunAsync(child, ["update-ref", "-d", "refs/heads/side-merge"]),
                "delete merged side branch");
            Ensure(await GitRunner.RunAsync(child, ["update-ref", "-d", $"refs/heads/{mergedAiName}"]),
                "delete merged ai ref");

            var recovered = await graph.GetCommitsAsync(childBranch, limit: 50);
            True(recovered.Success && recovered.Report != null,
                $"commits after deleting merged refs: {recovered.Message}");
            True(recovered.Report!.Nodes.Any(item => item.Sha == sideSha),
                "unique commits of a deleted merged branch remain reachable from the merge");
            True(recovered.Report.Lanes.Any(item => item.TargetSha == sideSha && !item.IsOpen),
                "merge second-parent recovers a closed historical lane after the branch is deleted");
            True(!recovered.Report.Lanes.Any(item => item.Name == mergedAiName),
                "deleted ai/ ref is no longer listed as a named lane");
            var recoveredLayout = GraphLayout.Arrange(recovered.Report, childBranch);
            True(recoveredLayout.Nodes.Any(item =>
                    item.Node.Sha == sideSha && item.LaneIndex > 0 && !item.IsOpenTip),
                "recovered merge history still occupies a row without a right-side tip");

            var registry = new CommandRegistry();
            GraphCommands.RegisterAll(registry, graph);
            var commands = registry.All().ToDictionary(command => command.Name, StringComparer.OrdinalIgnoreCase);
            True(commands.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([
                "janus.graph.summary", "janus.graph.branches", "janus.graph.commits", "janus.graph.node",
            ]), "graph command catalog complete");
            True(commands.Values.All(descriptor => descriptor.Readonly
                    && descriptor.Readonly
                    && descriptor.ConfirmPrompt == null),
                "graph commands are readonly with no confirmation");
            True(!registry.All().Any(descriptor => descriptor.Name.StartsWith("janus.proj.", StringComparison.Ordinal)),
                "graph suite registers no project mutation commands");
        }
        finally
        {
            if (Directory.Exists(root))
                DeleteTree(root);
        }
    }

    private static void VerifyGraphLayoutV1()
    {
        var module = File.ReadAllText(Path.Combine(RepoRoot, "Module", "HistoryJanusUiModule.cs"));
        Contains(module, "schemaVersion = 1", "graph page uses Aurora page protocol V1");
        Contains(module, "type = \"swimlane\"", "graph page uses Aurora swimlane component");
        Contains(module, "view == \"graph\"", "graph data remains available through janus.ui.data");
        Contains(module, "dataSource = new { command = \"janus.ui.data\"",
            "graph and overview delegate component data loading to Aurora 1.8.3");
        Contains(module, "tabTarget = \"console\"", "graph joins the host console tab group");
    }

    private static void VerifyGraphLayout()
    {
        True(File.Exists(Path.Combine(RepoRoot, "Views", "GraphView.xaml")),
            "GraphView.xaml exists");

        var graphView = File.ReadAllText(Path.Combine(RepoRoot, "Views", "GraphView.xaml"));
        Contains(graphView, "Aurora.Brush.Surface",
            "GraphView uses host surface token");
        Contains(graphView, "Aurora.Brush.Hairline",
            "GraphView uses host hairline token");

        True(!graphView.Contains("LaneLegend", StringComparison.Ordinal),
            "GraphView has no left-side lane legend");
        Contains(graphView, "HorizontalScrollBarVisibility=\"Hidden\"",
            "graph hides the horizontal scrollbar and pans by dragging");
        Contains(graphView, "VerticalScrollBarVisibility=\"Hidden\"",
            "graph hides the vertical scrollbar and pans by dragging");
        Contains(graphView, "PreviewMouseWheel",
            "graph swallows the mouse wheel instead of scrolling");

        var graphViewCode = File.ReadAllText(Path.Combine(RepoRoot, "Views", "GraphView.xaml.cs"));
        Contains(graphViewCode, "CaptureMouse",
            "graph pans the canvas by dragging the background");
        Contains(graphViewCode, "HitGraphNode",
            "graph drag-pan does not steal commit node clicks");
        Contains(graphViewCode, "DebouncedAction",
            "graph coalesces overview selection instead of cancelling in-flight HTTP");
        True(!graphViewCode.Contains("\"UI\", cancellation)", StringComparison.Ordinal),
            "graph loads do not pass a cancellation token that aborts the host HTTP write");

        var layoutSource = File.ReadAllText(Path.Combine(RepoRoot, "Views", "GraphLayout.cs"));
        Contains(layoutSource, "IsOpenTip",
            "layout distinguishes open parallel tips from merged historical rows");
        True(!layoutSource.Contains("TotalSeconds", StringComparison.Ordinal),
            "layout X is not mapped from a global timestamp span");
        VerifyParallelLaneAnchorsToFork();
        VerifyMergedLaneAnchorsToMerge();
        VerifyMergedLaneDoesNotOvershootFork();
        VerifyOpenLaneStaysCompactNearFork();
        VerifyNonOverlappingBranchesShareNearestLane();
        VerifyOverlappingBranchesStackFromMainline();

        var overview = XDocument.Load(Path.Combine(RepoRoot, "Views", "OverviewView.xaml"));
        var overviewSource = overview.ToString();
        True(!overviewSource.Contains("GraphHost", StringComparison.Ordinal),
            "overview no longer embeds the graph");
        True(!overview.Descendants().Any(element => element.Name.LocalName == "GridSplitter"),
            "overview is a full-height project list");

        var module = File.ReadAllText(Path.Combine(RepoRoot, "Module", "HistoryJanusUiModule.cs"));
        var windowIds = System.Text.RegularExpressions.Regex.Matches(
                module, @"Id = ""(?<id>[a-z]+)""", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Select(match => match.Groups["id"].Value)
            .ToArray();
        True(windowIds.SequenceEqual(["overview", "graph", "projops"]),
            "module registers overview, graph, and projops");
        Contains(module, "DefaultSide = DockSide.Tab",
            "graph joins a host tab group instead of occupying the center document pane");
        Contains(module, "DefaultTabTarget = StandardWindowIds.Console",
            "graph default tab target is the host console");
    }

    private static void VerifyParallelLaneAnchorsToFork()
    {
        var root = Commit("root", At(2026, 1, 1));
        var fork = Commit("fork", At(2026, 1, 3), root.Sha, BranchRefs: ["main"]);
        var branch = Commit("branch", At(2025, 12, 20), fork.Sha, BranchRefs: ["refs/heads/ai/demo/fix-layout"]);
        var layout = GraphLayout.Arrange(
            new GraphCommitsReport
            {
                ProjectName = "demo",
                Nodes = [root, fork, branch],
                Edges = [new GraphEdge { FromSha = fork.Sha, ToSha = branch.Sha }],
                Lanes =
                [
                    new GraphRef { Name = "main", Kind = BranchKind.Mainline, TargetSha = fork.Sha },
                    new GraphRef
                    {
                        Name = "ai/demo/fix-layout",
                        Kind = BranchKind.AiWork,
                        TargetSha = branch.Sha,
                        IsOpen = true,
                    },
                ],
            },
            "demo");
        AssertAdjacent(layout, fork.Sha, branch.Sha,
            "parallel lane starts immediately after its mainline fork even when timestamps are out of order");
    }

    private static void VerifyMergedLaneAnchorsToMerge()
    {
        var root = Commit("root", At(2026, 1, 1));
        var mainBefore = Commit("main-before", At(2026, 1, 3), root.Sha, BranchRefs: ["main"]);
        var branchBase = Commit("branch-base", At(2025, 12, 20), root.Sha);
        var branchTip = Commit("branch-tip", At(2025, 12, 21), branchBase.Sha);
        var merge = Commit("merge", At(2026, 1, 4), mainBefore.Sha, branchTip.Sha, BranchRefs: ["main"]);
        var layout = ArrangeClosed(
            [root, mainBefore, branchBase, branchTip, merge],
            merge.Sha,
            branchTip.Sha);
        AssertAdjacent(layout, branchTip.Sha, merge.Sha,
            "merged historical lane ends immediately before the mainline merge even when timestamps are out of order");
    }

    private static void VerifyMergedLaneDoesNotOvershootFork()
    {
        var fork = Commit("fork", At(2026, 1, 3));
        var mainMid = Commit("main-mid", At(2026, 1, 4), fork.Sha);
        var b1 = Commit("b1", At(2025, 12, 1), fork.Sha);
        var b2 = Commit("b2", At(2025, 12, 2), b1.Sha);
        var b3 = Commit("b3", At(2025, 12, 3), b2.Sha);
        var b4 = Commit("b4", At(2025, 12, 4), b3.Sha);
        var merge = Commit("merge", At(2026, 1, 5), mainMid.Sha, b4.Sha);
        var layout = ArrangeClosed(
            [fork, mainMid, b1, b2, b3, b4, merge],
            merge.Sha,
            b4.Sha);
        AssertAdjacent(layout, fork.Sha, b1.Sha,
            "a longer merged lane still starts immediately after the fork instead of running left of it");
        AssertAdjacent(layout, b4.Sha, merge.Sha,
            "a longer merged lane still ends immediately before the merge");
        True(X(layout, b1.Sha) >= X(layout, fork.Sha),
            "merged historical commits must not overshoot the fork they came from");
    }

    private static void VerifyOpenLaneStaysCompactNearFork()
    {
        var fork = Commit("fork", At(2026, 1, 1));
        var mainTip = Commit("main-tip", At(2026, 8, 1), fork.Sha);
        var b1 = Commit("b1", At(2025, 6, 1), fork.Sha);
        var b2 = Commit("b2", At(2026, 3, 1), b1.Sha);
        var b3 = Commit("b3", At(2026, 8, 15), b2.Sha, BranchRefs: ["refs/heads/ai/demo/long"]);
        var layout = GraphLayout.Arrange(
            new GraphCommitsReport
            {
                ProjectName = "demo",
                Nodes = [fork, mainTip, b1, b2, b3],
                Edges =
                [
                    new GraphEdge { FromSha = fork.Sha, ToSha = mainTip.Sha },
                    new GraphEdge { FromSha = fork.Sha, ToSha = b1.Sha },
                    new GraphEdge { FromSha = b1.Sha, ToSha = b2.Sha },
                    new GraphEdge { FromSha = b2.Sha, ToSha = b3.Sha },
                ],
                Lanes =
                [
                    new GraphRef { Name = "main", Kind = BranchKind.Mainline, TargetSha = mainTip.Sha },
                    new GraphRef
                    {
                        Name = "ai/demo/long",
                        Kind = BranchKind.AiWork,
                        TargetSha = b3.Sha,
                        IsOpen = true,
                    },
                ],
            },
            "demo");
        AssertAdjacent(layout, fork.Sha, b1.Sha,
            "open parallel starts immediately after its fork even when timestamps span the whole window");
        AssertAdjacent(layout, b1.Sha, b2.Sha,
            "open parallel stays packed at one node step instead of stretching across the time axis");
        AssertAdjacent(layout, b2.Sha, b3.Sha,
            "open parallel tip stays one node step after the previous branch commit");
        True(Math.Abs(X(layout, b3.Sha) - X(layout, b1.Sha) - 2 * (GraphLayout.NodeWidth + GraphLayout.MinGap)) < 0.001,
            "open lane width follows commit count, not the timestamp span");
    }

    private static void VerifyNonOverlappingBranchesShareNearestLane()
    {
        var root = Commit("root", At(2026, 1, 1));
        var a = Commit("a", At(2026, 1, 2), root.Sha);
        var left = Commit("left", At(2026, 1, 2), root.Sha);
        var m1 = Commit("m1", At(2026, 1, 3), a.Sha, left.Sha);
        var b = Commit("b", At(2026, 1, 4), m1.Sha);
        var right = Commit("right", At(2026, 1, 4), b.Sha);
        var m2 = Commit("m2", At(2026, 1, 5), b.Sha, right.Sha);
        var layout = GraphLayout.Arrange(
            new GraphCommitsReport
            {
                ProjectName = "demo",
                Nodes = [root, a, left, m1, b, right, m2],
                Edges = [],
                Lanes = [new GraphRef { Name = "main", Kind = BranchKind.Mainline, TargetSha = m2.Sha }],
            },
            "demo");
        Equal(1, Lane(layout, left.Sha), "the earlier merged row sits on the lane nearest the mainline");
        Equal(Lane(layout, left.Sha), Lane(layout, right.Sha),
            "a later merged row reuses the nearest lane when its X span is free");
        Equal(0, Lane(layout, m1.Sha), "merge commits stay on the mainline");
    }

    private static void VerifyOverlappingBranchesStackFromMainline()
    {
        var root = Commit("root", At(2026, 1, 1));
        var p = Commit("p", At(2026, 1, 2), root.Sha);
        var a1 = Commit("a1", At(2026, 1, 2), p.Sha);
        var a2 = Commit("a2", At(2026, 1, 3), a1.Sha);
        var q = Commit("q", At(2026, 1, 3), p.Sha);
        var b1 = Commit("b1", At(2026, 1, 2), p.Sha);
        var mA = Commit("ma", At(2026, 1, 4), q.Sha, a2.Sha);
        var mB = Commit("mb", At(2026, 1, 4), mA.Sha, b1.Sha);
        var layout = GraphLayout.Arrange(
            new GraphCommitsReport
            {
                ProjectName = "demo",
                Nodes = [root, p, a1, a2, q, b1, mA, mB],
                Edges = [],
                Lanes = [new GraphRef { Name = "main", Kind = BranchKind.Mainline, TargetSha = mB.Sha }],
            },
            "demo");
        Equal(1, Lane(layout, a1.Sha), "the first occupying branch keeps the lane nearest the mainline");
        True(Lane(layout, b1.Sha) > Lane(layout, a1.Sha),
            "an overlapping branch moves down instead of leaving a hole under the mainline");
    }

    private static GraphLayout.Result ArrangeClosed(
        GraphCommitNode[] nodes,
        string mergeSha,
        string tipSha)
        => GraphLayout.Arrange(
            new GraphCommitsReport
            {
                ProjectName = "demo",
                Nodes = [..nodes],
                Edges = [],
                Lanes =
                [
                    new GraphRef { Name = "main", Kind = BranchKind.Mainline, TargetSha = mergeSha },
                    new GraphRef
                    {
                        Name = "merged/long",
                        Kind = BranchKind.Parallel,
                        TargetSha = tipSha,
                        IsOpen = false,
                    },
                ],
            },
            "demo");

    private static GraphCommitNode Commit(
        string sha,
        DateTimeOffset at,
        string? parent = null,
        string? mergeParent = null,
        string[]? BranchRefs = null)
        => new()
        {
            Sha = sha,
            Parents = mergeParent == null
                ? parent == null ? [] : [parent]
                : [parent!, mergeParent],
            CommittedAt = at,
            BranchRefs = [..(BranchRefs ?? [])],
        };

    private static DateTimeOffset At(int year, int month, int day)
        => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    private static void AssertAdjacent(GraphLayout.Result layout, string leftSha, string rightSha, string name)
    {
        var expected = X(layout, leftSha) + GraphLayout.NodeWidth + GraphLayout.MinGap;
        True(Math.Abs(X(layout, rightSha) - expected) < 0.001, name);
    }

    private static double X(GraphLayout.Result layout, string sha)
        => layout.Nodes.Single(item => item.Node.Sha == sha).X;

    private static int Lane(GraphLayout.Result layout, string sha)
        => layout.Nodes.Single(item => item.Node.Sha == sha).LaneIndex;

    private static string Sha(GitResult result)
    {
        Ensure(result, "resolve sha");
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
    }
}
