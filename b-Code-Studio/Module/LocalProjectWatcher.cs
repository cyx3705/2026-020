using System.IO;

namespace HistoryJanus.Module;

/// <summary>
/// 监视项目库目录，把文件改动折算成「哪些项目变了」（5.16.2）。
///
/// 一个递归监视器盖住整个库，路径的第一段就是项目名。同一个项目的改动在
/// <see cref="Quiet"/> 内连续来，只算一次；停手后逐个交给回调，同一时刻只跑一个，
/// 回调期间又来的改动排到下一轮。监视缓冲溢出时说不清丢了哪些，就把见过的项目全排一遍——
/// 本地重读不 fetch，排一遍也不贵。
/// </summary>
internal sealed class LocalProjectWatcher : IDisposable
{
    /// <summary>同一项目停手多久才算改完：编译、解压一次写上千个文件，不能每个文件读一遍 git。</summary>
    internal static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(800);

    private readonly string _root;
    private readonly Func<string, Task> _changed;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private bool _running;
    private bool _disposed;

    public LocalProjectWatcher(string root, Func<string, Task> changed)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _changed = changed;
        _timer = new Timer(_ => Flush());
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += (_, e) => Note(e.FullPath);
        _watcher.Created += (_, e) => Note(e.FullPath);
        _watcher.Deleted += (_, e) => Note(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            Note(e.OldFullPath);
            Note(e.FullPath);
        };
        _watcher.Error += (_, _) => NoteAll();
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// 一个路径算不算「这个项目的本地状态可能变了」，算的话返回项目名。
    /// .git 里只认 HEAD、refs/heads 与 packed-refs；其余（index、objects、logs、锁、FETCH_HEAD）都是
    /// git 自己跑起来就会写的东西，算进来就会被本地重读自己触发。
    /// </summary>
    internal static string? ProjectOf(string root, string fullPath)
    {
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;
        var relative = fullPath[root.Length..];
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
        if (parts.Length >= 2 && parts[1].Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            var inside = parts.Skip(2).ToArray();
            var relevant = inside.Length switch
            {
                0 => false,
                1 => inside[0] is "HEAD" or "packed-refs",
                _ => inside[0] == "refs" && inside[1] == "heads" && !inside[^1].EndsWith(".lock", StringComparison.OrdinalIgnoreCase),
            };
            return relevant ? parts[0] : null;
        }
        return parts[0];
    }

    private void Note(string fullPath)
    {
        if (ProjectOf(_root, fullPath) is not { } name)
            return;
        lock (_gate)
        {
            if (_disposed)
                return;
            _seen.Add(name);
            _pending.Add(name);
            _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void NoteAll()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _pending.UnionWith(_seen);
            _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        string[] batch;
        lock (_gate)
        {
            if (_disposed || _running || _pending.Count == 0)
                return;
            _running = true;
            batch = _pending.ToArray();
            _pending.Clear();
        }

        _ = Task.Run(async () =>
        {
            foreach (var name in batch)
            {
                if (_disposed)
                    break;
                try
                {
                    await _changed(name).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 一个项目读坏了不影响别的项目；下一次改动再试。
                }
            }

            lock (_gate)
            {
                _running = false;
                if (!_disposed && _pending.Count > 0)
                    _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }
}
