namespace HistoryJanus.Views;

/// <summary>进程内共享的当前项目选择，只传递稳定的项目名。</summary>
public sealed class ProjectSelectionState
{
    private string? _currentProjectName;

    public string? CurrentProjectName
    {
        get => _currentProjectName;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_currentProjectName, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _currentProjectName = normalized;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? Changed;
}
