using System.IO;
using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Logging;

namespace HistoryJanus.Git;

/// <summary>
/// 操作留痕与分支描述的文件存储：操作留痕使用 JSONL 追加，
/// 分支描述使用原子替换 JSON。记录失败仍只告警，不阻断主操作。
/// </summary>
/// <remarks>
/// 本类曾实现 <c>IMcpAuditLog</c>，把 MCP 调用留痕转交给宿主的 McpAuditRecorder。
/// 那条实现从来没有被接上：唯一的装配点是 Aurora 的 <c>ShellConfig.McpAuditLog</c>，
/// 而它全仓从未被赋值。MCP 随 Vulcan 4.4.0 迁往 HistoryPortunus 后，
/// 由那个模块自己的审计器落盘，Janus 不再需要转交任何东西。
/// </remarks>
public sealed class HistoryRecorder
{
    public const string TablePushHistory = "push_history";
    public const string TableBranchNotes = "branch_notes";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _pushPath;
    private readonly string _notesPath;
    private readonly IShellLog _log;
    private readonly object _pushGate = new();
    private readonly object _notesGate = new();

    public HistoryRecorder(string dataDirectory, IShellLog log)
    {
        var state = Path.Combine(dataDirectory, "state");
        _pushPath = Path.Combine(state, "push-history.jsonl");
        _notesPath = Path.Combine(state, "branch-notes.json");
        _log = log;
    }

    public void Record(string branch, string action, string message, string result, int warnings = 0)
    {
        try
        {
            var row = new PushRow(
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), branch, action,
                Truncate(message, MessageLimit), result, warnings);
            var line = JsonSerializer.Serialize(row) + Environment.NewLine;
            lock (_pushGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_pushPath)!);
                File.AppendAllText(_pushPath, line, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            _log.Warn("history", $"留痕写入失败(不影响操作本身): {ex.Message}");
        }
    }

    /// <summary>留痕单行的消息长度上限。</summary>
    private const int MessageLimit = 500;

    /// <summary>
    /// 截断到上限并补省略号。
    /// </summary>
    /// <remarks>
    /// 原先调用宿主的 <c>Core.Data.SqlText.Truncate</c>。那个类型随 SQL 文本拼接工具
    /// 一起从宿主删除（Vulcan 4.2.0）——留痕写的是 JSONL，本来就不该依赖 SQL 转义工具集，
    /// 只是当年顺手拿了同一个 Truncate。
    /// </remarks>
    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    public void SetNote(string branch, string note)
    {
        lock (_notesGate)
        {
            var notes = LoadNotes();
            notes[branch] = new NoteRow(note, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            SaveNotes(notes);
        }
    }

    public IReadOnlyDictionary<string, string> AllNotes()
    {
        lock (_notesGate)
        {
            try
            {
                return LoadNotes().ToDictionary(item => item.Key, item => item.Value.Note,
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _log.Warn("history", $"读取分支描述失败: {ex.Message}");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private Dictionary<string, NoteRow> LoadNotes()
    {
        if (!File.Exists(_notesPath))
            return new Dictionary<string, NoteRow>(StringComparer.OrdinalIgnoreCase);
        var stored = JsonSerializer.Deserialize<Dictionary<string, NoteRow>>(
                         File.ReadAllText(_notesPath), JsonOptions)
                     ?? new Dictionary<string, NoteRow>();
        return new Dictionary<string, NoteRow>(stored, StringComparer.OrdinalIgnoreCase);
    }

    private void SaveNotes(Dictionary<string, NoteRow> notes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_notesPath)!);
        var temp = _notesPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(notes, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, _notesPath, overwrite: true);
    }

    private sealed record PushRow(
        string Time, string Branch, string Action, string Message, string Result, int Warnings);

    private sealed record NoteRow(string Note, string Updated);
}
