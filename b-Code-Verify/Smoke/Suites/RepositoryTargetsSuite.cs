using System.Xml.Linq;
using HistoryVulcan.Core.Commands;
using HistoryJanus.Git;
using HistoryJanus.Views;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>父仓库、子模块与批量提交推送范围。</summary>
internal static class RepositoryTargetsSuite
{
    private const string parentBranch = "2026-232-Parent";
    private const string noChildBranch = "2026-233-NoChild";
    private const string childBranch = "child-main";
    private const string childRelative = "child repo 中文";

    public static async Task RunAsync(string[] args)
    {
        var root = TemporaryDirectory("repository-targets");
        var parentRemote = Path.Combine(root, "parent-remote.git");
        var noChildRemote = Path.Combine(root, "nochild-remote.git");
        var parent = Path.Combine(root, parentBranch);
        var noChild = Path.Combine(root, noChildBranch);
        var child = Path.Combine(parent, childRelative);
        var childRemote = Path.Combine(root, "child-remote.git");
        Directory.CreateDirectory(root);

        try
        {
            await InitStandaloneRepo(parent, "Repository Targets Smoke", "repository-targets@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(parent, "README.md"), "seed\n");
            Ensure(await GitRunner.RunAsync(parent, ["add", "."]), "add parent seed");
            Ensure(await GitRunner.RunAsync(parent, ["commit", "-m", "seed"]), "commit parent seed");
            Ensure(await GitRunner.RunAsync(root, ["clone", "--bare", parent, parentRemote]), "clone parent remote");
            Ensure(await GitRunner.RunAsync(parent, ["remote", "add", "origin", parentRemote]), "set parent origin");
            Ensure(await GitRunner.RunAsync(parent, ["push", "-u", "origin", "HEAD"]), "push parent baseline");

            await InitStandaloneRepo(noChild, "Repository Targets Smoke", "repository-targets@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(noChild, "README.md"), "seed\n");
            Ensure(await GitRunner.RunAsync(noChild, ["add", "."]), "add no-child seed");
            Ensure(await GitRunner.RunAsync(noChild, ["commit", "-m", "seed"]), "commit no-child seed");
            Ensure(await GitRunner.RunAsync(root, ["clone", "--bare", noChild, noChildRemote]), "clone no-child remote");
            Ensure(await GitRunner.RunAsync(noChild, ["remote", "add", "origin", noChildRemote]), "set no-child origin");
            Ensure(await GitRunner.RunAsync(noChild, ["push", "-u", "origin", "HEAD"]), "push no-child baseline");

            await CreateChild(child, childBranch, childRemote);
            Ensure(await GitRunner.RunAsync(parent, ["add", "--", childRelative]), "stage gitlink");
            Ensure(await GitRunner.RunAsync(parent, ["commit", "-m", "register child"]), "commit gitlink");
            Ensure(await GitRunner.RunAsync(parent, ["push", "origin", "HEAD"]), "push parent with gitlink");

            var settings = new MemorySettings();
            BindLibrary(settings, root, parentBranch);
            var service = new ProjectService(settings, _ => true, root);

            await File.AppendAllTextAsync(Path.Combine(child, "tracked.txt"), "child-only change\n");
            var parentBeforeChildOnly = Sha(parent);
            var parentRemoteBeforeChildOnly = RefSha(parentRemote, "main");
            var childOnly = await service.CommitAsync(parentBranch, "统一描述", null,
                RepositoryTarget.Submodules);
            True(childOnly.Outcome == CommitOutcome.Success && !childOnly.ParentExecuted
                 && childOnly.ParentPointerPending, $"child-only commit report: {childOnly.Message}");
            Equal(parentBeforeChildOnly, Sha(parent), "child-only commit leaves parent HEAD unchanged");
            Equal("统一描述", Subject(child), "single message is reused by child");
            True(GitlinkSha(parent, childRelative) != Sha(child), "parent gitlink intentionally pending");

            var childOnlyPush = await service.PushAsync(parentBranch, RepositoryTarget.Submodules);
            True(childOnlyPush.Success && !childOnlyPush.ParentPushed && childOnlyPush.ParentPointerPending,
                $"child-only push succeeds with pending pointer: {childOnlyPush.Message}");
            Equal(Sha(child), RefSha(childRemote, childBranch), "child-only push updates child remote");
            Equal(parentRemoteBeforeChildOnly, RefSha(parentRemote, "main"),
                "child-only push leaves parent remote unchanged");

            var closePointer = await service.CommitAsync(parentBranch, "收口父指针", null, RepositoryTarget.Both);
            True(closePointer.Outcome == CommitOutcome.Success && closePointer.ParentExecuted
                 && !closePointer.ParentPointerPending, $"both commit closes pointer: {closePointer.Message}");
            Equal(Sha(child), GitlinkSha(parent, childRelative), "both commit records child HEAD");
            var bothPush = await service.PushAsync(parentBranch, RepositoryTarget.Both);
            True(bothPush.Success && bothPush.ParentPushed, "both push updates parent after child");
            Equal(Sha(parent), RefSha(parentRemote, "main"), "both push updates parent remote");

            await File.AppendAllTextAsync(Path.Combine(child, "tracked.txt"), "batch child-only\n");
            var parentBeforeBatchChildren = Sha(parent);
            var noChildBeforeBatch = Sha(noChild);
            var parentRemoteBeforeBatch = RefSha(parentRemote, "main");
            var allChildren = await service.CommitAllAsync(
                "全部子模块描述", null, null, RepositoryTarget.Submodules);
            True(allChildren.Success && allChildren.ParentPointerPendingCount == 1,
                $"all submodules commit: {allChildren.Message}");
            Equal(parentBeforeBatchChildren, Sha(parent), "all submodules does not commit parent");
            Equal(noChildBeforeBatch, Sha(noChild), "all submodules does not commit no-child branch");
            var allChildrenPush = await service.PushAllAsync(RepositoryTarget.Submodules);
            True(allChildrenPush.Success && !allChildrenPush.ParentPushed
                 && allChildrenPush.ParentPointerPendingCount == 1,
                $"all submodules push: {allChildrenPush.Message}");
            Equal(parentRemoteBeforeBatch, RefSha(parentRemote, "main"),
                "all submodules push leaves all parent refs unchanged");

            var allBoth = await service.CommitAllAsync("全部完整收口", null, null, RepositoryTarget.Both);
            True(allBoth.Success && allBoth.ParentPointerPendingCount == 0,
                $"all both commit closes all pointers: {allBoth.Message}");
            var allBothPush = await service.PushAllAsync(RepositoryTarget.Both);
            True(allBothPush.Success && allBothPush.ParentPushed, "all both pushes bare parent repo");
            Equal(Sha(parent), RefSha(parentRemote, "main"), "all both updates parent remote");

            var noChildren = await service.CommitAsync(
                noChildBranch, "no child", null, RepositoryTarget.Submodules);
            True(noChildren.Outcome == CommitOutcome.Skipped && !noChildren.ParentExecuted,
                "submodules target skips project without children");
            var noChildrenPush = await service.PushAsync(noChildBranch, RepositoryTarget.Submodules);
            True(noChildrenPush.Success && !noChildrenPush.ParentPushed,
                "submodules push skips project without children");

            await File.AppendAllTextAsync(Path.Combine(child, "tracked.txt"), "legacy parent-only\n");
            var legacyChildHead = Sha(child);
            var legacyParentOnly = await service.CommitAsync(parentBranch, "legacy false", null,
                includeSubmodules: false);
            Equal(CommitOutcome.Skipped, legacyParentOnly.Outcome, "legacy false remains parent-only");
            Equal(legacyChildHead, Sha(child), "legacy false does not commit child");
            Ensure(await GitRunner.RunAsync(child, ["reset", "--hard", "HEAD"]), "clean legacy false change");
            await File.AppendAllTextAsync(Path.Combine(child, "tracked.txt"), "legacy both\n");
            var legacyBoth = await service.CommitAsync(parentBranch, "legacy parent", null,
                includeSubmodules: true, submoduleMessage: "legacy child");
            Equal(CommitOutcome.Success, legacyBoth.Outcome, "legacy true remains both");
            Equal("legacy child", Subject(child), "legacy submsg remains supported");

            Equal(RepositoryTarget.Parent,
                ProjectCommands.ResolveRepositoryTarget(null, false), "resolver default parent");
            Equal(RepositoryTarget.Both,
                ProjectCommands.ResolveRepositoryTarget(null, true), "resolver legacy true both");
            Equal(RepositoryTarget.Submodules,
                ProjectCommands.ResolveRepositoryTarget("submodules", false), "target overrides false");
            Equal(RepositoryTarget.Parent,
                ProjectCommands.ResolveRepositoryTarget("parent", true), "target overrides true");

            Equal("janus.proj.commit name=Demo msg=说明 target=submodules",
                ProjectOperationCommandBuilder.BuildCommit(
                    ProjectOperationMode.CurrentSubmodules, "Demo", "说明"), "current child command");
            Equal("janus.proj.commit name=Demo msg=说明 target=both",
                ProjectOperationCommandBuilder.BuildCommit(
                    ProjectOperationMode.CurrentBoth, "Demo", "说明"), "current both command");
            Equal("janus.proj.commitall msg=说明 target=submodules",
                ProjectOperationCommandBuilder.BuildCommit(
                    ProjectOperationMode.AllSubmodules, null, "说明"), "all child command");
            Equal("janus.proj.pushall target=both",
                ProjectOperationCommandBuilder.BuildPush(ProjectOperationMode.AllBoth, null), "all both push command");

            var registry = new CommandRegistry();
            ProjectCommands.RegisterAll(registry, service, null!);
            foreach (var commandName in new[] { "janus.proj.commit", "janus.proj.push", "janus.proj.commitall", "janus.proj.pushall" })
            {
                True(registry.TryGet(commandName, out var descriptor), $"{commandName} registered");
                var target = descriptor.Parameters.Single(parameter => parameter.Name == "target");
                True(target.Default == null && target.AllowedValues is ["parent", "submodules", "both"],
                    $"{commandName} target enum schema");
            }
            True(registry.TryGet("janus.proj.metas", out var metaListDescriptor) && metaListDescriptor.Readonly,
                "janus.proj.metas stays a readonly module command");
            True(registry.TryGet("janus.proj.metaopen", out var metaOpenDescriptor)
                 && metaOpenDescriptor.Parameters.Any(parameter => parameter.Name == "name")
                 && metaOpenDescriptor.Parameters.Any(parameter => parameter.Name == "meta"),
                "janus.proj.metaopen keeps its name/meta parameters");

            await VerifyOverviewMetaMerge(service, parent, noChild);
            VerifyXamlLayout();
        }
        finally
        {
            if (Directory.Exists(root))
                DeleteTree(root);
        }
    }

    private static void VerifyXamlLayout()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var projectPath = Path.Combine(RepoRoot,
            "Views", "ProjectOperationsView.xaml");
        var overviewPath = Path.Combine(RepoRoot,
            "Views", "OverviewView.xaml");
        var project = XDocument.Load(projectPath);
        var overview = XDocument.Load(overviewPath);
        var named = project.Descendants()
            .Select(element => element.Attribute(x + "Name")?.Value)
            .Where(name => name != null).ToList();
        Equal(1, named.Count(name => name == "SelectedCommitMessageBox"), "one commit message box");
        foreach (var removed in new[]
                 {
                     "OpenProjectButton", "ScanCoverageButton", "ReloadRulesButton",
                     "ShowGapsButton", "SuggestButton", "SaveRuleButton",
                     "CurrentProjectBox", "RefreshProjectsButton",
                 })
            True(!named.Contains(removed), $"removed project operation control is absent: {removed}");
        Equal(4, project.Descendants().Count(element => element.Name.LocalName == "RadioButton"
            && element.Attribute("GroupName")?.Value == "OperationMode"), "four operation segments");
        var bottomSegments = project.Descendants().Where(element =>
                element.Name.LocalName == "RadioButton"
                && element.Attribute("GroupName")?.Value == "BottomPage").ToArray();
        // DEC-008：GitHub 并入同一行分段，宿主不再注册独立 github 窗口。
        Equal(3, bottomSegments.Length, "bottom switcher has three same-level segments");
        True(bottomSegments.Select(segment => segment.Attribute("Content")?.Value)
                .SequenceEqual(["Git 文件规则", "分支历史", "GitHub"]),
            "bottom switcher toggles Git file rules, embedded branch history and GitHub");
        Equal("True", bottomSegments[0].Attribute("IsChecked")?.Value,
            "bottom switcher defaults to the Git file rules page");
        True(bottomSegments.All(segment => segment.Attribute("MinHeight")?.Value == "26"),
            "bottom switcher stays a slim bar instead of reusing the 46px segment height");
        foreach (var panelName in new[] { "HistoryPanel", "GitHubPanel" })
        {
            var panel = project.Descendants().Single(element =>
                element.Attribute(x + "Name")?.Value == panelName);
            Equal("ContentControl", panel.Name.LocalName,
                $"{panelName} is a content host for the embedded component");
            Equal("Collapsed", panel.Attribute("Visibility")?.Value,
                $"{panelName} starts collapsed behind the rules page");
        }
        True(project.Descendants().Any(element =>
                element.Attribute(x + "Name")?.Value == "RulePanel"),
            "rules panel stays as the default bottom page");
        // DEC-008：分段按钮与选中项标题已给出面板名和当前项目，规则面板不再重复标题行。
        True(!project.Descendants().Any(element =>
                element.Attribute(x + "Name")?.Value == "RuleTitle"),
            "rules panel carries no redundant title row");
        True(!overview.ToString().Contains("CommitAll", StringComparison.Ordinal)
             && !overview.ToString().Contains("PushAll", StringComparison.Ordinal)
             && !overview.ToString().Contains("Submodule", StringComparison.OrdinalIgnoreCase),
            "overview contains no git write controls");
        var overviewColumns = overview.Descendants()
            .Where(element => element.Name.LocalName == "GridViewColumn")
            .ToArray();
        Equal(5, overviewColumns.Length, "overview is a five-column navigator");
        True(overviewColumns.Select(column => column.Attribute("Header")?.Value)
                .SequenceEqual(["#", "工作树", "分支 / 项目", "元文件夹", "最近提交"]),
            "overview columns are number, worktree status, branch/project, meta folders, then latest commit");
        True(overview.ToString().Contains("CleanStatusGlyph", StringComparison.Ordinal)
             && overview.ToString().Contains("CleanStatusToolTip", StringComparison.Ordinal),
            "overview worktree column projects the clean/dirty glyph and diagnostic tooltip");
        var overviewCode = File.ReadAllText(Path.Combine(RepoRoot, "Views", "OverviewView.xaml.cs"));
        Contains(overviewCode, "janus.proj.list status=true",
            "overview explicitly requests worktree status without taxing other project-list consumers");
        var overviewMarkup = overview.ToString();
        foreach (var token in new[]
                 {
                     "PrimaryMetaName", "HasMeta", "HasAdditionalMeta",
                     "MoreMetaLabel", "OnMetaFolderClick", "OnMoreMetaClick",
                 })
        {
            True(overviewMarkup.Contains(token, StringComparison.Ordinal),
                $"overview meta column projects the merged contract: {token}");
        }
        var overviewSource = overview.ToString();
        True(!overviewSource.Contains("LastCommitTime", StringComparison.Ordinal)
             && !overviewSource.Contains("WorktreePath", StringComparison.Ordinal)
             && !overviewSource.Contains("OnOpenRootClick", StringComparison.Ordinal),
            "overview removes raw commit time, path and root-open entry");
        True(overviewMarkup.Contains("CommitDisplay", StringComparison.Ordinal)
             || overviewMarkup.Contains("LastCommitMessage", StringComparison.Ordinal),
            "overview shows the latest commit tip");
        foreach (var retained in new[] { "SearchBox", "RefreshButton", "WorktreeList" })
        {
            True(overview.Descendants().Any(element =>
                    element.Attribute(x + "Name")?.Value == retained),
                $"overview retains compact navigation control: {retained}");
        }
        True(overviewMarkup.Contains("ItemContainerStyle", StringComparison.Ordinal),
            "overview compresses row height with an item container style");
        True(overviewMarkup.Contains("GridViewRowPresenter", StringComparison.Ordinal)
             && overviewMarkup.Contains("Shell.Brush.AccentSoft", StringComparison.Ordinal)
             && overviewMarkup.Contains("Shell.Brush.SurfaceHover", StringComparison.Ordinal),
            "overview row template paints selection with the host AccentSoft token");
        True(!overview.Descendants().Any(element => element.Attribute(x + "Name")?.Value == "StatusText"),
            "overview drops the bottom status strip");

        var githubPath = Path.Combine(RepoRoot, "Views", "GitHubConnectionView.xaml");
        var github = XDocument.Load(githubPath);
        var githubMarkup = github.ToString();
        True(!github.Descendants().Any(element => element.Name.LocalName == "TabControl"),
            "github page is a single flat form without tabs");
        True(!github.Descendants().Any(element => element.Name.LocalName == "DataGrid"),
            "github page uses selectors instead of DataGrids");
        True(github.Descendants().Any(element =>
                element.Attribute(x + "Name")?.Value == "AccountsBox"),
            "github page keeps an account ComboBox");
        True(github.Descendants().Any(element =>
                element.Attribute(x + "Name")?.Value == "KeysBox"),
            "github page keeps an SSH key ComboBox");
        True(githubMarkup.Contains("OnDiagnosticsClick", StringComparison.Ordinal),
            "github diagnostics open via a popup action");
        True(!githubMarkup.Contains("TargetType=\"Button\"", StringComparison.Ordinal),
            "github page does not override the host implicit Button style");
        True(!github.Descendants().Any(element => element.Attribute(x + "Name")?.Value == "StatusText"),
            "github page drops the bottom status strip like the other pages");

        True(!named.Contains("StatusText"),
            "project page drops the bottom status strip");
        True(!project.Descendants().Any(element => element.Name.LocalName == "GroupBox"),
            "project page drops the group-box frames for density");
        True(!project.ToString().Contains("基础分支", StringComparison.Ordinal)
             && !project.ToString().Contains("打开所选项目", StringComparison.Ordinal),
            "project page removes the base-branch label and duplicate open entry");

        var byName = project.Descendants()
            .Where(element => element.Attribute(x + "Name") != null)
            .ToDictionary(element => element.Attribute(x + "Name")!.Value);
        Equal("2", byName["CreateProjectButton"].Attribute("Grid.Column")?.Value,
            "new project action sits to the right of its input");
        Equal("1", byName["SelectedCommitMessageBox"].Attribute("Grid.Row")?.Value,
            "commit description remains on the action row");
        var commitActions = byName["SelectedCommitButton"].Parent;
        True(commitActions != null && ReferenceEquals(commitActions, byName["SelectedPushButton"].Parent)
             && commitActions.Attribute("Grid.Row")?.Value == "1"
             && commitActions.Attribute("Grid.Column")?.Value == "2",
            "commit and push buttons share the description row immediately to its right");

        True(ReferenceEquals(byName["PatternBox"].Parent, byName["AddRuleButton"].Parent)
             && byName["AddRuleButton"].Parent?.Name.LocalName == "Grid"
             && byName["AddRuleButton"].Attribute("Grid.Column")?.Value == "1",
            "add rule action sits on the extension input row");
        var actionNames = new[]
        {
            "RefreshRulesButton", "ReviewRulesButton", "SyncBaselineButton", "DeleteRuleButton",
        };
        var actionStrip = byName[actionNames[0]].Parent;
        True(actionStrip != null && actionStrip.Name.LocalName == "StackPanel"
             && actionNames.All(name => ReferenceEquals(actionStrip, byName[name].Parent))
             && actionStrip.Parent?.Name.LocalName == "ScrollViewer",
            "the remaining Git rule actions stay in one horizontally scrollable strip");

        var rulesCode = File.ReadAllText(Path.Combine(
            RepoRoot, "Views", "ProjectOperationsView.Rules.cs"));
        True(!rulesCode.Contains("DispatcherTimer", StringComparison.Ordinal)
             && !rulesCode.Contains("ScheduleRuleAutoSave", StringComparison.Ordinal)
             && rulesCode.Contains("RulePanel.IsVisibleChanged", StringComparison.Ordinal)
             && rulesCode.Contains("SaveRulesOnPageLeaveAsync", StringComparison.Ordinal)
             && rulesCode.Contains("_ruleSaveTask", StringComparison.Ordinal),
            "rule edits defer one serialized batch save until the rules page is left");
        True(!rulesCode.Contains("OnSaveRuleClick", StringComparison.Ordinal)
             && !rulesCode.Contains("MessageBox.Show", StringComparison.Ordinal),
            "manual save entry and unsaved-change dialog are removed");
    }

    private static async Task VerifyOverviewMetaMerge(ProjectService service, string parent, string noChild)
    {
        Directory.CreateDirectory(Path.Combine(parent, "z-alpha"));
        var secondMeta = Path.Combine(parent, "z-Beta");
        Directory.CreateDirectory(secondMeta);
        Directory.CreateDirectory(Path.Combine(parent, "docs"));

        var (metaGit, metas, metaWarnings) = await service.ListMetaFoldersAsync();
        True(metaGit.Success && metaWarnings.Count == 0, "meta scan succeeds without warnings");
        var parentMetas = metas.Where(meta => meta.ProjectName == parentBranch).ToList();
        Equal(2, parentMetas.Count, "only z/Z-level folders are listed as meta");
        True(parentMetas.Select(meta => meta.MetaName).SequenceEqual(new[] { "z-alpha", "z-Beta" }),
            "meta folders sort by case-insensitive name");
        True(metas.All(meta => meta.ProjectName != noChildBranch),
            "project without z-level folder contributes no meta entries");

        var (listGit, worktrees) = await service.ListWorktreesAsync();
        True(listGit.Success, "worktree list feeds the overview merge");
        var cleanRows = await service.ReadWorktreeStatusesAsync(
            [worktrees.Single(row => row.BranchName == noChildBranch)]);
        True(cleanRows.Single().IsClean == true, "overview status marks a clean worktree with a check");

        await File.AppendAllTextAsync(Path.Combine(noChild, "README.md"), "tracked change\n");
        var trackedDirtyRows = await service.ReadWorktreeStatusesAsync(cleanRows);
        True(trackedDirtyRows.Single().IsClean == false,
            "overview status marks a tracked modification with a cross");
        Ensure(await GitRunner.RunAsync(noChild, ["restore", "--", "README.md"]),
            "restore overview status fixture");

        var untrackedPath = Path.Combine(noChild, "untracked-status.tmp");
        await File.WriteAllTextAsync(untrackedPath, "untracked\n");
        var untrackedDirtyRows = await service.ReadWorktreeStatusesAsync(cleanRows);
        True(untrackedDirtyRows.Single().IsClean == false,
            "overview status includes untracked files in the dirty state");
        File.Delete(untrackedPath);
        var restoredRows = await service.ReadWorktreeStatusesAsync(cleanRows);
        True(restoredRows.Single().IsClean == true,
            "overview status returns to clean after tracked and untracked changes are removed");
        var unknownRows = await service.ReadWorktreeStatusesAsync(
            [new WorktreeInfo("missing", Path.Combine(Path.GetDirectoryName(noChild)!, "missing-worktree"))]);
        True(unknownRows.Single().IsClean == null,
            "overview status keeps a missing worktree unknown instead of guessing clean or dirty");

        var mergedRows = OverviewMetaMerge.Merge(worktrees, metas);
        var parentRow = mergedRows.Single(row => row.BranchName == parentBranch);
        var noChildRow = mergedRows.Single(row => row.BranchName == noChildBranch);
        Equal("z-alpha", parentRow.PrimaryMetaName, "primary meta is the first by name");
        True(parentRow.HasMeta && parentRow.HasAdditionalMeta && parentRow.MoreMetaLabel == "+1",
            "multiple metas collapse to the primary with a +N badge");
        True(!noChildRow.HasMeta && !noChildRow.HasAdditionalMeta
             && noChildRow.PrimaryMetaName == "-" && noChildRow.MoreMetaLabel.Length == 0,
            "project without meta shows the placeholder");
        var cleanOverviewRow = OverviewMetaMerge.Merge(restoredRows, []).Single();
        Equal("✓", cleanOverviewRow.CleanStatusGlyph,
            "overview row projects the clean worktree check glyph");
        var dirtyOverviewRow = OverviewMetaMerge.Merge(untrackedDirtyRows, []).Single();
        Equal("×", dirtyOverviewRow.CleanStatusGlyph,
            "overview row projects the dirty worktree cross glyph");
        var unknownOverviewRow = OverviewMetaMerge.Merge(unknownRows, []).Single();
        Equal("?", unknownOverviewRow.CleanStatusGlyph,
            "overview row projects the unknown worktree question glyph");
        var singleRow = OverviewMetaMerge.Merge(
                worktrees, metas.Where(meta => meta.MetaName == "z-alpha").ToList())
            .Single(row => row.BranchName == parentBranch);
        True(singleRow.HasMeta && !singleRow.HasAdditionalMeta && singleRow.MoreMetaLabel.Length == 0,
            "a single meta needs no overflow badge");

        True(OverviewMetaMerge.MatchesKeyword(parentRow, "beta"), "search matches the meta name");
        True(OverviewMetaMerge.MatchesKeyword(parentRow, "232"), "search matches the project name");
        True(OverviewMetaMerge.MatchesKeyword(parentRow, parentRow.MetaFolders[1].FullPath),
            "search matches the meta full path");
        True(!OverviewMetaMerge.MatchesKeyword(noChildRow, "beta"),
            "search does not match rows missing the keyword");

        True(OverviewMetaMerge.BuildOpenCommand(parentRow.PrimaryMeta!)
                .StartsWith("janus.proj.metaopen path=", StringComparison.Ordinal),
            "meta click opens by registered FullPath, not WorktreeRoot+branch");
        True(OverviewMetaMerge.BuildOpenCommand(parentRow.PrimaryMeta!)
                .Contains(parentRow.PrimaryMeta!.FullPath, StringComparison.OrdinalIgnoreCase),
            "meta open command embeds the scanned FullPath");
    }

    private static async Task CreateChild(string path, string branch, string remote)
    {
        Ensure(await GitRunner.RunAsync(Path.GetDirectoryName(path)!, ["init", "-b", branch, path]),
            "init child");
        await ConfigureIdentity(path);
        await File.WriteAllTextAsync(Path.Combine(path, "tracked.txt"), "tracked\n");
        Ensure(await GitRunner.RunAsync(path, ["add", "."]), "add child");
        Ensure(await GitRunner.RunAsync(path, ["commit", "-m", "child seed"]), "commit child");
        Ensure(await GitRunner.RunAsync(Path.GetDirectoryName(remote)!, ["init", "--bare", remote]),
            "init child remote");
        Ensure(await GitRunner.RunAsync(path, ["remote", "add", "origin", remote]), "add child origin");
        Ensure(await GitRunner.RunAsync(path, ["push", "origin", branch]), "push child baseline");
    }

    private static Task ConfigureIdentity(string repository)
        => SmokeKit.ConfigureIdentity(repository, "Repository Targets Smoke", "repository-targets@example.invalid");
}
