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

        public static string Serialize(object? data)
            => JsonSerializer.Serialize(Read(data), Options);

        public static IReadOnlyList<UiProjectRow> Read(object? data)
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
                var projects = JsonSerializer.Deserialize<List<WorktreeInfo>>(json, Options) ?? [];
                return projects.Select(Project).ToList();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Janus 项目数据不是合法 WorktreeInfo 数组。", ex);
            }
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
