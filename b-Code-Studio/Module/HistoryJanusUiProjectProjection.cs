using System.Text.Json;
using HistoryJanus.Git;

namespace HistoryJanus.Module;

internal static partial class HistoryJanusUiCommands
{
    internal sealed record UiProjectRow(
        string Name,
        string ZFolders,
        string Subject,
        string Status,
        string LifecycleAction,
        string StatusSymbol,
        string IsClean,
        string Archived,
        string LifecycleState);

    internal static class UiProjectProjection
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public static string Serialize(IEnumerable<WorktreeInfo> projects)
            => JsonSerializer.Serialize(projects.Select(Project).ToList(), Options);

        public static string Serialize(object? data)
            => JsonSerializer.Serialize(Read(data), Options);

        public static IReadOnlyList<UiProjectRow> Read(object? data)
            => ReadWorktrees(data).Select(Project).ToList();

        public static IReadOnlyList<WorktreeInfo> ReadWorktrees(object? data)
        {
            if (data == null)
                return [];

            var json = data switch
            {
                string text => text,
                JsonElement element => element.GetRawText(),
                JsonDocument document => document.RootElement.GetRawText(),
                _ => JsonSerializer.Serialize(data, Options),
            };

            try
            {
                return JsonSerializer.Deserialize<List<WorktreeInfo>>(json, Options) ?? [];
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Janus 项目数据不是合法 WorktreeInfo 数组。", ex);
            }
        }

        /// <summary>
        /// 总览表格的行序：**最新的项目在最上面**。
        ///
        /// 项目名以「年份-序号」开头，因此按名字倒序就是按登记时间倒序；
        /// 业务侧 <c>janus.proj.list</c> 仍按名字正序返回——那是给人读清单用的，
        /// 与页面上「先看见最近在做的那几个」不是同一件事，两处各自成立。
        /// </summary>
        public static IReadOnlyList<WorktreeInfo> Newest(IEnumerable<WorktreeInfo> projects)
            => projects
                .OrderByDescending(project => project.BranchName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>
        /// 顶栏两个筛选器：搜索词匹配**项目名或任一 z 级文件夹名**，
        /// 年份匹配项目名开头的四位年份。两者求交；空搜索词与「全部」都表示不过滤。
        /// </summary>
        public static IReadOnlyList<WorktreeInfo> Filter(
            IEnumerable<WorktreeInfo> projects,
            string? query,
            string? year,
            string allYears)
        {
            var needle = query?.Trim() ?? "";
            var wanted = year?.Trim() ?? "";
            if (wanted.Length == 0 || wanted.Equals(allYears, StringComparison.Ordinal))
                wanted = "";

            return Newest(projects.Where(project =>
                (wanted.Length == 0 || YearOf(project).Equals(wanted, StringComparison.Ordinal)) &&
                (needle.Length == 0 || Matches(project, needle))));
        }

        private static bool Matches(WorktreeInfo project, string needle)
            => project.BranchName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               (project.ZFolders ?? []).Any(folder =>
                   folder.Contains(needle, StringComparison.OrdinalIgnoreCase));

        /// <summary>年份候选：「全部」加上现有项目里出现过的年份，新的在前。</summary>
        public static IReadOnlyList<IReadOnlyDictionary<string, string>> YearOptions(
            IEnumerable<WorktreeInfo> projects,
            string allYears)
            => new[] { allYears }
                .Concat(projects
                    .Select(YearOf)
                    .Where(year => year.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .OrderByDescending(year => year, StringComparer.Ordinal))
                .Select(value => (IReadOnlyDictionary<string, string>)
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = value })
                .ToList();

        /// <summary>项目名开头的四位年份；不是这个形状就返回空串，交给「全部」兜住。</summary>
        private static string YearOf(WorktreeInfo project)
        {
            var name = project.BranchName;
            return name.Length >= 5 && name[4] == '-' && name[..4].All(char.IsAsciiDigit)
                ? name[..4]
                : "";
        }

        private static UiProjectRow Project(WorktreeInfo project)
        {
            var lifecycleAction = project.LifecycleAction switch
            {
                "提交" or "推送" or "同步" or "归档" or "拉取" => project.LifecycleAction,
                _ => "同步",
            };

            return new(
                project.BranchName,
                project.ZFolderCount switch
                {
                    0 => "无目录",
                    1 => project.ZFolders?.FirstOrDefault(folder => folder.Length > 0) ?? "1 个",
                    _ => $"{project.ZFolderCount} 个",
                },
                project.LastCommitMessage,
                lifecycleAction,
                lifecycleAction,
                lifecycleAction switch
                {
                    "提交" => "●",
                    "推送" => "↑",
                    "归档" => "□",
                    "拉取" => "↓",
                    _ => "↔",
                },
                project.IsClean switch
                {
                    true => "干净",
                    false => "有修改",
                    null => "未知",
                },
                project.IsArchived ? "true" : "false",
                project.LifecycleState);
        }
    }
}
