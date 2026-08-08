using System.IO;
using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Data;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Services.Mcp;

namespace HistoryJanus.Git;

/// <summary>
/// 操作留痕与分支描述的文件存储：操作和 MCP 留痕使用 JSONL 追加，
/// 分支描述使用原子替换 JSON。记录失败仍只告警，不阻断主操作。
/// </summary>
public sealed class HistoryRecorder : IMcpAuditLog
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
    private readonly McpAuditRecorder _mcp;
    private readonly object _pushGate = new();
    private readonly object _notesGate = new();

    public HistoryRecorder(string dataDirectory, IShellLog log)
    {
        var state = Path.Combine(dataDirectory, "state");
        _pushPath = Path.Combine(state, "push-history.jsonl");
        _notesPath = Path.Combine(state, "branch-notes.json");
        _log = log;
        _mcp = new McpAuditRecorder(dataDirectory, log);
    }

    public void Record(string branch, string action, string message, string result, int warnings = 0)
    {
        try
        {
            var row = new PushRow(
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), branch, action,
                SqlText.Truncate(message, 500), result, warnings);
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

    public void RecordMcp(string client, string tool, string arguments, string result, long elapsedMs)
        => _mcp.RecordMcp(client, tool, arguments, result, elapsedMs);

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
