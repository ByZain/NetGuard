using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NetClean;

internal static class WindowsServiceManager
{
    private const int ScEnumProcessInfo = 0;
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceWin32 = 0x00000030;
    private const uint ServiceStateAll = 0x00000003;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStopPending = 0x00000003;

    public static IReadOnlyList<WindowsServiceInfo> GetServicesByProcessId(int pid)
    {
        return EnumerateServices()
            .Where(service => service.ProcessId == pid)
            .OrderBy(service => service.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static ServiceStopResult StopService(string serviceName, TimeSpan timeout)
    {
        using var serviceHandle = OpenServiceForStop(serviceName);
        var status = QueryStatus(serviceHandle.DangerousGetHandle());

        if (status.CurrentState == ServiceStopped)
        {
            return ServiceStopResult.Success(serviceName, "服务已经停止。");
        }

        if ((status.ControlsAccepted & ServiceAcceptStop) == 0)
        {
            return ServiceStopResult.Failed(serviceName, "服务不接受停止请求。");
        }

        var simpleStatus = new ServiceStatus();
        if (!ControlService(serviceHandle.DangerousGetHandle(), ServiceControlStop, ref simpleStatus))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1062)
            {
                return ServiceStopResult.Failed(serviceName, new Win32Exception(error).Message);
            }
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(300);
            status = QueryStatus(serviceHandle.DangerousGetHandle());
            if (status.CurrentState == ServiceStopped)
            {
                return ServiceStopResult.Success(serviceName, "服务已停止。");
            }

            if (status.CurrentState != ServiceStopPending)
            {
                break;
            }
        }

        return ServiceStopResult.Failed(serviceName, "等待服务停止超时。");
    }

    private static IReadOnlyList<WindowsServiceInfo> EnumerateServices()
    {
        using var managerHandle = OpenServiceManager();
        uint bytesNeeded = 0;
        uint servicesReturned = 0;
        uint resumeHandle = 0;

        EnumServicesStatusEx(
            managerHandle.DangerousGetHandle(),
            ScEnumProcessInfo,
            ServiceWin32,
            ServiceStateAll,
            IntPtr.Zero,
            0,
            out bytesNeeded,
            out servicesReturned,
            ref resumeHandle,
            null);

        var lastError = Marshal.GetLastWin32Error();
        if (bytesNeeded == 0 && lastError != 234)
        {
            throw new Win32Exception(lastError);
        }

        var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
        try
        {
            resumeHandle = 0;
            if (!EnumServicesStatusEx(
                managerHandle.DangerousGetHandle(),
                ScEnumProcessInfo,
                ServiceWin32,
                ServiceStateAll,
                buffer,
                bytesNeeded,
                out bytesNeeded,
                out servicesReturned,
                ref resumeHandle,
                null))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var services = new List<WindowsServiceInfo>((int)servicesReturned);
            var itemSize = Marshal.SizeOf<EnumServiceStatusProcess>();
            for (var i = 0; i < servicesReturned; i++)
            {
                var itemPtr = IntPtr.Add(buffer, i * itemSize);
                var item = Marshal.PtrToStructure<EnumServiceStatusProcess>(itemPtr);
                services.Add(new WindowsServiceInfo(
                    item.ServiceName,
                    item.DisplayName,
                    item.ServiceStatus.ProcessId,
                    ToServiceStateText(item.ServiceStatus.CurrentState),
                    (item.ServiceStatus.ControlsAccepted & ServiceAcceptStop) != 0));
            }

            return services;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SafeServiceHandle OpenServiceManager()
    {
        var handle = OpenSCManager(null, null, ScManagerConnect | ScManagerEnumerateService);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return handle;
    }

    private static SafeServiceHandle OpenServiceForStop(string serviceName)
    {
        using var managerHandle = OpenServiceManager();
        var handle = OpenService(managerHandle.DangerousGetHandle(), serviceName, ServiceQueryStatus | ServiceStop);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return handle;
    }

    private static ServiceStatusProcess QueryStatus(IntPtr serviceHandle)
    {
        var bufferSize = Marshal.SizeOf<ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (!QueryServiceStatusEx(serviceHandle, 0, buffer, (uint)bufferSize, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ToServiceStateText(uint state)
    {
        return state switch
        {
            1 => "已停止",
            2 => "正在启动",
            3 => "正在停止",
            4 => "正在运行",
            5 => "继续待定",
            6 => "暂停待定",
            7 => "已暂停",
            _ => $"状态 {state}"
        };
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeServiceHandle OpenService(IntPtr serviceManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumServicesStatusEx(
        IntPtr serviceManager,
        int infoLevel,
        uint serviceType,
        uint serviceState,
        IntPtr services,
        uint bufferSize,
        out uint bytesNeeded,
        out uint servicesReturned,
        ref uint resumeHandle,
        string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    private sealed class SafeServiceHandle : SafeHandle
    {
        public SafeServiceHandle()
            : base(IntPtr.Zero, ownsHandle: true)
        {
        }

        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

        protected override bool ReleaseHandle()
        {
            return CloseServiceHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EnumServiceStatusProcess
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ServiceName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string DisplayName;

        public ServiceStatusProcess ServiceStatus;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public int ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }
}

internal sealed record WindowsServiceInfo(
    string Name,
    string DisplayName,
    int ProcessId,
    string State,
    bool CanStop);

internal sealed record ServiceStopResult(
    string ServiceName,
    bool Succeeded,
    string Message)
{
    public static ServiceStopResult Success(string serviceName, string message) => new(serviceName, true, message);
    public static ServiceStopResult Failed(string serviceName, string message) => new(serviceName, false, message);
}
