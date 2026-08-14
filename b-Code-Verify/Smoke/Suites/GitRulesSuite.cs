using HistoryVulcan.Core;
using HistoryVulcan.Core.Mcp;
using System.Text;
using HistoryVulcan.Core.Commands;
using HistoryJanus.Git;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>Git 文件规则、LFS/LF 规范化与格式台账。</summary>
internal static class GitRulesSuite
{
    private const string Project = "2026-001-Sample";
    private const string Template = "0000-000-Template";
    private const string Target = "2026-002-Target";

    public static async Task RunAsync(string[] args)
    {
        var root = TemporaryDirectory("git-rules");
        var template = Path.Combine(root, Template);
        var worktree = Path.Combine(root, Project);
        Directory.CreateDirectory(root);

        try
        {
            await InitStandaloneRepo(template, "Git Rules Smoke", "git-rules@example.invalid");
            Ensure(await GitRunner.RunAsync(template, ["lfs", "install", "--local"]), "git lfs local install");

            var attributes =
                "# manual attributes before\r\n" +
                "*.bin filter=lfs diff=lfs merge=lfs -text\r\n" +
                "*.txt text eol=lf\r\n" +
                "# HistoryJanus managed begin\r\n" +
                "*.seed text eol=lf\r\n" +
                "# HistoryJanus managed end\r\n" +
                "# manual attributes after\r\n";
            var ignore =
                "# manual ignore before\r\n" +
                "manual-only/\r\n" +
                "# HistoryJanus managed begin\r\n" +
                "*.old\r\n" +
                "# HistoryJanus managed end\r\n" +
                "# manual ignore after\r\n";
            await File.WriteAllTextAsync(Path.Combine(template, ".gitattributes"), attributes, new UTF8Encoding(true));
            await File.WriteAllTextAsync(Path.Combine(template, ".gitignore"), ignore, new UTF8Encoding(true));
            await File.WriteAllBytesAsync(Path.Combine(template, "asset.bin"), [1, 2, 3, 4, 5]);
            await File.WriteAllTextAsync(Path.Combine(template, "readme.txt"), "hello\r\nworld\r\n", new UTF8Encoding(false));
            Ensure(await GitRunner.RunAsync(template, ["add", "."]), "git add template");
            Ensure(await GitRunner.RunAsync(template, ["commit", "-m", "seed"]), "git commit template");

            var settings = new MemorySettings();
            BindLibrary(settings, root, Template);
            var projects = new ProjectService(settings, _ => true, root);
            var created = await projects.CreateAsync(Project, Template, null);
            True(created.Success, $"create sample project: {created.Message}");
            Ensure(await GitRunner.RunAsync(worktree, ["lfs", "install", "--local"]), "sample lfs install");
            var service = new GitFileRuleService(projects);
            var headBefore = await GitRunner.RunAsync(worktree, ["rev-parse", "HEAD"]);
            Ensure(headBefore, "read initial HEAD");

            var outsideProject = await service.ListAsync("..");
            True(!outsideProject.Success, "unregistered project and boundary escape rejected");
            var invalidPattern = await service.SetAsync(Project, "../*.txt", true, false, true, apply: false);
            True(!invalidPattern.Success, "path-bearing pattern rejected as a controlled failure");

            var batchA = Path.Combine(worktree, "first.batcha");
            var batchB = Path.Combine(worktree, "second.batchb");
            var batchC = Path.Combine(worktree, "third.batchc");
            await File.WriteAllTextAsync(batchA, "first\r\nline\r\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(batchB, "keep locally", new UTF8Encoding(false));
            await File.WriteAllTextAsync(batchC, "ordinary", new UTF8Encoding(false));
            Ensure(await GitRunner.RunAsync(worktree, ["add", "--", "second.batchb"]),
                "track batch ignore fixture before applying rules");
            IReadOnlyList<GitFileRuleChange> batchChanges =
            [
                new("*.batcha", true, false, true),
                new("*.batchb", false, false, false),
                new("*.batchc", true, true, false),
            ];
            var batchPreview = await service.BatchSetAsync(Project, batchChanges, apply: false);
            True(batchPreview.Success && batchPreview.Preview is
            {
                Changed: true,
                Applied: false,
                Items.Count: 3,
                AddToIndex: 2,
                RemoveFromIndex: 1,
            }, "three rule changes produce one complete batch preview");
            var invalidBatch = await service.BatchSetAsync(Project,
            [
                new("*.never", true, false, false),
                new("*.NEVER", false, false, false),
            ], apply: true);
            True(!invalidBatch.Success && invalidBatch.Message.Contains("重复"),
                "duplicate batch pattern rejects the whole batch before writing");

            var batchApplied = await service.BatchSetAsync(Project, batchChanges, apply: true);
            True(batchApplied.Success && batchApplied.Preview is { Applied: true, Items.Count: 3 },
                $"three rule changes apply together: {batchApplied.Message}");
            Ensure(await GitRunner.RunAsync(worktree, ["ls-files", "--error-unmatch", "--", "first.batcha"]),
                "batch LF file added to index");
            Ensure(await GitRunner.RunAsync(worktree, ["ls-files", "--error-unmatch", "--", "third.batchc"]),
                "batch ordinary file added to index");
            True(!(await GitRunner.RunAsync(
                    worktree, ["ls-files", "--error-unmatch", "--", "second.batchb"])).Success,
                "batch ignored file removed from index");
            True(File.Exists(batchB), "batch ignore preserves working-tree file");
            var batchRules = await service.ListAsync(Project);
            True(batchRules.Success
                 && batchRules.Rules.Any(rule => rule.Pattern == "*.batcha" && rule.Track && rule.Lf)
                 && batchRules.Rules.Any(rule => rule.Pattern == "*.batchb" && !rule.Track)
                 && batchRules.Rules.Any(rule => rule.Pattern == "*.batchc" && rule.Track && rule.Lfs && !rule.Lf),
                "all three batch rules persist after one apply");
            var unchangedBatch = await service.BatchSetAsync(Project, batchChanges, apply: false);
            True(unchangedBatch.Success && unchangedBatch.Preview is
            { Changed: false, AddToIndex: 0, RemoveFromIndex: 0, Renormalize: 0 },
                "repeating an already converged batch is a no-op");

            var initialRules = await service.ListAsync(Project);
            True(initialRules.Success, $"initial list: {initialRules.Message}");
            True(initialRules.Rules.Any(rule =>
                    rule.Pattern == "*.bin" && rule.Track && rule.Lfs && rule.LfsPointerCount == 1),
                "existing canonical LFS rule and pointer imported");
            True(initialRules.Rules.Any(rule => rule.Pattern == "*.txt" && rule.Track && rule.Lf
                                              && rule.LfAttributeCount == 1),
                "existing canonical LF rule imported from check-attr truth");

            var attributePath = Path.Combine(worktree, ".gitattributes");
            var ignorePath = Path.Combine(worktree, ".gitignore");
            var originalAttributeBytes = await File.ReadAllBytesAsync(attributePath);
            var originalIgnoreBytes = await File.ReadAllBytesAsync(ignorePath);
            await File.WriteAllBytesAsync(Path.Combine(worktree, "workbook.xlsx"), [9, 8, 7, 6]);

            var preview = await service.SetAsync(Project, "*.xlsx", track: true, lfs: true, lf: false, apply: false);
            True(preview.Success && preview.Preview is { Changed: true, Applied: false }, "LFS preview produced");
            BytesEqual(originalAttributeBytes, await File.ReadAllBytesAsync(attributePath), "preview preserves attributes");
            BytesEqual(originalIgnoreBytes, await File.ReadAllBytesAsync(ignorePath), "preview preserves ignore");

            var lfsApplied = await service.SetAsync(Project, "*.xlsx", true, true, false, apply: true);
            True(lfsApplied.Success && lfsApplied.Preview is { Applied: true }, "LFS rule applied");
            Contains(await File.ReadAllTextAsync(attributePath),
                "*.xlsx filter=lfs diff=lfs merge=lfs -text", "canonical managed LFS line");
            var xlsxPointer = await GitRunner.RunAsync(worktree, ["show", ":workbook.xlsx"]);
            Ensure(xlsxPointer, "read staged xlsx");
            True(xlsxPointer.Output.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal),
                "LFS clean filter created index pointer");
            var afterLfs = await service.ListAsync(Project);
            True(afterLfs.Success && afterLfs.Rules.Any(rule =>
                    rule.Pattern == "*.xlsx" && rule.LfsAttributeCount == 1 && rule.LfsPointerCount == 1),
                "LFS list verifies both final attribute and staged pointer");

            Directory.CreateDirectory(Path.Combine(worktree, "资料 目录"));
            var specialRelative = "资料 目录/数据[1].lfcase";
            await File.WriteAllTextAsync(
                Path.Combine(worktree, "资料 目录", "数据[1].lfcase"), "第一行\r\n第二行\r\n", new UTF8Encoding(false));
            var lfApplied = await service.SetAsync(Project, "*.lfcase", true, false, true, apply: true);
            True(lfApplied.Success, $"LF rule applied: {lfApplied.Message}");
            var lfAttr = await GitRunner.RunAsync(
                worktree, ["check-attr", "text", "eol", "--", specialRelative.Replace('/', '\\')]);
            Ensure(lfAttr, "check special path attributes");
            Contains(lfAttr.Output, "text: set", "LF text attribute final truth");
            Contains(lfAttr.Output, "eol: lf", "LF eol attribute final truth");
            var indexText = await GitRunner.RunAsync(worktree, ["show", $":{specialRelative}"]);
            Ensure(indexText, "read staged LF file");
            True(!indexText.Output.Contains('\r'), "LF file normalized in index");

            var conflict = await service.SetAsync(Project, "*.bad", true, true, true, apply: false);
            True(!conflict.Success && conflict.Message.Contains("互斥"), "LFS and LF rejected together");
            var invalidIgnored = await service.SetAsync(Project, "*.bad", false, true, false, apply: false);
            True(!invalidIgnored.Success, "ignored format cannot enable LFS");

            var localTmp = Path.Combine(worktree, "保留 文件[1].tmp");
            await File.WriteAllTextAsync(localTmp, "keep locally", new UTF8Encoding(false));
            Ensure(await GitRunner.RunAsync(worktree, ["add", "--", "保留 文件[1].tmp"]), "track tmp before ignore");
            var ignoredRule = await service.SetAsync(Project, "*.tmp", false, false, false, apply: true);
            True(ignoredRule.Success, $"ignore rule applied: {ignoredRule.Message}");
            True(File.Exists(localTmp), "track=false preserves working-tree file");
            Contains(await File.ReadAllTextAsync(ignorePath), "*.tmp", "ignore rule written");
            var tmpTracked = await GitRunner.RunAsync(worktree, ["ls-files", "--error-unmatch", "--", "保留 文件[1].tmp"]);
            True(!tmpTracked.Success, "track=false removes index entry");

            var trackedAgain = await service.SetAsync(Project, "*.tmp", true, false, false, apply: true);
            True(trackedAgain.Success, $"track rule applied: {trackedAgain.Message}");
            Ensure(await GitRunner.RunAsync(worktree, ["ls-files", "--error-unmatch", "--", "保留 文件[1].tmp"]),
                "track=true restores index entry");
            var removed = await service.RemoveAsync(Project, "*.tmp", apply: true);
            True(removed.Success, $"remove rule: {removed.Message}");
            True(File.Exists(localTmp), "removing rule preserves local file");

            BytesEqual(OutsideManagedBytes(originalAttributeBytes),
                OutsideManagedBytes(await File.ReadAllBytesAsync(attributePath)),
                "content outside managed attributes block byte-preserved");
            BytesEqual(OutsideManagedBytes(originalIgnoreBytes),
                OutsideManagedBytes(await File.ReadAllBytesAsync(ignorePath)),
                "content outside managed ignore block byte-preserved");
            True((await File.ReadAllBytesAsync(attributePath)).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()),
                "attributes BOM preserved");
            Contains(await File.ReadAllTextAsync(attributePath), "\r\n", "attributes CRLF preserved");

            var registry = new CommandRegistry();
            var inventory = new FormatInventoryService(projects, new MemoryLog(), root);
            GitRuleCommands.RegisterAll(registry, service, inventory, projects);
            var commandNames = registry.All().Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            True(commandNames.SetEquals([
                    "janus.gitrule.list", "janus.gitrule.set", "janus.gitrule.batchset", "janus.gitrule.remove", "janus.gitrule.scan",
                    "janus.gitrule.review", "janus.gitrule.sync",
                ]),
                "command catalog contains the V2.9 combined review command and no split legacy commands");
            True(!commandNames.Contains("janus.gitrule.gaps") && !commandNames.Contains("janus.gitrule.suggest"),
                "split gap and suggestion commands are no longer registered");
            True(!commandNames.Any(name => name.StartsWith("attr.", StringComparison.OrdinalIgnoreCase)),
                "old attr commands absent");
            // V2.4.4:只读性由描述符自描述,不再查名字白名单。判据升级为「真值 + 解释结果」。
            True(registry.TryGet("janus.gitrule.list", out var ruleList) && ruleList.Readonly
                 && McpExposurePolicy.State(ruleList) == "readonly",
                "janus.gitrule.list is readonly MCP projection");
            True(registry.TryGet("janus.gitrule.review", out var ruleReview) && ruleReview.Readonly
                 && McpExposurePolicy.State(ruleReview) == "readonly",
                "janus.gitrule.review is readonly across UI, Web and MCP projections");
            True(registry.TryGet("janus.gitrule.set", out var ruleSet) && !ruleSet.Readonly
                 && McpExposurePolicy.State(ruleSet) != "readonly",
                "janus.gitrule.set requires standard MCP policy");
            True(registry.TryGet("janus.gitrule.batchset", out var batchSet) && !batchSet.Readonly
                 && McpExposurePolicy.State(batchSet) != "readonly",
                "janus.gitrule.batchset requires standard MCP policy");
            var commandLog = new MemoryLog();
            var commandBus = new CommandBus(registry, commandLog);
            var executed = false;
            commandBus.Executed += (_, source, result) => executed = source == "UI" && result.Success;
            var listCommand = await commandBus.ExecuteAsync($"janus.gitrule.list name={Project}", "UI");
            True(listCommand.Success && executed,
                $"automatic rule load completes through CommandBus lifecycle; success={listCommand.Success}, " +
                $"executed={executed}, message={listCommand.Message}");

            var commandChanges = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new GitFileRuleChange("*.batcha", true, false, true),
                new GitFileRuleChange("*.batchc", true, true, false),
            });
            var batchCommand = await commandBus.ExecuteAsync(
                $"janus.gitrule.batchset name={Project} changes={CommandParser.QuoteArg(commandChanges)} apply=false", "UI");
            True(batchCommand.Success && batchCommand.Data is GitFileRuleBatchPreview { Items.Count: 2 },
                "batch JSON executes through CommandBus and returns typed preview");

            await File.WriteAllTextAsync(Path.Combine(worktree, "proposal.md"), "review me\n");
            await File.WriteAllTextAsync(Path.Combine(worktree, "mystery.reviewunknown"), "unknown\n");
            var directoryCandidate = Path.Combine(worktree, "generated-review");
            Directory.CreateDirectory(directoryCandidate);
            for (var index = 0; index < 50; index++)
                await File.WriteAllTextAsync(Path.Combine(directoryCandidate, $"item-{index:00}"), "generated\n");

            var scanCommand = await commandBus.ExecuteAsync($"janus.gitrule.scan name={Project} refresh=true", "UI");
            True(scanCommand.Success && scanCommand.Data is InventoryReport
            {
                ProjectCount: 1,
                Formats.Count: > 0,
            }, "format inventory executes through CommandBus and returns structured data");
            var reviewCommand = await commandBus.ExecuteAsync($"janus.gitrule.review name={Project}", "UI");
            True(reviewCommand.Success && reviewCommand.Data is GitRuleReviewReport
            {
                Gaps.Directories.Count: > 0,
                Suggestions.Count: > 0,
                UnknownFormats.Count: > 0,
                SuggestedFileCount: > 0,
            } review && review.Gaps.UndecidedCount > 0 && review.SuggestedCoverageRate > 0,
                "one review returns suggestions, unknown formats and directory candidates from one scan");
            var reviewJson = System.Text.Json.JsonSerializer.SerializeToElement(reviewCommand.Data);
            True(System.Text.Json.JsonSerializer.Deserialize<GitRuleReviewReport>(
                        reviewJson.GetRawText(),
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    is { Gaps.Formats.Count: > 0 },
                "the combined review contract survives JSON projection");

            var cachedReview = await commandBus.ExecuteAsync($"janus.gitrule.review name={Project}", "UI");
            True(cachedReview.Data is GitRuleReviewReport { Gaps.CachedProjects: 1 },
                "repeated review reuses the project inventory cache");
            var missingReview = await commandBus.ExecuteAsync("janus.gitrule.review name=missing-project", "UI");
            True(!missingReview.Success, "review reports a controlled failure when no scan target exists");

            True((await service.SetAsync(Project, "*.md", true, false, true, apply: true)).Success,
                "apply suggested text rule for zero-gap regression");
            True((await service.SetAsync(Project, "*.reviewunknown", false, false, false, apply: true)).Success,
                "apply manual decision for unknown format");
            True((await service.SetAsync(Project, "*.tmp", false, false, false, apply: true)).Success,
                "apply suggested temporary-file decision");
            True((await service.SetAsync(Project, "generated-review/", false, false, false, apply: true)).Success,
                "apply directory decision for zero-gap regression");
            var resolvedReview = await commandBus.ExecuteAsync($"janus.gitrule.review name={Project}", "UI");
            True(resolvedReview.Success && resolvedReview.Data is GitRuleReviewReport resolved
                 && resolved.Gaps.UndecidedCount == 0
                 && resolved.Gaps.Formats.Count == 0
                 && resolved.Gaps.Directories.Count == 0
                 && resolved.Suggestions.Count == 0
                 && resolved.UnknownFormats.Count == 0
                 && resolved.SuggestedCoverageRate == 1d,
                "review reports zero undecided items after every decision is applied: " +
                resolvedReview.Message);

            Ensure(await GitRunner.RunAsync(worktree, ["rm", "-f", "--", "asset.bin", "workbook.xlsx"]),
                "remove all LFS pointers for empty JSON regression");
            var emptyLfsList = await service.ListAsync(Project);
            True(emptyLfsList.Success, $"null Git LFS files list is treated as empty: {emptyLfsList.Message}");

            // 基线同步必须把基线块留在原位:gitattributes 是最后匹配生效,
            // 块被前移会让块外兜底的 * text=auto 落到块后面,反过来盖掉块内的 -text。
            await File.WriteAllTextAsync(
                Path.Combine(template, ".gitattributes"),
                "# HistoryJanus baseline begin\r\n" +
                "*.png filter=lfs diff=lfs merge=lfs -text\r\n" +
                "# HistoryJanus baseline end\r\n",
                new UTF8Encoding(true));
            var createdTarget = await projects.CreateAsync(Target, Template, null);
            True(createdTarget.Success, $"create sync target: {createdTarget.Message}");
            var target = Path.Combine(root, Target);
            await File.WriteAllTextAsync(
                Path.Combine(target, ".gitattributes"),
                "* text=auto\r\n" +
                "\r\n" +
                "# HistoryJanus baseline begin\r\n" +
                "*.stale filter=lfs diff=lfs merge=lfs -text\r\n" +
                "# HistoryJanus baseline end\r\n" +
                "\r\n" +
                "*.md text eol=lf\r\n",
                new UTF8Encoding(true));
            var synced = await service.SyncBaselineAsync(Target, apply: true, null);
            True(synced.Success, $"baseline sync applies to the target worktree: {synced.Message}");
            var syncedText = await File.ReadAllTextAsync(Path.Combine(target, ".gitattributes"));
            var fallbackAt = syncedText.IndexOf("* text=auto", StringComparison.Ordinal);
            var blockAt = syncedText.IndexOf("# HistoryJanus baseline begin", StringComparison.Ordinal);
            True(fallbackAt >= 0 && blockAt > fallbackAt,
                "baseline block stays below the hand-written * text=auto fallback");
            True(syncedText.Contains("*.png filter=lfs", StringComparison.Ordinal)
                 && !syncedText.Contains("*.stale", StringComparison.Ordinal),
                "baseline block content is refreshed from the template");
            True(syncedText.Contains("*.md text eol=lf", StringComparison.Ordinal),
                "hand-written content after the baseline block survives the sync");

            var headAfter = await GitRunner.RunAsync(worktree, ["rev-parse", "HEAD"]);
            Ensure(headAfter, "read final HEAD");
            Equal(headBefore.Output, headAfter.Output, "rule operations do not commit or rewrite history");

            const string realRoot = @"C:\OneHistory\HistoryClio";
            var realTemplate = Path.Combine(realRoot, "0000-000-Template");
            if (args.Contains("--real-template", StringComparer.OrdinalIgnoreCase)
                && Directory.Exists(realTemplate))
            {
                var realSettings = new MemorySettings();
                BindLibrary(realSettings, realRoot, "0000-000-Template");
                var realService = new GitFileRuleService(new ProjectService(realSettings, _ => false, root));
                var realAttributes = SnapshotOptional(Path.Combine(realTemplate, ".gitattributes"));
                var realIgnore = SnapshotOptional(Path.Combine(realTemplate, ".gitignore"));
                var templateRules = await realService.ListAsync("0000-000-Template");
                True(templateRules.Success, $"template readonly list: {templateRules.Message}");
                BytesEqual(realAttributes, SnapshotOptional(Path.Combine(realTemplate, ".gitattributes")),
                    "template attributes unchanged by list");
                BytesEqual(realIgnore, SnapshotOptional(Path.Combine(realTemplate, ".gitignore")),
                    "template ignore unchanged by list");
            }

        }
        finally
        {
            if (Directory.Exists(root))
                DeleteTree(root);
        }
    }

    private static byte[] OutsideManagedBytes(byte[] bytes)
    {
        var begin = Encoding.ASCII.GetBytes("# HistoryJanus managed begin");
        var end = Encoding.ASCII.GetBytes("# HistoryJanus managed end");
        var beginIndex = bytes.AsSpan().IndexOf(begin);
        if (beginIndex < 0)
            return bytes;
        var relativeEnd = bytes.AsSpan(beginIndex).IndexOf(end);
        if (relativeEnd < 0)
            return bytes;
        var endIndex = beginIndex + relativeEnd + end.Length;
        if (endIndex < bytes.Length && bytes[endIndex] == (byte)'\r')
            endIndex++;
        if (endIndex < bytes.Length && bytes[endIndex] == (byte)'\n')
            endIndex++;
        return [.. bytes.AsSpan(0, beginIndex), .. bytes.AsSpan(endIndex)];
    }

    private static byte[] SnapshotOptional(string path) => File.Exists(path) ? File.ReadAllBytes(path) : [];
}
