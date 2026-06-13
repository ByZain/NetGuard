using System.Diagnostics;
using System.Text;

namespace NetClean;

internal static class ProcessTerminator
{
    public static ProcessStopResult TryClose(int pid, bool force)
    {
        if (pid == Environment.ProcessId)
        {
            return ProcessStopResult.Fail("不能关闭 NetClean 自己。");
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return ProcessStopResult.Success("该程序已经退出。");
            }

            if (SystemProcessClassifier.IsCriticalProcess(process))
            {
                return ProcessStopResult.Fail("这是系统关键进程，已阻止关闭。");
            }

            if (!force && process.MainWindowHandle != IntPtr.Zero)
            {
                process.CloseMainWindow();
                if (process.WaitForExit(1800))
                {
                    return ProcessStopResult.Success("已发送关闭请求，程序已经退出。");
                }

                return ProcessStopResult.NeedsForce("已发送普通关闭请求，但程序没有及时退出。可以再点“强制结束”。");
            }

            return KillProcessTree(pid);
        }
        catch (ArgumentException)
        {
            return ProcessStopResult.Success("该程序已经退出。");
        }
        catch (Exception ex)
        {
            return ProcessStopResult.Fail($"关闭失败：{ex.Message}");
        }
    }

    public static ProcessStopResult TryDeepClose(int pid)
    {
        if (pid == Environment.ProcessId)
        {
            return ProcessStopResult.Fail("不能关闭 NetClean 自己。");
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return ProcessStopResult.Success("该程序已经退出。");
            }

            if (SystemProcessClassifier.IsCriticalProcess(process))
            {
                return ProcessStopResult.Fail("这是系统关键进程，已阻止深度关闭。");
            }
        }
        catch (ArgumentException)
        {
            return ProcessStopResult.Success("该程序已经退出。");
        }
        catch (Exception ex)
        {
            return ProcessStopResult.Fail($"读取进程失败：{ex.Message}");
        }

        var report = new StringBuilder();
        var hadServiceFailure = false;
        IReadOnlyList<WindowsServiceInfo> services = [];

        try
        {
            services = WindowsServiceManager.GetServicesByProcessId(pid);
        }
        catch (Exception ex)
        {
            hadServiceFailure = true;
            report.AppendLine($"读取关联服务失败：{ex.Message}");
        }

        if (services.Count == 0)
        {
            report.AppendLine("没有找到由该进程承载的 Windows 服务。");
        }
        else
        {
            report.AppendLine("已尝试停止关联服务：");
            foreach (var service in services)
            {
                try
                {
                    var result = WindowsServiceManager.StopService(service.Name, TimeSpan.FromSeconds(8));
                    hadServiceFailure |= !result.Succeeded;
                    report.AppendLine($"- {service.DisplayName} ({service.Name})：{result.Message}");
                }
                catch (Exception ex)
                {
                    hadServiceFailure = true;
                    report.AppendLine($"- {service.DisplayName} ({service.Name})：停止失败：{ex.Message}");
                }
            }
        }

        Thread.Sleep(500);
        var killResult = KillProcessTree(pid);
        report.AppendLine(killResult.Message);

        if (killResult.Status == ProcessStopStatus.Failed)
        {
            return ProcessStopResult.Fail(report.ToString().Trim());
        }

        if (hadServiceFailure)
        {
            report.AppendLine("部分服务没有成功停止。如果程序稍后又出现，可能还有计划任务或自启动项在拉起它。");
            return ProcessStopResult.NeedsForce(report.ToString().Trim());
        }

        if (services.Count == 0)
        {
            report.AppendLine("如果程序稍后又出现，可能不是服务拉起，而是计划任务、自启动项或另一个守护进程。");
        }

        return ProcessStopResult.Success(report.ToString().Trim());
    }

    private static ProcessStopResult KillProcessTree(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return ProcessStopResult.Success("该程序已经退出。");
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(2500);
            return ProcessStopResult.Success("已结束残留进程树。");
        }
        catch (ArgumentException)
        {
            return ProcessStopResult.Success("该程序已经退出。");
        }
        catch (Exception ex)
        {
            return ProcessStopResult.Fail($"结束残留进程失败：{ex.Message}");
        }
    }
}

internal enum ProcessStopStatus
{
    Success,
    NeedsForce,
    Failed
}

internal sealed record ProcessStopResult(ProcessStopStatus Status, string Message)
{
    public static ProcessStopResult Success(string message) => new(ProcessStopStatus.Success, message);
    public static ProcessStopResult NeedsForce(string message) => new(ProcessStopStatus.NeedsForce, message);
    public static ProcessStopResult Fail(string message) => new(ProcessStopStatus.Failed, message);
}
