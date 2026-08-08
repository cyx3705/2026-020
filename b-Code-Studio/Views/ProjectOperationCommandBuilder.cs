using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Views;

public enum ProjectOperationMode
{
    CurrentSubmodules,
    CurrentBoth,
    AllSubmodules,
    AllBoth,
}

public static class ProjectOperationCommandBuilder
{
    public static bool RequiresCurrentProject(ProjectOperationMode mode)
        => mode is ProjectOperationMode.CurrentSubmodules or ProjectOperationMode.CurrentBoth;

    public static string BuildCommit(
        ProjectOperationMode mode, string? project, string message)
    {
        var target = Target(mode);
        if (RequiresCurrentProject(mode))
        {
            if (string.IsNullOrWhiteSpace(project))
                throw new ArgumentException("当前模式必须选择项目", nameof(project));
            return $"janus.proj.commit name={CommandParser.QuoteArg(project.Trim())} " +
                   $"msg={CommandParser.QuoteArg(message.Trim())} target={target}";
        }
        return $"janus.proj.commitall msg={CommandParser.QuoteArg(message.Trim())} target={target}";
    }

    public static string BuildPush(ProjectOperationMode mode, string? project)
    {
        var target = Target(mode);
        if (RequiresCurrentProject(mode))
        {
            if (string.IsNullOrWhiteSpace(project))
                throw new ArgumentException("当前模式必须选择项目", nameof(project));
            return $"janus.proj.push name={CommandParser.QuoteArg(project.Trim())} target={target}";
        }
        return $"janus.proj.pushall target={target}";
    }

    private static string Target(ProjectOperationMode mode)
        => mode is ProjectOperationMode.CurrentSubmodules or ProjectOperationMode.AllSubmodules
            ? "submodules"
            : "both";
}
