using System.IO;
using System.Text;
using System.Text.Json;

namespace HistoryJanus.Git;

public sealed record ProjectLifecycleRecord(
    string ProjectName,
    string Remote,
    string Branch,
    string VerifiedSha,
    IReadOnlyList<string> ZFolders,
    DateTimeOffset? ArchivedAt);

internal sealed class ProjectLifecycleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public ProjectLifecycleStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "state", "project-lifecycle.json");
    }

    public IReadOnlyList<ProjectLifecycleRecord> All()
    {
        lock (_gate)
            return Load().Values.OrderBy(item => item.ProjectName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public ProjectLifecycleRecord? Get(string projectName)
    {
        lock (_gate)
            return Load().GetValueOrDefault(projectName.Trim());
    }

    public void Save(ProjectLifecycleRecord record)
    {
        lock (_gate)
        {
            var records = Load();
            records[record.ProjectName] = record;
            Write(records);
        }
    }

    public void Rename(string currentName, string newName)
    {
        lock (_gate)
        {
            var records = Load();
            if (!records.Remove(currentName, out var record))
                return;
            records[newName] = record with { ProjectName = newName };
            Write(records);
        }
    }

    public void Remove(string projectName)
    {
        lock (_gate)
        {
            var records = Load();
            if (records.Remove(projectName))
                Write(records);
        }
    }

    private Dictionary<string, ProjectLifecycleRecord> Load()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, ProjectLifecycleRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = JsonSerializer.Deserialize<List<ProjectLifecycleRecord>>(
                           File.ReadAllText(_path), JsonOptions) ?? [];
            return rows.ToDictionary(item => item.ProjectName, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"项目生命周期记录损坏: {ex.Message}", ex);
        }
    }

    private void Write(Dictionary<string, ProjectLifecycleRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(
            records.Values.OrderBy(item => item.ProjectName, StringComparer.OrdinalIgnoreCase),
            JsonOptions), new UTF8Encoding(false));
        File.Move(temp, _path, overwrite: true);
    }
}
