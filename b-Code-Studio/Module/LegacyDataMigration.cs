using System.IO;
using HistoryVulcan.Core.Logging;

namespace HistoryJanus.Module;

/// <summary>
/// 5.13.0 一次性搬迁：5.12.x 以前数据写在包槽位的 <c>data/</c>（<c>Modules\HistoryJanus\data</c>），
/// 宿主 5.9.0 起数据目录是槽位之外的 <c>ModuleData\HistoryJanus</c>。
/// </summary>
/// <remarks>
/// 只在新目录为空时复制一次，旧目录原样保留（宿主 6.0.0 前仍会在装包时保留它，作为回退依据）。
/// 旧位置由新位置推出：两者同在宿主数据根下，这是唯一一处仍知道旧布局的代码，6.0.0 后删除。
/// </remarks>
internal static class LegacyDataMigration
{
    internal static void CopyOnce(string dataDirectory, IShellLog log)
    {
        var hostRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(dataDirectory)));
        if (hostRoot == null)
            return;
        var legacy = Path.Combine(hostRoot, "Modules", "HistoryJanus", "data");
        if (!Directory.Exists(legacy) || Directory.EnumerateFileSystemEntries(dataDirectory).Any())
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dataDirectory, Path.GetRelativePath(legacy, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: false);
            }

            log.Log(ShellLogLevel.Info, "janus", $"已把旧数据从 {legacy} 复制到 {dataDirectory}（旧目录保留）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Log(ShellLogLevel.Warn, "janus", $"旧数据复制未完成，继续使用新目录：{ex.Message}");
        }
    }
}
