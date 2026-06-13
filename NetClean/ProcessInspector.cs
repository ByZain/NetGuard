using System.Diagnostics;
using System.Text;

namespace NetClean;

internal static class ProcessInspector
{
    public static ProcessRunState GetRunState(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited
                ? new ProcessRunState("已退出", IsRunning: false)
                : new ProcessRunState("运行中", IsRunning: true);
        }
        catch
        {
            return new ProcessRunState("已退出", IsRunning: false);
        }
    }

    public static IReadOnlyList<RelatedProcessInfo> FindRelatedProcesses(int originalPid, string originalPath, int maxCount = 12)
    {
        if (string.IsNullOrWhiteSpace(originalPath))
        {
            return [];
        }

        var originalDirectory = Path.GetDirectoryName(originalPath) ?? "";
        var originalCompany = TryReadCompany(originalPath);
        var related = new List<RelatedProcessInfo>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == originalPid || process.Id == Environment.ProcessId)
                {
                    continue;
                }

                if (SystemProcessClassifier.IsCriticalProcess(process))
                {
                    continue;
                }

                var path = TryReadPath(process);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var reasons = new List<string>();
                var directory = Path.GetDirectoryName(path) ?? "";
                if (!string.IsNullOrWhiteSpace(originalDirectory)
                    && string.Equals(directory, originalDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    reasons.Add("同目录");
                }

                var company = TryReadCompany(path);
                if (!string.IsNullOrWhiteSpace(originalCompany)
                    && string.Equals(company, originalCompany, StringComparison.OrdinalIgnoreCase))
                {
                    reasons.Add("同厂商");
                }

                if (reasons.Count == 0)
                {
                    continue;
                }

                var classification = SystemProcessClassifier.Classify(process, path);
                related.Add(new RelatedProcessInfo(
                    process.Id,
                    SafeReadProcessName(process),
                    path,
                    string.Join("、", reasons.Distinct()),
                    classification.Tag));
            }
        }

        return related
            .OrderBy(item => item.Reason, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxCount)
            .ToArray();
    }

    public static string FormatRelatedProcesses(IReadOnlyList<RelatedProcessInfo> related)
    {
        if (related.Count == 0)
        {
            return "";
        }

        var builder = new StringBuilder();
        builder.AppendLine("仍在运行的相关进程（仅提示，未自动关闭）：");
        foreach (var item in related)
        {
            builder.AppendLine($"- {item.ProcessName}，PID {item.ProcessId}，{item.Reason}，标记：{item.ProcessTag}");
        }

        return builder.ToString().TrimEnd();
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

    private static string TryReadCompany(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).CompanyName ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string SafeReadProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return "未知进程";
        }
    }
}

internal sealed record ProcessRunState(string Status, bool IsRunning);

internal sealed record RelatedProcessInfo(
    int ProcessId,
    string ProcessName,
    string Path,
    string Reason,
    string ProcessTag);
