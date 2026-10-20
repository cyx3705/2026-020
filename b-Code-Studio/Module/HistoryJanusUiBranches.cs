using HistoryJanus.Git;
using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Module;

/// <summary>
/// 项目操作页的分支控制行（5.16.1）：面板第三行的选项条，加上它下面那张历史表与右键菜单的四种操作。
/// </summary>
internal static partial class HistoryJanusUiCommands
{
    private static void RegisterBranchCommands(
        ICommandRegistrar registry, ICommandBus bus, Func<StudioBusinessComposition?> business)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "janus.ui.branchop",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "分支控制行右键菜单：切换、合并到当前、分出、删除",
            Example = "janus.ui.branchop 2026-020-HistoryJanus feature-x switch",
            HiddenReason = "本机界面动作；模型请用 janus.branch.*",
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "已登记项目的工作树名称，例如 2026-020-HistoryJanus", Required = true, Position = 0 },
                new ParameterSpec { Name = "branch", Description = "右键所在那一项的分支名，例如 feature-x", Required = true, Position = 1 },
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

    /// <summary>
    /// 分支控制行的候选：当前分支、main，以及还没并入 main 的分支（5.16.1，用户定：合并完的不再摆出来）。
    /// 当前分支排第一——选项条默认选第一项，历史表一打开看的就是工作树所在的那条。
    /// </summary>
    private static async Task<CommandResult> LoadBranchesAsync(
        CommandContext context, Func<StudioBusinessComposition?> business)
    {
        var name = context.GetString("name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Rows([]);
        if (business()?.Branches is not { } branches)
            return CommandResult.Fail("Janus 分支服务尚未就绪");
        var listed = await branches.ListAsync(name, context.Cancellation);
        // 已归档项目只剩 z 文件夹，没有分支可列：给一项 main，历史表照样能按归档记录取。
        if (!listed.Success || listed.Report is not { } report)
            return Rows([Row(("value", ProjectService.MainlineBranch), ("text", ProjectService.MainlineBranch))]);
        return Rows(UiBranchProjection.Options(report));
    }

    internal static class UiBranchProjection
    {
        /// <summary>
        /// 选项条上一个分支按钮最多这么多字（5.16.2，用户定）：分支名常常很长，原样摆一行放不下几个。
        /// 超长的截短、末尾补「…」；当前分支的「·当前」也算在里面。全名悬停可见（Aurora 1.38.1 起）。
        /// </summary>
        internal const int MaxLabelLength = 10;

        private const string CurrentMark = "·当前";

        public static IReadOnlyList<IReadOnlyDictionary<string, string>> Options(BranchListReport report)
            => report.Branches
                .Where(branch => branch.IsCurrent || !branch.MergedIntoMain)
                .Select(branch => Row(
                    ("value", branch.Name),
                    ("text", Label(branch.Name, branch.IsCurrent))))
                .ToList();

        internal static string Label(string name, bool current)
        {
            var room = current ? MaxLabelLength - CurrentMark.Length : MaxLabelLength;
            var shown = name.Length <= room ? name : name[..(room - 1)] + "…";
            return current ? shown + CurrentMark : shown;
        }
    }

    private static async Task<CommandResult> LoadHistoryAsync(
        CommandContext context, ICommandBus bus, Func<StudioBusinessComposition?> business)
    {
        var name = context.GetString("name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Fail("未知 Janus 页面视图: history（缺少 name）");

        // 分支来自分支控制行的通道。刚换项目时通道里可能还是上一个项目的分支名（选项条重取是异步的）：
        // 这个项目没有它就退回当前分支，而不是报一张错表。
        var branch = context.GetString("branch")?.Trim() ?? "";
        if (business()?.Branches is { } branches)
        {
            var listed = await branches.ListAsync(name, context.Cancellation);
            if (listed.Success && listed.Report is { } report
                && !report.Branches.Any(item => item.Name == branch))
                branch = report.Current;
            else if (!listed.Success)
                branch = "";
        }

        // 读不到分支（已归档项目）时不带 branch：历史服务按原样走 main / 远端记录。
        var command = $"janus.history.list name={Quote(name)} limit=200" +
                      (branch.Length > 0 ? $" branch={Quote(branch)}" : "");
        var result = await bus.ExecuteAsync(command, context.Source, context.Cancellation);
        return result.Success
            ? Rows(UiHistoryProjection.Read(result.Data))
            : CommandResult.Fail(result.Message);
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
                    return ToCommandResult(await WithCleanWorktreeAsync(
                        () => branches.SwitchAsync(name, branch, context.Cancellation)));
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
            // 切换与合并改了工作树和 HEAD：历史表与总览那一行都要对上现场。
            // 分支控制行的候选由 Aurora 在动作成功后自己重取（选项条的规矩），这里不管。
            _ = await bus.ExecuteAsync("aurora.ui.refreshdata node=history-rows", context.Source);
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
}
