using System.Diagnostics;

namespace NetClean;

internal static class SystemProcessClassifier
{
    private static readonly HashSet<string> CriticalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle",
        "System",
        "Registry",
        "smss",
        "csrss",
        "wininit",
        "winlogon",
        "services",
        "lsass",
        "svchost",
        "fontdrvhost",
        "dwm"
    };

    public static ProcessClassification Classify(Process process, string path)
    {
        var processName = SafeReadProcessName(process);
        return Classify(process.Id, processName, path, TryReadSessionId(process));
    }

    public static ProcessClassification Classify(int pid, string processName, string path, int? sessionId = null)
    {
        if (IsCriticalProcess(pid, processName))
        {
            return new ProcessClassification("系统关键", IsSystemProcess: true, IsCriticalSystemProcess: true);
        }

        if (sessionId == 0)
        {
            return new ProcessClassification("系统服务", IsSystemProcess: true, IsCriticalSystemProcess: false);
        }

        if (IsWindowsComponent(path))
        {
            return new ProcessClassification("系统组件", IsSystemProcess: true, IsCriticalSystemProcess: false);
        }

        return new ProcessClassification("普通", IsSystemProcess: false, IsCriticalSystemProcess: false);
    }

    public static bool IsCriticalProcess(Process process)
    {
        return IsCriticalProcess(process.Id, SafeReadProcessName(process));
    }

    public static bool IsCriticalProcess(int pid, string processName)
    {
        return pid <= 4 || CriticalProcessNames.Contains(processName);
    }

    private static string SafeReadProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private static int? TryReadSessionId(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWindowsComponent(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsPath))
        {
            return false;
        }

        var normalizedWindowsPath = Path.TrimEndingDirectorySeparator(windowsPath) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedWindowsPath, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record ProcessClassification(
    string Tag,
    bool IsSystemProcess,
    bool IsCriticalSystemProcess);
