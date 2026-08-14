using HistoryVulcan.Core;
using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Core.Commands;
using System.Xml.Linq;
using HistoryJanus.Git;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>分支历史、恢复提交、本地重置与强推保护。</summary>
internal static class BranchHistorySuite
{
    private const string baseBranch = "0000-000-Template";
    private const string parentBranch = "2026-001-Parent";
    private const string childBranch = "2026-002-Child";

    public static async Task RunAsync(string[] args)
    {
        VerifyHistoryLayout();
        var root = TemporaryDirectory("branch-history");
        var remote = Path.Combine(root, "remote.git");
        var template = Path.Combine(root, baseBranch);
        var parent = Path.Combine(root, parentBranch);
        var child = Path.Combine(root, childBranch);
        Directory.CreateDirectory(root);

        try
        {
            await InitStandaloneRepo(template, "Branch History Smoke", "branch-history@example.invalid");
            await WriteProjectManifest(template, "", isTemplate: true);
            await File.WriteAllTextAsync(Path.Combine(template, "README.md"), "template\n");
            Ensure(await GitRunner.RunAsync(template, ["add", "."]), "add template");
            Ensure(await GitRunner.RunAsync(template, ["commit", "-m", "template root"]), "commit template");

            await InitStandaloneRepo(parent, "Branch History Smoke", "branch-history@example.invalid");
            await WriteProjectManifest(parent, baseBranch);
            await File.WriteAllTextAsync(Path.Combine(parent, "README.md"), "parent\n");
            Ensure(await GitRunner.RunAsync(parent, ["add", "."]), "add parent");
            Ensure(await GitRunner.RunAsync(parent, ["commit", "-m", "parent one"]), "commit parent one");
            await CommitFile(parent, "parent.txt", "parent two\n", "parent two");

            await InitStandaloneRepo(child, "Branch History Smoke", "branch-history@example.invalid");
            await WriteProjectManifest(child, parentBranch);
            await File.WriteAllTextAsync(Path.Combine(child, "README.md"), "child\n");
            Ensure(await GitRunner.RunAsync(child, ["add", "."]), "add child seed");
            Ensure(await GitRunner.RunAsync(child, ["commit", "-m", "child seed"]), "commit child seed");
            var forkSha = Sha(await GitRunner.RunAsync(child, ["rev-parse", "HEAD"]));
            await CommitFile(child, "child.txt", "child one\n", "child one");
            var childOneSha = Sha(await GitRunner.RunAsync(child, ["rev-parse", "HEAD"]));
            await CommitFile(child, "child.txt", "child two\n", "child two");
            var childTwoSha = Sha(await GitRunner.RunAsync(child, ["rev-parse", "HEAD"]));

            Ensure(await GitRunner.RunAsync(root, ["clone", "--bare", child, remote]), "clone child remote");
            Ensure(await GitRunner.RunAsync(child, ["remote", "add", "origin", remote]), "set origin");
            Ensure(await GitRunner.RunAsync(child, ["push", "-u", "origin", "HEAD"]), "initial push");
            await CommitFile(child, "child.txt", "child local three\n", "child local three");
            var childThreeSha = Sha(await GitRunner.RunAsync(child, ["rev-parse", "HEAD"]));

            var settings = new MemorySettings();
            BindLibrary(settings, root, baseBranch);
            settings.Set(ProjectService.KeyProtected, baseBranch);
            var projects = new ProjectService(settings, _ => true, root);
            var service = new BranchHistoryService(projects);

            var history = await service.GetHistoryAsync(childBranch, refreshTree: true, refreshRemote: true);
            True(history.Success && history.Report != null, $"history loaded: {history.Message}");
            var report = history.Report!;
            Equal(parentBranch, report.ParentBranch, "nested branch parent detected");
            Equal(forkSha, report.ForkSha, "merge-base fork detected");
            Equal(childThreeSha, report.HeadSha, "head detected");
            Equal(3, report.TotalOwnCommits, "own commit count");
            True(report.Entries[0].IsForkPoint, "fork is first baseline row");
            Equal(forkSha, report.Entries[0].Sha, "fork baseline sha");
            Equal(childOneSha, report.Entries[1].Sha, "history chronological first own commit");
            Equal(childThreeSha, report.Entries[^1].Sha, "history chronological head");
            Equal(CommitRemoteState.Pushed, report.Entries[1].RemoteState, "pushed commit marked");
            Equal(CommitRemoteState.LocalOnly, report.Entries[^1].RemoteState, "local commit marked");
            Equal(BranchRemoteState.Ahead, report.RemoteState, "branch ahead state");

            var newestPage = await service.GetHistoryAsync(childBranch, limit: 1, skip: 0);
            var olderPage = await service.GetHistoryAsync(childBranch, limit: 1, skip: 1);
            True(newestPage.Success && newestPage.Report is { HasMore: true, Entries.Count: 2 }
                  && newestPage.Report.Entries[^1].Sha == childThreeSha,
                "first page contains baseline plus newest commit");
            True(olderPage.Success && olderPage.Report is { HasMore: true, Entries.Count: 2 }
                  && olderPage.Report.Entries[^1].Sha == childTwoSha,
                "skip loads the next older page without losing baseline");

            var detail = await service.GetCommitAsync(childBranch, childOneSha);
            True(detail.Success && detail.Detail?.Files.Any(file => file.Path == "child.txt") == true,
                "commit detail contains changed file");
            var diff = await service.GetDiffAsync(childBranch, forkSha);
            True(diff.Success && diff.Report is { CommitCount: 3, FileCount: > 0 },
                "fork-to-head diff counts commits and files");

            await File.WriteAllTextAsync(Path.Combine(child, "dirty.tmp"), "dirty");
            var dirtyRollback = await service.RollbackAsync(childBranch, childOneSha, "must reject dirty");
            True(!dirtyRollback.Success && dirtyRollback.Message.Contains("未跟踪"),
                "dirty worktree rejected before rollback");
            File.Delete(Path.Combine(child, "dirty.tmp"));

            var emptyMessage = await service.RollbackAsync(childBranch, childOneSha, "   ");
            True(!emptyMessage.Success && emptyMessage.Message.Contains("不能为空"),
                "blank rollback message rejected");
            var changedAfterConfirmation = await service.RollbackAsync(
                childBranch, childOneSha, "stale confirmation", expectedHead: new string('0', 40));
            True(!changedAfterConfirmation.Success && changedAfterConfirmation.Message.Contains("HEAD 已变化"),
                "rollback rejects a head changed after confirmation");

            var rollback = await service.RollbackAsync(childBranch, childOneSha, "restore child one");
            True(rollback.Success && rollback.Changed && rollback.AfterSha != childThreeSha,
                $"rollback commit created: {rollback.Message}");
            Equal("child one\n", (await File.ReadAllTextAsync(Path.Combine(child, "child.txt")))
                    .Replace("\r\n", "\n", StringComparison.Ordinal),
                "rollback restored target tree content");
            var rollbackParents = await GitRunner.RunAsync(child, ["rev-list", "--first-parent", "--count",
                $"{childThreeSha}..HEAD"]);
            Ensure(rollbackParents, "count recovery commits");
            Equal("1", rollbackParents.Output.Trim(), "rollback appended one commit");

            var protectedReset = await service.ResetAsync(baseBranch, forkSha);
            True(!protectedReset.Success && protectedReset.Message.Contains("受保护"),
                "protected branch hard reset rejected");
            var reset = await service.ResetAsync(childBranch, childThreeSha);
            True(reset.Success && reset.Changed, $"local hard reset: {reset.Message}");
            Equal(childThreeSha, Sha(await GitRunner.RunAsync(child, ["rev-parse", "HEAD"])),
                "hard reset moved local head");
            Equal(childTwoSha, Sha(await GitRunner.RunAsync(remote, ["rev-parse", "refs/heads/main"])),
                "hard reset did not touch remote");

            var staleLocalConfirmation = await service.ForcePushAsync(
                childBranch, expectedLocal: new string('0', 40));
            True(!staleLocalConfirmation.Success && staleLocalConfirmation.Message.Contains("本地 HEAD 已变化"),
                "force push rejects local head changed after confirmation");
            var forcePush = await service.ForcePushAsync(childBranch);
            True(forcePush.Success, $"lease force push succeeded: {forcePush.Message}");
            Equal(childThreeSha, Sha(await GitRunner.RunAsync(remote, ["rev-parse", "refs/heads/main"])),
                "force-with-lease updated remote");
            var protectedForcePush = await service.ForcePushAsync(baseBranch);
            True(!protectedForcePush.Success && protectedForcePush.Message.Contains("受保护"),
                "protected branch force push rejected");

            var intruder = Path.Combine(root, "intruder");
            Ensure(await GitRunner.RunAsync(root, ["clone", remote, intruder]), "clone intruder");
            await ConfigureIdentity(intruder, "Branch History Smoke", "branch-history@example.invalid");
            Ensure(await GitRunner.RunAsync(intruder, ["checkout", "main"]), "checkout intruder branch");
            await CommitFile(intruder, "intruder.txt", "remote changed\n", "remote competitor");
            Ensure(await GitRunner.RunAsync(intruder, ["push", "origin", "main"]), "push remote competitor");
            var staleLease = await service.ForcePushAsync(childBranch);
            True(!staleLease.Success && staleLease.Message.Contains("force-with-lease"),
                "stale lease rejects remote overwrite");

            var registry = new CommandRegistry();
            BranchHistoryCommands.RegisterAll(registry, service, null!);
            var commands = registry.All().ToDictionary(command => command.Name, StringComparer.OrdinalIgnoreCase);
            True(commands.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([
                "janus.history.list", "janus.history.show", "janus.history.diff",
                "janus.history.rollback", "janus.history.reset", "janus.history.forcepush",
            ]), "V2.3 command catalog complete");
            // V2.4.4:只读性由描述符自描述,不再查名字白名单。判据升级为「真值 + 解释结果」。
            True(new[] { "janus.history.list", "janus.history.show", "janus.history.diff" }
                    .All(name => commands[name].Readonly
                                 && McpExposurePolicy.State(commands[name]) == "readonly"),
                "history reads are readonly MCP tools");
            True(commands["janus.history.rollback"].ConfirmPrompt != null &&
                 commands["janus.history.reset"].ConfirmPrompt != null &&
                 commands["janus.history.forcepush"].ConfirmPrompt != null, "history writes are dangerous commands");

        }
        finally
        {
            if (Directory.Exists(root))
                DeleteTree(root);
        }
    }

    private static void VerifyHistoryLayout()
    {
        var document = XDocument.Load(Path.Combine(
            RepoRoot, "Views", "BranchHistoryView.xaml"));
        var columns = document.Descendants()
            .Where(element => element.Name.LocalName == "GridViewColumn")
            .ToArray();
        var headers = columns.Select(column => column.Attribute("Header")?.Value).ToArray();
        True(headers.SequenceEqual(["节点", "提交说明", "时间", "作者", "origin"]),
            "history columns omit SHA and place time after subject");
        Equal("360", columns[1].Attribute("Width")?.Value,
            "history subject column uses the expanded width");
        Equal("138", columns[2].Attribute("Width")?.Value,
            "history time column keeps its width");
        var source = document.ToString();
        True(!source.Contains("ShortSha", StringComparison.Ordinal)
             && source.Contains("OnCopyShaClick", StringComparison.Ordinal),
            "history hides the SHA column but retains the copy action");
    }

    /// <summary>读取 GitResult 的首行 SHA。</summary>
    private static string Sha(GitResult result)
    {
        Ensure(result, "resolve sha");
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
    }
}
