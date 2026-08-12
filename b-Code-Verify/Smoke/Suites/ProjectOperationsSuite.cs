using System.ComponentModel;
using System.Reflection;
using HistoryVulcan.Core.Commands;
using HistoryJanus.Git;
using HistoryJanus.Views;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

internal static class ProjectOperationsSuite
{
    public static Task RunAsync(string[] args)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunRuleDeferredSave();
                RunRuleNoChange();
                RunRuleValidationFailure();
                RunRuleConfirmationCancellation();
                RunRuleBoundarySaves();
                RunRuleSaveFailure();
                RunRuleEditingModel();
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

    private static void RunRuleDeferredSave()
    {
        var executions = 0;
        string? changesJson = null;
        var releasePreview = new TaskCompletionSource<bool>();
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "janus.gitrule.batchset",
            Summary = "deferred save fixture",
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "project", Required = true },
                new ParameterSpec { Name = "changes", Description = "changes", Required = true },
                new ParameterSpec
                {
                    Name = "apply", Description = "apply", Type = ParamType.Bool, Default = "false",
                },
            ],
            Handler = async context =>
            {
                executions++;
                changesJson = context.RequireString("changes");
                var applied = context.GetBool("apply");
                if (!applied)
                    await releasePreview.Task;
                return CommandResult.Ok("saved", new GitFileRuleBatchPreview(
                    context.RequireString("name"), [], "", "", 0, 0, 0,
                    Changed: true, Applied: applied));
            },
        });
        var bus = new CommandBus(registry, new MemoryLog());
        // DEC-008：GitHub 面板并入本页；规则用例不触达 GitHub 服务，传空访问器即可。
        var view = new ProjectOperationsView(() => bus, new ProjectSelectionState(), _ => false, () => null);
        SetLoadedRuleProject(view, "main");
        var rules = Rules(view);
        var row = new ProjectOperationsView.RuleEditRow("*.deferred");
        AttachRuleChanged(view, row);
        rules.Add(row);
        row.Lfs = true;
        row.Lf = true;
        Equal(0, executions, "editing rules stays in memory while the rules page is visible");

        var firstLeave = LeaveRulesPage(view);
        Equal(1, executions, "leaving starts exactly one batch preview");
        var concurrentLeave = LeaveRulesPage(view);
        Equal(1, executions, "concurrent leave reuses the in-flight save task");
        releasePreview.SetResult(true);
        True(firstLeave.GetAwaiter().GetResult(), "deferred rule batch saves successfully");
        True(concurrentLeave.GetAwaiter().GetResult(), "concurrent leave observes the same save result");

        Equal(2, executions, "one page leave executes one preview and one apply");
        var changes = System.Text.Json.JsonSerializer.Deserialize<List<GitFileRuleChange>>(
            changesJson!, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        True(changes is [{ Pattern: "*.deferred", Track: true, Lfs: false, Lf: true }],
            "deferred save persists the final linked checkbox state");
        True(!row.IsDirty, "successful page-leave save accepts the edited snapshot");
        True(LeaveRulesPage(view).GetAwaiter().GetResult(), "leaving again with no dirty rules succeeds");
        Equal(2, executions, "leaving with no dirty rules performs no command");
    }

    private static void RunRuleNoChange()
    {
        var calls = new List<string>();
        var bus = RuleFlowBus(calls, batchChanged: false);
        var selection = new ProjectSelectionState { CurrentProjectName = "main" };
        var view = new ProjectOperationsView(() => bus, selection, _ => false, () => null);
        SetLoadedRuleProject(view, "main");
        var row = AddDirtyDraft(view, "*.unchanged");

        True(LeaveRulesPage(view).GetAwaiter().GetResult(), "unchanged preview allows page leave");
        True(calls.SequenceEqual(["batch:preview"]),
            "unchanged preview does not request confirmation or apply");
        True(!row.IsDirty, "unchanged preview accepts the in-memory snapshot");
    }

    private static void RunRuleBoundarySaves()
    {
        VerifyProjectSwitchSavesFirst();
        VerifyProjectSwitchFailureRetainsDraft();
        VerifyRefreshSavesFirst();
        VerifyDeleteSavesFirst();
    }

    private static void RunRuleValidationFailure()
    {
        var calls = new List<string>();
        var bus = RuleFlowBus(calls);
        var selection = new ProjectSelectionState { CurrentProjectName = "main" };
        var view = new ProjectOperationsView(() => bus, selection, _ => false, () => null);
        SetLoadedRuleProject(view, "main");
        var row = AddDirtyDraft(view, "*.invalid");
        row.Track = null;
        var rulesButton = MoveOutsideRules(view);

        True(!LeaveRulesPage(view).GetAwaiter().GetResult(),
            "invalid dirty rule blocks page leave");
        Equal(0, calls.Count, "invalid dirty rule performs no preview or write");
        True(row.IsDirty, "validation failure retains the dirty rule");
        True(rulesButton.IsChecked == true, "validation failure restores the rules segment");
    }

    private static void RunRuleConfirmationCancellation()
    {
        var handlerCalls = 0;
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "janus.gitrule.batchset",
            Summary = "cancelled rule save fixture",
            Parameters = [Text("name", required: true), Text("changes", required: true), Flag("apply")],
            ConfirmPrompt = context => context.GetBool("apply") ? "confirm rule batch" : null,
            Handler = CommandDescriptor.Sync(context =>
            {
                handlerCalls++;
                var applied = context.GetBool("apply");
                return CommandResult.Ok("batch", new GitFileRuleBatchPreview(
                    context.RequireString("name"), [], "", "", 0, 0, 0,
                    Changed: true, Applied: applied));
            }),
        });
        var bus = new CommandBus(registry, new MemoryLog())
        {
            Confirmation = new RejectConfirmation(),
        };
        var selection = new ProjectSelectionState { CurrentProjectName = "main" };
        var view = new ProjectOperationsView(() => bus, selection, _ => false, () => null);
        SetLoadedRuleProject(view, "main");
        var row = AddDirtyDraft(view, "*.cancelled");
        var rulesButton = MoveOutsideRules(view);

        True(!LeaveRulesPage(view).GetAwaiter().GetResult(),
            "host confirmation cancellation blocks page leave");
        Equal(1, handlerCalls, "confirmation cancellation runs preview but blocks apply handler");
        True(row.IsDirty, "confirmation cancellation retains the dirty rule");
        True(rulesButton.IsChecked == true, "confirmation cancellation restores the rules segment");
    }

    private static void VerifyProjectSwitchSavesFirst()
    {
        var calls = new List<string>();
        var bus = RuleFlowBus(calls);
        var selection = new ProjectSelectionState { CurrentProjectName = "next" };
        var view = new ProjectOperationsView(() => bus, selection, _ => false, () => null);
        SetField(view, "_projectNames", new List<string> { "main", "next" });
        SetLoadedRuleProject(view, "main");
        AddDirtyDraft(view, "*.switch");

        InvokeTask(view, "ApplySharedSelectionAsync").GetAwaiter().GetResult();

        AssertBatchPrecedes(calls, "scan:next", "project switch");
        Equal("next", LoadedRuleProject(view), "project switch loads the selected project after saving");
    }

    private static void VerifyRefreshSavesFirst()
    {
        var calls = new List<string>();
        var bus = RuleFlowBus(calls);
        var selection = new ProjectSelectionState { CurrentProjectName = "main" };
        var view = new ProjectOperationsView(() => bus, selection, _ => false, () => null);
        SetLoadedRuleProject(view, "main");
        AddDirtyDraft(view, "*.refresh");

        InvokeTask(view, "RefreshRulesAsync").GetAwaiter().GetResult();

        AssertBatchPrecedes(calls, "scan:main", "rule refresh");
        Equal(1, calls.Count(call => call == "scan:main"),
            "rule refresh scans exactly once after saving");
    }

    private static void VerifyProjectSwitchFailureRetainsDraft()
    {
        var calls = new List<string>();
        var bus = RuleFlowBus(calls, batchApplySucceeds: false);
        var selection = new ProjectSelectionState { CurrentProjectName = "next" };
        var view = new ProjectOperationsView(() => bus, selection, _ => false, () => null);
        SetField(view, "_projectNames", new List<string> { "main", "next" });
        SetLoadedRuleProject(view, "main");
        var row = AddDirtyDraft(view, "*.switch-failed");
        var rulesButton = MoveOutsideRules(view);

        InvokeTask(view, "ApplySharedSelectionAsync").GetAwaiter().GetResult();
        Equal("main", selection.CurrentProjectName,
            "failed project-switch save restores the original project selection");
        True(row.IsDirty && Rules(view).Contains(row),
            "failed project-switch save retains the dirty draft");
        True(rulesButton.IsChecked == true,
            "failed project-switch save restores the rules segment");

        InvokeTask(view, "ApplySharedSelectionAsync").GetAwaiter().GetResult();
        True(row.IsDirty && Rules(view).Contains(row),
            "restored project selection does not reload over the retained draft");
        True(!calls.Any(call => call.StartsWith("scan:", StringComparison.Ordinal)),
            "failed project switch never starts a rule reload");
    }

    private static void VerifyDeleteSavesFirst()
    {
        var calls = new List<string>();
        var bus = RuleFlowBus(calls);
        var selection = new ProjectSelectionState { CurrentProjectName = "main" };
        var view = new ProjectOperationsView(() => bus, selection, _ => false, () => null);
        SetLoadedRuleProject(view, "main");
        var info = new GitFileRuleInfo(
            "*.delete", true, false, false, true, 1, 1, 0, 0, 0, 0, "tracked");
        var row = new ProjectOperationsView.RuleEditRow(null, info);
        AttachRuleChanged(view, row);
        Rules(view).Add(row);
        ((System.Windows.Controls.DataGrid)view.FindName("RuleGrid")).SelectedItem = row;
        row.Track = false;

        InvokeTask(view, "DeleteSelectedRuleAsync").GetAwaiter().GetResult();

        AssertBatchPrecedes(calls, "remove:preview", "rule delete");
        True(calls.Contains("remove:apply"), "rule delete applies only after dirty rules are saved");
    }

    private static CommandBus RuleFlowBus(
        List<string> calls, bool batchChanged = true, bool batchApplySucceeds = true)
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "janus.gitrule.batchset",
            Summary = "rule boundary save fixture",
            Parameters =
            [
                Text("name", required: true),
                Text("changes", required: true),
                Flag("apply"),
            ],
            Handler = CommandDescriptor.Sync(context =>
            {
                var applied = context.GetBool("apply");
                calls.Add(applied ? "batch:apply" : "batch:preview");
                if (applied && !batchApplySucceeds)
                    return CommandResult.Fail("batch apply rejected");
                return CommandResult.Ok("batch", new GitFileRuleBatchPreview(
                    context.RequireString("name"), [], "", "", 0, 0, 0,
                    Changed: batchChanged, Applied: applied));
            }),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "janus.gitrule.scan",
            Summary = "rule scan fixture",
            Parameters = [Text("name", required: true), Flag("refresh")],
            Handler = CommandDescriptor.Sync(context =>
            {
                calls.Add($"scan:{context.RequireString("name")}");
                return CommandResult.Ok("scan", new InventoryReport(
                    1, 0, 0, 0, 0, 1, "default", 1, 0, [], []));
            }),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "janus.gitrule.list",
            Summary = "rule list fixture",
            Parameters = [Text("name", required: true)],
            Handler = CommandDescriptor.Sync(context =>
            {
                calls.Add($"list:{context.RequireString("name")}");
                return CommandResult.Ok("list", Array.Empty<GitFileRuleInfo>());
            }),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "janus.gitrule.remove",
            Summary = "rule delete fixture",
            Parameters = [Text("name", required: true), Text("pattern", required: true), Flag("apply")],
            Handler = CommandDescriptor.Sync(context =>
            {
                var applied = context.GetBool("apply");
                calls.Add(applied ? "remove:apply" : "remove:preview");
                return CommandResult.Ok("remove", new GitFileRulePreview(
                    context.RequireString("name"), context.RequireString("pattern"),
                    false, false, false, "", "", 0, 0, 0, 0,
                    Changed: true, Applied: applied));
            }),
        });
        return new CommandBus(registry, new MemoryLog());
    }

    private static ParameterSpec Text(string name, bool required)
        => new() { Name = name, Description = name, Required = required };

    private static ParameterSpec Flag(string name)
        => new() { Name = name, Description = name, Type = ParamType.Bool, Default = "false" };

    private static ProjectOperationsView.RuleEditRow AddDirtyDraft(
        ProjectOperationsView view, string pattern)
    {
        var row = new ProjectOperationsView.RuleEditRow(pattern);
        AttachRuleChanged(view, row);
        Rules(view).Add(row);
        row.Lf = true;
        return row;
    }

    private static void AssertBatchPrecedes(List<string> calls, string next, string boundary)
    {
        True(calls.Count >= 3 && calls[0] == "batch:preview" && calls[1] == "batch:apply",
            $"{boundary} starts with one batch preview and one apply");
        True(calls.IndexOf(next) > 1, $"{boundary} continues only after the batch save");
    }

    private static System.Windows.Controls.RadioButton MoveOutsideRules(ProjectOperationsView view)
    {
        var historyButton = (System.Windows.Controls.RadioButton)view.FindName("HistoryPageButton");
        var rulesButton = (System.Windows.Controls.RadioButton)view.FindName("RulesPageButton");
        historyButton.IsChecked = true;
        True(!rulesButton.IsChecked.GetValueOrDefault(), "fixture starts outside the rules segment");
        return rulesButton;
    }

    private static void RunRuleSaveFailure()
    {
        var executions = 0;
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "janus.gitrule.batchset",
            Summary = "failed deferred save fixture",
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "project", Required = true },
                new ParameterSpec { Name = "changes", Description = "changes", Required = true },
                new ParameterSpec
                {
                    Name = "apply", Description = "apply", Type = ParamType.Bool, Default = "false",
                },
            ],
            Handler = CommandDescriptor.Sync(context =>
            {
                executions++;
                if (context.GetBool("apply"))
                    return CommandResult.Fail("save rejected");
                return CommandResult.Ok("preview", new GitFileRuleBatchPreview(
                    context.RequireString("name"), [], "", "", 0, 0, 0,
                    Changed: true, Applied: false));
            }),
        });
        var view = new ProjectOperationsView(
            () => new CommandBus(registry, new MemoryLog()), new ProjectSelectionState(), _ => false, () => null);
        SetLoadedRuleProject(view, "main");
        var row = new ProjectOperationsView.RuleEditRow("*.failed");
        AttachRuleChanged(view, row);
        Rules(view).Add(row);
        row.Track = false;

        var rulesButton = MoveOutsideRules(view);

        True(!LeaveRulesPage(view).GetAwaiter().GetResult(), "failed page-leave save reports failure");
        Equal(2, executions, "failed save performs one preview and one apply attempt");
        True(row.IsDirty, "failed save retains the dirty rule data");
        True(rulesButton.IsChecked == true, "failed save restores the Git file rules segment");
    }

    private static System.Collections.ObjectModel.ObservableCollection<ProjectOperationsView.RuleEditRow>
        Rules(ProjectOperationsView view)
        => (System.Collections.ObjectModel.ObservableCollection<ProjectOperationsView.RuleEditRow>)
            typeof(ProjectOperationsView).GetField("_rules", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(view)!;

    private static void SetLoadedRuleProject(ProjectOperationsView view, string project)
        => SetField(view, "_loadedRuleProject", project);

    private static string? LoadedRuleProject(ProjectOperationsView view)
        => (string?)typeof(ProjectOperationsView).GetField(
                "_loadedRuleProject", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(view);

    private static void SetField(ProjectOperationsView view, string name, object? value)
        => typeof(ProjectOperationsView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, value);

    private static void AttachRuleChanged(
        ProjectOperationsView view, ProjectOperationsView.RuleEditRow row)
    {
        var method = typeof(ProjectOperationsView).GetMethod(
            "OnRuleRowChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        row.PropertyChanged += (PropertyChangedEventHandler)Delegate.CreateDelegate(
            typeof(PropertyChangedEventHandler), view, method);
    }

    private static Task<bool> LeaveRulesPage(ProjectOperationsView view)
        => (Task<bool>)typeof(ProjectOperationsView).GetMethod(
                "SaveRulesOnPageLeaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, null)!;

    private static Task InvokeTask(ProjectOperationsView view, string method)
        => (Task)typeof(ProjectOperationsView).GetMethod(
                method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, null)!;

    private sealed class RejectConfirmation : IConfirmationService
    {
        public bool Confirm(string prompt) => false;
    }

    private static void RunRuleEditingModel()
    {
        var scannedFormat = new FormatRow(
            "*.cs", 12, 1, 12, 0, 12, 1024, 4096, "undecided", null, null, null);
        var scannedRow = new ProjectOperationsView.RuleEditRow(scannedFormat, null);
        True(scannedRow.Track == null && scannedRow.Lfs == null && scannedRow.Lf == null,
            "scanned undeclared format preserves nullable rule state");
        True(!scannedRow.IsDirty, "loaded scanned row starts clean");
        scannedRow.Track = true;
        True(scannedRow.Lfs == false && scannedRow.Lf == false,
            "choosing Git makes both storage attributes explicit false");
        True(scannedRow.IsDirty && scannedRow.IsValid,
            "editing a complete scanned rule marks it valid and dirty");
        scannedRow.ResetChanges();
        True(!scannedRow.IsDirty && scannedRow.Track == null,
            "reset restores the loaded snapshot and clears dirty state");

        var lfRule = new GitFileRuleInfo(
            "*.md", true, false, true, true, 3, 3, 0, 0, 0, 3, "3/3 LF");
        var lfRow = new ProjectOperationsView.RuleEditRow(
            new FormatRow("*.md", 3, 1, 3, 0, 0, 100, 200, "tracked", true, false, true),
            lfRule);
        True(lfRow.IsDeclared && lfRow.Lfs == false && lfRow.Lf == true,
            "declared LF row keeps explicit non-LFS state");
        lfRow.Lf = false;
        True(lfRow.IsDirty, "declared row change is tracked");
        lfRow.Lf = true;
        True(!lfRow.IsDirty, "restoring original values clears dirty state");

        var noExtensionRow = new ProjectOperationsView.RuleEditRow(
            new FormatRow(FormatInventoryService.NoExtension, 2, 1, 2, 0, 2, 10, 20,
                "undecided", null, null, null),
            null);
        True(!noExtensionRow.CanEdit,
            "no-extension aggregate remains visible but cannot become an invalid format rule");

        var mergeMethod = typeof(ProjectOperationsView).GetMethod(
            "MergeRows", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ProjectOperationsView.MergeRows not found");
        var report = new InventoryReport(
            1, 14, 14, 0, 14, 0, "default", 1, 10,
            [scannedFormat, new FormatRow(FormatInventoryService.NoExtension, 2, 1, 2, 0, 2, 10, 20,
                "undecided", null, null, null)], []);
        IReadOnlyList<GitFileRuleInfo> rules =
        [
            lfRule with { Pattern = "*.cs", FileCount = 12 },
            new GitFileRuleInfo("*.xlsx", true, true, false, true, 0, 0, 0, 0, 0, 0, "0 files"),
            new GitFileRuleInfo("Library/", false, false, false, true, 0, 0, 0, 0, 0, 0, "0 files"),
        ];
        var merged = ((IEnumerable<ProjectOperationsView.RuleEditRow>)mergeMethod.Invoke(
            null, [report, rules])!).ToList();
        Equal(4, merged.Count, "rule table is the de-duplicated union of scan and declarations");
        Equal(1, merged.Count(row => row.Pattern == "*.cs"),
            "declared rule overlays scanned format once");
        True(merged.Any(row => row.Pattern == "*.xlsx" && row.IsDeclared && row.FileCount == 0),
            "zero-file declaration remains visible");
        True(merged.Any(row => row.Pattern == "Library/" && row.IsDeclared),
            "directory declaration remains visible");
    }
}
