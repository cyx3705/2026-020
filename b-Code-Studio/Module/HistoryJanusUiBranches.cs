using HistoryJanus.Git;
using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Module;

/// <summary>
/// 项目操作页的分支控制（5.16.0）：分支表、它下面那张历史表，以及右键菜单的四种操作。
/// </summary>
internal static partial class HistoryJanusUiCommands
{
    /// <summary>
    /// 每个项目「正在看哪条分支的历史」。点分支名时改，没点过就看当前分支。
    ///
    /// 不走选择通道：表格不会自动选中首行，历史表按通道取的话，没点过分支之前是一张空表；
    /// 换项目时通道里也还留着上一个项目的分支名。
    /// </summary>
    private static readonly Dictionary<string, string> PickedBranches = new(StringComparer.OrdinalIgnoreCase);

    private static void RegisterBranchCommands(
        ICommandRegistrar registry, ICommandBus bus, Func<StudioBusinessComposition?> business)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "janus.ui.branchpick",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "分支表点分支名：下面的历史表换成这条分支",
            Example = "janus.ui.branchpick 2026-020-HistoryJanus main",
            HiddenReason = "界面内部协议，对模型无意义",
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "已登记项目的工作树名称，例如 2026-020-HistoryJanus", Required = true, Position = 0 },
                new ParameterSpec { Name = "branch", Description = "本地分支名，例如 main", Required = true, Position = 1 },
            ],
            Handler = async context =>
            {
                var name = context.RequireString("name").Trim();
                var branch = context.RequireString("branch").Trim();
                lock (PickedBranches)
                    PickedBranches[name] = branch;
                await RefreshBranchViewsAsync(bus, context.Source);
                return CommandResult.Ok($"{name}：显示 {branch} 的提交历史");
            },
        });

        registry.Register(new CommandDescriptor
        {
            Name = "janus.ui.branchop",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "分支表右键菜单：切换、合并到当前、分出、删除",
            Example = "janus.ui.branchop 2026-020-HistoryJanus feature-x switch",
            HiddenReason = "本机界面动作；模型请用 janus.branch.*",
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "已登记项目的工作树名称，例如 2026-020-HistoryJanus", Required = true, Position = 0 },
                new ParameterSpec { Name = "branch", Description = "右键所在那一行的分支名，例如 feature-x", Required = true, Position = 1 },
                new ParameterSpec
                {
                    Name = "op",
                    Description = "switch 切换到此分支、merge 合并到当前分支、create 从此分出新分支、delete 删除分支",
                    Required = true,
                    Position = 2,
                    AllowedValues = [BranchOpSwitch, BranchOpMerge, BranchOpCreate, BranchOpDelete],
                },
            ],
            Handler = context => BranchOperationAsync(context, bus, business),
        });
    }

    private static async Task<CommandResult> LoadBranchesAsync(
        CommandContext context, Func<StudioBusinessComposition?> business)
    {
        var name = context.GetString("name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Rows([]);
        if (business()?.Branches is not { } branches)
            return CommandResult.Fail("Janus 分支服务尚未就绪");
        var listed = await branches.ListAsync(name, context.Cancellation);
        if (!listed.Success || listed.Report is not { } report)
            return CommandResult.Fail(listed.Message);
        var viewing = Viewing(name, report);
        return Rows(report.Branches
            .Select(branch => Row(
                ("project", name),
                ("branch", branch.Name),
                ("state", (branch.IsCurrent, branch.Name == viewing) switch
                {
                    (true, true) => "当前 · 查看中",
                    (true, false) => "当前",
                    (false, true) => "查看中",
                    _ => "",
                }),
                ("track", branch.Track),
                ("time", branch.Time),
                ("subject", branch.Subject)))
            .ToList());
    }

    private static async Task<CommandResult> LoadHistoryAsync(
        CommandContext context, ICommandBus bus, Func<StudioBusinessComposition?> business)
    {
        var name = context.GetString("name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Fail("未知 Janus 页面视图: history（缺少 name）");
        var branch = "";
        if (business()?.Branches is { } branches)
        {
            var listed = await branches.ListAsync(name, context.Cancellation);
            if (listed.Success && listed.Report is { } report)
                branch = Viewing(name, report);
        }

        // 读不到分支（已归档项目只剩 z 文件夹）时不带 branch：历史服务按原样走 main / 远端记录。
        var command = $"janus.history.list name={Quote(name)} limit=200" +
                      (branch.Length > 0 ? $" branch={Quote(branch)}" : "");
        var result = await bus.ExecuteAsync(command, context.Source, context.Cancellation);
        return result.Success
            ? Rows(UiHistoryProjection.Read(result.Data))
            : CommandResult.Fail(result.Message);
    }

    /// <summary>正在看的分支：点过且还在就是它，否则是当前分支（游离 HEAD 时退回 main）。</summary>
    private static string Viewing(string name, BranchListReport report)
    {
        lock (PickedBranches)
        {
            if (PickedBranches.TryGetValue(name, out var picked)
                && report.Branches.Any(branch => branch.Name == picked))
                return picked;
            PickedBranches.Remove(name);
        }
        return report.Current.Length > 0 ? report.Current : ProjectService.MainlineBranch;
    }

    private static async Task<CommandResult> BranchOperationAsync(
        CommandContext context, ICommandBus bus, Func<StudioBusinessComposition?> business)
    {
        var name = context.RequireString("name").Trim();
        var branch = context.RequireString("branch").Trim();
        var op = context.RequireString("op").Trim().ToLowerInvariant();
        if (business() is not { } composition)
            return CommandResult.Fail("Janus 项目服务尚未就绪");
        var branches = composition.Branches;

        try
        {
            switch (op)
            {
                case BranchOpSwitch:
                {
                    var result = await WithCleanWorktreeAsync(
                        () => branches.SwitchAsync(name, branch, context.Cancellation));
                    if (result.Success)
                        lock (PickedBranches)
                            PickedBranches.Remove(name);
                    return ToCommandResult(result);
                }
                case BranchOpMerge:
                {
                    var current = await branches.CurrentBranchAsync(name, context.Cancellation);
                    var confirm = await bus.ExecuteAsync(
                        $"aurora.ui.dialog kind=confirm title=合并分支 body={Quote($"把 {branch} 合并到当前分支 {current}？\n有冲突时什么都不动。")} primary=合并 cancel=取消",
                        context.Source, context.Cancellation);
                    if (!confirm.Success)
                        return CommandResult.Ok($"{name}：已取消合并");
                    return ToCommandResult(await WithCleanWorktreeAsync(
                        () => branches.MergeAsync(name, branch, context.Cancellation)));
                }
                case BranchOpCreate:
                {
                    var prompt = await bus.ExecuteAsync(
                        $"aurora.ui.dialog kind=prompt title=分出分支 body={Quote($"从 {branch} 分出的新分支名")} primary=分出 cancel=取消",
                        context.Source, context.Cancellation);
                    if (!prompt.Success)
                        return CommandResult.Ok($"{name}：已取消分出");
                    var created = await branches.CreateAsync(name, branch, DialogValue(prompt), context.Cancellation);
                    return ToCommandResult(created);
                }
                case BranchOpDelete:
                {
                    // 未并入当前分支的要强制删；强制与否都由 janus.branch.delete 自己经宿主确认，
                    // 确认文字会说清楚独有提交会丢。
                    var merged = await branches.IsMergedAsync(name, branch, context.Cancellation);
                    return await bus.ExecuteAsync(
                        $"janus.branch.delete name={Quote(name)} branch={Quote(branch)} force={(merged ? "false" : "true")}",
                        context.Source, context.Cancellation);
                }
                default:
                    return CommandResult.Fail($"未知分支操作：{op}");
            }
        }
        finally
        {
            // 切换与合并改了工作树和 HEAD：分支表、历史表与总览那一行都要对上现场。
            await RefreshBranchViewsAsync(bus, context.Source);
            await RefreshCachedProjectAsync(composition.Projects, name, null);
            _ = await bus.ExecuteAsync("aurora.ui.refreshdata node=projects", context.Source);
        }

        // 工作树脏就被拒的操作：直接弹提交窗口（先看差异，可提交也可丢弃）。
        // 人提交或丢弃之后工作树干净了，就接着做原来的操作；取消则停在拒绝那一步。
        async Task<BranchMutationResult> WithCleanWorktreeAsync(Func<Task<BranchMutationResult>> operation)
        {
            var first = await operation();
            if (first.Success || !first.Dirty)
                return first;
            var decided = await DecideCommitAsync(context, bus, composition.Projects, name);
            if (decided.Command != null)
            {
                var committed = await bus.ExecuteAsync(decided.Command, context.Source, context.Cancellation);
                if (!committed.Success)
                    return new(false, $"{first.Message}；提交失败：{committed.Message}");
            }
            else if (decided.Result is { Success: false } failed)
            {
                return new(false, $"{first.Message}；{failed.Message}");
            }

            var diff = await composition.Projects.ReadWorktreeDiffAsync(name, RepositoryTarget.Parent, context.Cancellation);
            return diff.IsDirty ? first : await operation();
        }
    }

    private static CommandResult ToCommandResult(BranchMutationResult result)
        => result.Success ? CommandResult.Ok(result.Message) : CommandResult.Fail(result.Message);

    private static async Task RefreshBranchViewsAsync(ICommandBus bus, string source)
    {
        foreach (var node in new[] { "branch-rows", "history-rows" })
            _ = await bus.ExecuteAsync($"aurora.ui.refreshdata node={node}", source);
    }
}
