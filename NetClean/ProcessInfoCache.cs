using System.Diagnostics;

namespace NetClean;

internal sealed class ProcessInfoCache
{
    private readonly Dictionary<int, CachedProcessInfo> _cache = [];
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(10);

    public CachedProcessInfo Get(int pid, string fallbackName)
    {
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(pid, out var cached) && now - cached.CachedAt < _ttl)
        {
            return cached;
        }

        var info = ReadProcessInfo(pid, fallbackName, now);
        _cache[pid] = info;
        return info;
    }

    public void TrimMissingProcesses()
    {
        var deadPids = _cache.Keys.Where(pid =>
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }).ToArray();

        foreach (var pid in deadPids)
        {
            _cache.Remove(pid);
        }
    }

    private static CachedProcessInfo ReadProcessInfo(int pid, string fallbackName, DateTime now)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var name = string.IsNullOrWhiteSpace(process.ProcessName) ? fallbackName : process.ProcessName;
            var path = TryReadPath(process);
            var description = TryReadDescription(path);
            var displayName = string.IsNullOrWhiteSpace(description) ? name : description;
            var classification = SystemProcessClassifier.Classify(process, path);

            return new CachedProcessInfo(
                pid,
                string.IsNullOrWhiteSpace(name) ? $"PID {pid}" : name,
                displayName,
                path,
                classification.Tag,
                classification.IsSystemProcess,
                classification.IsCriticalSystemProcess,
                now);
        }
        catch
        {
            var name = string.IsNullOrWhiteSpace(fallbackName) ? $"PID {pid}" : fallbackName;
            var classification = SystemProcessClassifier.Classify(pid, name, "");
            return new CachedProcessInfo(
                pid,
                name,
                name,
                "",
                classification.Tag,
                classification.IsSystemProcess,
                classification.IsCriticalSystemProcess,
                now);
        }
    }

    private static string TryReadPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string TryReadDescription(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(info.FileDescription) ? "" : info.FileDescription;
        }
        catch
        {
            return "";
        }
    }
}

internal sealed record CachedProcessInfo(
    int ProcessId,
    string ProcessName,
    string DisplayName,
    string Path,
    string ProcessTag,
    bool IsSystemProcess,
    bool IsCriticalSystemProcess,
    DateTime CachedAt);
