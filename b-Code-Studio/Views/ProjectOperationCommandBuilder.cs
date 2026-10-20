using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Views;

public enum ProjectOperationMode
{
    Current,
    All,
}

public static class ProjectOperationCommandBuilder
{
    public static bool RequiresCurrentProject(ProjectOperationMode mode)
        => mode == ProjectOperationMode.Current;

    public static string BuildCommit(
        ProjectOperationMode mode, string? project, string message)
    {
        if (RequiresCurrentProject(mode))
        {
            if (string.IsNullOrWhiteSpace(project))
                throw new ArgumentException("当前模式必须选择项目", nameof(project));
            return $"janus.proj.commit name={CommandParser.QuoteArg(project.Trim())} " +
                   $"msg={CommandParser.QuoteArg(message.Trim())}";
        }
        return $"janus.proj.commitall msg={CommandParser.QuoteArg(message.Trim())}";
    }

    public static string BuildPush(ProjectOperationMode mode, string? project)
    {
        if (RequiresCurrentProject(mode))
        {
            if (string.IsNullOrWhiteSpace(project))
                throw new ArgumentException("当前模式必须选择项目", nameof(project));
            return $"janus.proj.push name={CommandParser.QuoteArg(project.Trim())}";
        }
        return "janus.proj.pushall";
    }

    /// <summary>排除清单是全库共用的设置，不带项目名。</summary>
    public static string Excludes(string list)
        => $"janus.gitrule.excludes list={CommandParser.QuoteArg(list.Trim())}";
}
