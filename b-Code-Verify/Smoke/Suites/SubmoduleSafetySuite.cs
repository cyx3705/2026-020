using HistoryVulcan.Core.Commands;
using HistoryJanus.Git;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>
/// 嵌套仓库不再单独提交或推送。父仓库里的 gitlink 只是一条路径，
/// 子目录自己的未提交内容不会变成另一次提交。
/// </summary>
internal static class SubmoduleSafetySuite
{
    private const string parentBranch = "2026-231-Parent";

    public static async Task RunAsync(string[] args)
    {
        var root = TemporaryDirectory("submodule-safety");
        var parentRemote = Path.Combine(root, "parent-remote.git");
        var parent = Path.Combine(root, parentBranch);
        var nested = Path.Combine(parent, "nested");
        Directory.CreateDirectory(root);

        try
        {
            await InitStandaloneRepo(parent, "Submodule Safety Smoke", "submodule-safety@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(parent, "README.md"), "seed\n");
            Ensure(await GitRunner.RunAsync(parent, ["add", "."]), "add parent seed");
            Ensure(await GitRunner.RunAsync(parent, ["commit", "-m", "seed"]), "commit parent seed");
            Ensure(await GitRunner.RunAsync(root, ["clone", "--bare", parent, parentRemote]), "clone parent remote");
            Ensure(await GitRunner.RunAsync(parent, ["remote", "add", "origin", parentRemote]), "set parent origin");
            Ensure(await GitRunner.RunAsync(parent, ["push", "-u", "origin", "HEAD"]), "push parent baseline");

            Ensure(await GitRunner.RunAsync(parent, ["init", "-b", "nested-main", nested]), "init nested repo");
            await SmokeKit.ConfigureIdentity(nested, "Submodule Safety Smoke", "submodule-safety@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(nested, "tracked.txt"), "tracked\n");
            Ensure(await GitRunner.RunAsync(nested, ["add", "."]), "add nested seed");
            Ensure(await GitRunner.RunAsync(nested, ["commit", "-m", "nested seed"]), "commit nested seed");
            Ensure(await GitRunner.RunAsync(parent, ["add", "--", "nested"]), "stage nested path");
            Ensure(await GitRunner.RunAsync(parent, ["commit", "-m", "record nested path"]), "commit nested path");

            var settings = new MemorySettings();
            BindLibrary(settings, root, parentBranch);
            var service = new ProjectService(settings, _ => true, root);
            var nestedBefore = Sha(nested);

            await File.WriteAllTextAsync(Path.Combine(parent, "note.txt"), "parent\n");
            var commit = await service.CommitAsync(parentBranch, "parent file", null);
            Equal(CommitOutcome.Success, commit.Outcome, $"parent file commits: {commit.Message}");
            Equal(nestedBefore, Sha(nested), "nested repository is not committed on its own");

            await File.AppendAllTextAsync(Path.Combine(nested, "tracked.txt"), "dirty inside\n");
            var skipped = await service.CommitAsync(parentBranch, "no parent content", null);
            Equal(CommitOutcome.Skipped, skipped.Outcome,
                $"dirty nested tree does not create a parent commit: {skipped.Message}");
            Equal(nestedBefore, Sha(nested), "dirty nested tree is not committed");

            var push = await service.PushAsync(parentBranch);
            True(push.Success && push.ParentPushed, $"dirty nested tree does not block push: {push.Message}");
            Equal(Sha(parent), RefSha(parentRemote, "main"), "push updates the project remote");

            var registry = new TestRegistrar();
            ProjectCommands.RegisterAll(registry, service, null!);
            foreach (var commandName in new[] { "janus.proj.commit", "janus.proj.push", "janus.proj.commitall", "janus.proj.pushall" })
            {
                True(registry.TryGet(commandName, out var descriptor), $"{commandName} registered");
                True(!descriptor.Summary.Contains("子模块", StringComparison.Ordinal),
                    $"{commandName} summary no longer offers a submodule scope");
            }
        }
        finally
        {
            if (Directory.Exists(root))
                DeleteTree(root);
        }
    }
}
