using HistoryVulcan.Core.Commands;
using HistoryJanus.Git;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>子模块提交、推送、gitlink 与嵌套仓库安全边界。</summary>
internal static class SubmoduleSafetySuite
{
    private const string parentBranch = "2026-231-Parent";
    private const string standardBranch = "standard-main";
    private const string legacyBranch = "legacy-main";
    private const string standardRelative = "a-standard 子模块";
    private const string legacyRelative = "z-legacy repo";

    public static async Task RunAsync(string[] args)
    {
        var root = TemporaryDirectory("submodule-safety");
        var parentRemote = Path.Combine(root, "parent-remote.git");
        var parent = Path.Combine(root, parentBranch);
        var standard = Path.Combine(parent, standardRelative);
        var legacy = Path.Combine(parent, legacyRelative);
        var standardRemote = Path.Combine(root, "standard-remote.git");
        var legacyRemote = Path.Combine(root, "legacy-remote.git");
        Directory.CreateDirectory(root);

        try
        {
            await InitStandaloneRepo(parent, "Submodule Safety Smoke", "submodule-safety@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(parent, "README.md"), "parent seed\n");
            Ensure(await GitRunner.RunAsync(parent, ["add", "."]), "add parent seed");
            Ensure(await GitRunner.RunAsync(parent, ["commit", "-m", "parent seed"]), "commit parent seed");
            Ensure(await GitRunner.RunAsync(root, ["clone", "--bare", parent, parentRemote]), "clone parent remote");
            Ensure(await GitRunner.RunAsync(parent, ["remote", "add", "origin", parentRemote]), "set parent origin");
            Ensure(await GitRunner.RunAsync(parent, ["push", "-u", "origin", "HEAD"]), "push parent seed");

            await CreateChild(standard, standardBranch, standardRemote);
            await CreateChild(legacy, legacyBranch, legacyRemote);
            await File.WriteAllTextAsync(Path.Combine(parent, ".gitmodules"),
                $"[submodule \"standard child\"]\n\tpath = {standardRelative}\n\turl = {standardRemote.Replace('\\', '/')}\n");
            Ensure(await GitRunner.RunAsync(parent, ["add", ".gitmodules", "--", standardRelative, legacyRelative]),
                "stage initial gitlinks");
            Ensure(await GitRunner.RunAsync(parent, ["commit", "-m", "register gitlinks"]),
                "commit initial gitlinks");
            Ensure(await GitRunner.RunAsync(parent, ["push", "origin", "HEAD"]), "push parent baseline");

            var settings = new MemorySettings();
            BindLibrary(settings, root, parentBranch);
            var prompts = new List<string>();
            var service = new ProjectService(settings, prompt =>
            {
                prompts.Add(prompt);
                return true;
            }, root);
            var gitlinks = new GitlinkService();

            var discovery = await gitlinks.DiscoverAsync(parent);
            True(discovery.Success && discovery.Items.Count == 2, $"discover two gitlinks: {discovery.Message}");
            Equal(GitlinkKind.StandardSubmodule,
                discovery.Items.Single(item => item.RelativePath == standardRelative).Kind,
                "standard .gitmodules kind");
            Equal(GitlinkKind.LegacyGitlink,
                discovery.Items.Single(item => item.RelativePath == legacyRelative).Kind,
                "legacy gitlink kind");
            True(!GitlinkService.ValidateChildPath(parent, "..\\outside").Success, "path escape rejected");

            await File.AppendAllTextAsync(Path.Combine(standard, "tracked.txt"), "modified\n");
            File.Delete(Path.Combine(standard, "delete.txt"));
            await File.WriteAllTextAsync(Path.Combine(standard, "新增 文件.txt"), "untracked\n");
            await File.AppendAllTextAsync(Path.Combine(legacy, "tracked.txt"), "legacy modified\n");
            var commit = await service.CommitAsync(parentBranch, "父提交 中文", null,
                includeSubmodules: true, submoduleMessage: "子提交 中文");
            True(commit.Outcome == CommitOutcome.Success, $"linked commit succeeds: {commit.Message}");
            True(commit.Submodules is { Count: 2 }
                 && commit.Submodules.All(item => item.Outcome == SubmoduleOperationOutcome.Success),
                "both children committed before parent");
            Equal("子提交 中文", Subject(standard), "standard child uses override message");
            Equal("子提交 中文", Subject(legacy), "legacy child uses override message");
            Equal("父提交 中文", Subject(parent), "parent keeps parent message");
            Equal(Sha(standard), GitlinkSha(parent, standardRelative), "parent records standard head");
            Equal(Sha(legacy), GitlinkSha(parent, legacyRelative), "parent records legacy head");
            True(prompts.Any(prompt => prompt.Contains("修改 1", StringComparison.Ordinal)
                                       && prompt.Contains("删除 1", StringComparison.Ordinal)
                                       && prompt.Contains("未跟踪 1", StringComparison.Ordinal)),
                "confirmation includes change counts");

            await File.AppendAllTextAsync(Path.Combine(standard, "tracked.txt"), "old mode dirty\n");
            var oldHead = Sha(standard);
            var parentOnly = await service.CommitAsync(parentBranch, "父仓库兼容", null);
            Equal(CommitOutcome.Skipped, parentOnly.Outcome, "old command remains parent only");
            Equal(oldHead, Sha(standard), "old command does not commit child");
            Ensure(await GitRunner.RunAsync(standard, ["reset", "--hard", "HEAD"]), "clean old-mode child");

            await File.AppendAllTextAsync(Path.Combine(standard, "tracked.txt"), "fallback message\n");
            var fallback = await service.CommitAsync(parentBranch, "父描述回退", null,
                includeSubmodules: true, submoduleMessage: "   ");
            Equal(CommitOutcome.Success, fallback.Outcome, "fallback commit succeeds");
            Equal("父描述回退", Subject(standard), "blank submsg falls back to parent message");

            await File.WriteAllTextAsync(Path.Combine(standard, "dirty.tmp"), "dirty");
            var dirtyPush = await service.PushAsync(parentBranch, includeSubmodules: true);
            True(!dirtyPush.Success && dirtyPush.Message.Contains("工作树不干净", StringComparison.Ordinal),
                "dirty child blocks all pushes");
            File.Delete(Path.Combine(standard, "dirty.tmp"));

            var parentRemoteBefore = RefSha(parentRemote, "main");
            Ensure(await GitRunner.RunAsync(legacy, ["remote", "set-url", "origin", Path.Combine(root, "missing-remote.git")]),
                "break legacy remote");
            var partialPush = await service.PushAsync(parentBranch, includeSubmodules: true);
            True(!partialPush.Success && !partialPush.ParentPushed && partialPush.PartialCompletion,
                "child failure blocks parent and reports partial push");
            Equal(parentRemoteBefore, RefSha(parentRemote, "main"), "parent remote unchanged after child failure");
            Ensure(await GitRunner.RunAsync(legacy, ["remote", "set-url", "origin", legacyRemote]),
                "restore legacy remote");
            var push = await service.PushAsync(parentBranch, includeSubmodules: true);
            True(push.Success && push.ParentPushed, $"children then parent push succeeds: {push.Message}");
            Equal(Sha(standard), RefSha(standardRemote, standardBranch), "standard remote updated");
            Equal(Sha(legacy), RefSha(legacyRemote, legacyBranch), "legacy remote updated");
            Equal(Sha(parent), RefSha(parentRemote, "main"), "parent remote updated last");

            var parentBeforePartialCommit = Sha(parent);
            await File.AppendAllTextAsync(Path.Combine(standard, "tracked.txt"), "partial standard\n");
            await File.AppendAllTextAsync(Path.Combine(legacy, "tracked.txt"), "partial legacy\n");
            var rejectingHook = Path.Combine(legacy, ".git", "hooks", "pre-commit");
            await File.WriteAllTextAsync(rejectingHook, "#!/bin/sh\nexit 1\n");
            var partialCommit = await service.CommitAsync(parentBranch, "partial parent", null,
                includeSubmodules: true, submoduleMessage: "partial children");
            True(partialCommit.Outcome == CommitOutcome.Failed && partialCommit.PartialCompletion,
                "later child commit failure reports partial completion");
            Equal(parentBeforePartialCommit, Sha(parent), "partial child failure blocks parent commit");
            Equal("partial children", Subject(standard), "first child commit is retained");
            File.Delete(rejectingHook);
            var recoverPartial = await service.CommitAsync(parentBranch, "recover partial parent", null,
                includeSubmodules: true, submoduleMessage: "recover remaining child");
            Equal(CommitOutcome.Success, recoverPartial.Outcome, "partial commit can be completed without reset");

            await File.AppendAllTextAsync(Path.Combine(legacy, "tracked.txt"), "batch change\n");
            var batchCommit = await service.CommitAllAsync("批量父描述", null,
                includeSubmodules: true, submoduleMessage: "批量子描述");
            True(batchCommit.Success && batchCommit.Projects.Count == 1, "commitall reuses linked commit path");
            Equal("批量子描述", Subject(legacy), "commitall child message");
            var batchPush = await service.PushAllAsync(includeSubmodules: true);
            True(batchPush.Success && batchPush.ParentPushed, $"pushall children then bare parent: {batchPush.Message}");
            Equal(Sha(parent), RefSha(parentRemote, "main"), "pushall updates parent remote");

            Ensure(await GitRunner.RunAsync(legacy, ["remote", "remove", "origin"]), "remove legacy origin");
            var missingOrigin = await service.PushAsync(parentBranch, includeSubmodules: true);
            True(!missingOrigin.Success && missingOrigin.Message.Contains("不存在 origin"),
                "missing child origin rejected before push");
            Ensure(await GitRunner.RunAsync(legacy, ["remote", "add", "origin", legacyRemote]),
                "restore removed legacy origin");

            Ensure(await GitRunner.RunAsync(standard, ["checkout", "--detach"]), "detach standard child");
            var detached = await service.CommitAsync(parentBranch, "must reject detached", null, true);
            True(detached.Outcome == CommitOutcome.Failed && detached.Message.Contains("detached HEAD"),
                "detached child rejected");
            Ensure(await GitRunner.RunAsync(standard, ["checkout", standardBranch]), "restore standard branch");

            Ensure(await GitRunner.RunAsync(legacy, ["config", "user.name", ""]), "blank legacy identity");
            var noIdentity = await service.CommitAsync(parentBranch, "must reject identity", null, true);
            True(noIdentity.Outcome == CommitOutcome.Failed && noIdentity.Message.Contains("user.name"),
                "missing child identity rejected");
            await ConfigureIdentity(legacy);

            await File.AppendAllTextAsync(Path.Combine(standard, "tracked.txt"), "pointer mismatch\n");
            Ensure(await GitRunner.RunAsync(standard, ["add", "-A"]), "stage pointer mismatch");
            Ensure(await GitRunner.RunAsync(standard, ["commit", "-m", "unrecorded child"]), "commit pointer mismatch");
            var mismatch = await service.PushAsync(parentBranch, includeSubmodules: true);
            True(!mismatch.Success && mismatch.Message.Contains("指针", StringComparison.Ordinal),
                "uncommitted parent gitlink blocks push");
            Ensure(await GitRunner.RunAsync(standard, ["reset", "--hard", GitlinkSha(parent, standardRelative)]),
                "restore recorded standard head");

            var large = Path.Combine(standard, "large.bin");
            await File.WriteAllBytesAsync(large, new byte[2 * 1024 * 1024]);
            var parentScan = WorktreeFileScanner.ScanDirectory(
                parent, 1024 * 1024, 1024 * 1024, [standard, legacy]);
            True(parentScan.LargeFiles.Count == 0, "parent size scan excludes gitlink directories");
            File.Delete(large);

            var nested = Path.Combine(standard, "nested-child");
            Ensure(await GitRunner.RunAsync(standard, ["init", "-b", "nested-main", nested]), "init nested child");
            await ConfigureIdentity(nested);
            await File.WriteAllTextAsync(Path.Combine(nested, "nested.txt"), "nested\n");
            Ensure(await GitRunner.RunAsync(nested, ["add", "."]), "add nested seed");
            Ensure(await GitRunner.RunAsync(nested, ["commit", "-m", "nested seed"]), "commit nested seed");
            Ensure(await GitRunner.RunAsync(standard, ["add", "--", "nested-child"]), "stage nested gitlink");
            Ensure(await GitRunner.RunAsync(standard, ["commit", "-m", "register nested"]), "register nested gitlink");
            await File.AppendAllTextAsync(Path.Combine(nested, "nested.txt"), "nested dirty\n");
            var nestedDirty = await service.CommitAsync(parentBranch, "must reject nested", null, true);
            True(nestedDirty.Outcome == CommitOutcome.Failed && nestedDirty.Message.Contains("第二层 gitlink"),
                $"dirty second-level gitlink rejected: {nestedDirty.Outcome} {nestedDirty.Message}");

            Ensure(await GitRunner.RunAsync(parent,
                ["update-index", "--add", "--cacheinfo", "160000", Sha(legacy), "missing-child"]),
                "register missing gitlink in index");
            var uninitialized = await gitlinks.DiscoverAsync(parent);
            True(!uninitialized.Success && uninitialized.Message.Contains("未初始化"),
                "uninitialized gitlink rejected before mutation");

            var registry = new CommandRegistry();
            ProjectCommands.RegisterAll(registry, service, null!);
            foreach (var commandName in new[] { "janus.proj.commit", "janus.proj.push", "janus.proj.commitall", "janus.proj.pushall" })
            {
                True(registry.TryGet(commandName, out var descriptor), $"{commandName} registered");
                var parameter = descriptor.Parameters.Single(item => item.Name == "submodules");
                Equal(ParamType.Bool, parameter.Type, $"{commandName} submodules is bool");
                Equal("false", parameter.Default, $"{commandName} compatibility default");
            }
            True(registry.TryGet("janus.proj.commit", out var commitCommand)
                 && commitCommand.Parameters.Any(item => item.Name == "submsg"), "janus.proj.commit submsg schema");
            True(registry.TryGet("janus.proj.commitall", out var commitAllCommand)
                 && commitAllCommand.Parameters.Any(item => item.Name == "submsg"), "janus.proj.commitall submsg schema");

        }
        finally
        {
            if (Directory.Exists(root))
                DeleteTree(root);
        }
    }

    private static async Task CreateChild(string path, string branch, string remote)
    {
        Ensure(await GitRunner.RunAsync(Path.GetDirectoryName(path)!, ["init", "-b", branch, path]),
            $"init child {path}");
        await ConfigureIdentity(path);
        await File.WriteAllTextAsync(Path.Combine(path, "tracked.txt"), "tracked\n");
        await File.WriteAllTextAsync(Path.Combine(path, "delete.txt"), "delete me\n");
        Ensure(await GitRunner.RunAsync(path, ["add", "."]), $"add child {path}");
        Ensure(await GitRunner.RunAsync(path, ["commit", "-m", "child seed"]), $"commit child {path}");
        Ensure(await GitRunner.RunAsync(Path.GetDirectoryName(remote)!, ["init", "--bare", remote]),
            $"init child remote {remote}");
        Ensure(await GitRunner.RunAsync(path, ["remote", "add", "origin", remote]), $"add child origin {path}");
        Ensure(await GitRunner.RunAsync(path, ["push", "origin", branch]), $"push child seed {path}");
    }

    private static Task ConfigureIdentity(string repository)
        => SmokeKit.ConfigureIdentity(repository, "Submodule Safety Smoke", "submodule-safety@example.invalid");
}
