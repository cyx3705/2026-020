using HistoryVulcan.Core.Commands;
using HistoryJanus.Views;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>
/// 项目操作页的规则分段：全库共用「不纳入仓库」清单的编辑行为。
///
/// 4.x 这里覆盖的是三态规则表的延迟保存、离页协商与并发保存，约 500 行断言。
/// 5.0.0 起页面只是一个设置编辑器：没有按项目状态、没有草稿、也没有离页时机问题
/// （DEC-022）。因此只守住三件事——写入必须走总线命令（确认策略归宿主）、
/// 空清单不发命令、页面不自己拼命令字符串。
/// </summary>
internal static class ProjectOperationsSuite
{
    public static Task RunAsync(string[] args)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunBuilderShapesCommand();
                RunExcludeSaveGoesThroughBus().GetAwaiter().GetResult();
                RunEmptyListPerformsNoCommand().GetAwaiter().GetResult();
                RunFailedSaveDoesNotReread().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("project operations smoke did not complete within 15 seconds");
        if (failure != null)
            throw new InvalidOperationException("project operations smoke failed", failure);
        return Task.CompletedTask;
    }

    private static void RunBuilderShapesCommand()
    {
        var command = ProjectOperationCommandBuilder.Excludes("bin/, obj/, *.user");
        True(command.StartsWith("janus.gitrule.excludes list=", StringComparison.Ordinal),
            "builder emits the excludes command with a list argument");
        True(!command.Contains("name=", StringComparison.Ordinal),
            "the shared list carries no project name");
    }

    private static async Task RunExcludeSaveGoesThroughBus()
    {
        var calls = new List<string>();
        var view = BuildView(BuildBus(calls, succeed: true));

        var result = await view.SaveExcludeListAsync("bin/, obj/, *.log");

        True(result?.Success == true, "a valid list saves through the bus");
        True(calls.Any(call => call.StartsWith("excludes ", StringComparison.Ordinal)),
            "saving executes janus.gitrule.excludes");
        True(calls.Any(call => call.Contains("bin/", StringComparison.Ordinal)),
            "the edited list reaches the command");
        True(calls.Contains("list"), "a successful save re-reads the landing state");
    }

    private static async Task RunEmptyListPerformsNoCommand()
    {
        var calls = new List<string>();
        var view = BuildView(BuildBus(calls, succeed: true));

        var result = await view.SaveExcludeListAsync("   ");

        True(result == null, "an empty list is refused before the bus");
        Equal(0, calls.Count, "an empty list performs no command at all");
    }

    private static async Task RunFailedSaveDoesNotReread()
    {
        var calls = new List<string>();
        var view = BuildView(BuildBus(calls, succeed: false));

        var result = await view.SaveExcludeListAsync("bin/");

        True(result?.Success == false, "a rejected list reports failure");
        True(!calls.Contains("list"), "a failed save does not re-read the landing state");
    }

    private static CommandBus BuildBus(List<string> calls, bool succeed)
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "janus.gitrule.excludes",
            CommandClass = "gitrule",
            Summary = "excludes fixture",
            Example = "janus.gitrule.excludes list=\"bin/\"",
            Parameters = [new ParameterSpec { Name = "list", Description = "list", Required = true }],
            Handler = context =>
            {
                calls.Add($"excludes {context.RequireString("list")}");
                return Task.FromResult(succeed
                    ? CommandResult.Ok("saved")
                    : new CommandResult { Success = false, Message = "rejected" });
            },
        });
        registry.Register(new CommandDescriptor
        {
            Name = "janus.gitrule.list",
            CommandClass = "gitrule",
            Summary = "list fixture",
            Example = "janus.gitrule.list",
            Readonly = true,
            Handler = _ =>
            {
                calls.Add("list");
                return Task.FromResult(CommandResult.Ok("落地状态"));
            },
        });
        return new CommandBus(registry, new MemoryLog());
    }

    private static ProjectOperationsView BuildView(CommandBus bus)
        => new(() => bus, new ProjectSelectionState(), _ => false, () => null);
}
