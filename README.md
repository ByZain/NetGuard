# NetGuard

NetGuard is a small self-contained Windows GUI tool for finding background traffic hogs. It shows live per-process upload and download usage, marks system and upload-limited processes, applies per-app upload limits through Windows QoS, and deep-closes stubborn service-backed processes by stopping related services before killing the process tree.

It was built for the very practical case where a game starts lagging because some background app is quietly saturating upload bandwidth.

## Features

- Live per-process upload and download traffic.
- Sortable process table.
- System process markings to reduce accidental damage.
- Process status markings such as running or exited.
- Upload-limit markings for apps already limited by NetGuard.
- Per-application upload limiting via Windows Policy-based QoS.
- Deep close: stop related Windows services first, then kill the remaining process tree.
- Related-process hints after deep close, such as same-directory or same-company processes that are still running.
- Self-contained `win-x64` single-file publish. Target machines do not need to install the .NET runtime.

## Requirements

- Windows 10/11 x64.
- Administrator permission at runtime.
- .NET SDK 9 to build from source.

NetGuard needs administrator permission because it reads Windows kernel network events and manages Windows QoS/service state.

## Build

```powershell
.\publish.ps1
```

Output:

```text
.\publish\NetGuard.exe
```

The published executable is self-contained, so it can be copied to another Windows x64 machine and run directly.

## How It Works

### Live Traffic Monitoring

`NetworkMonitor` uses `Microsoft.Diagnostics.Tracing.TraceEvent` to start a kernel ETW session with the `NetworkTCPIP` keyword enabled.

It listens to TCP and UDP send/receive events:

- `TcpIpSend`
- `TcpIpSendIPV6`
- `TcpIpRetransmit`
- `TcpIpRetransmitIPV6`
- `UdpIpSend`
- `UdpIpSendIPV6`
- matching receive events for download rate

Each network event contains a process ID and byte count. NetGuard accumulates bytes per PID in a short time window, then converts that into upload/download bytes per second for the UI.

The important startup detail is that kernel provider enabling must happen before accessing `session.Source.Kernel`; otherwise TraceEvent throws:

```text
Kernel sessions must be started (EnableKernelProvider called) before accessing the source.
```

So the monitor starts the kernel provider first, then attaches event callbacks.

### Process Metadata And System Marking

`ProcessInfoCache` maps process IDs to display names, executable paths, and classification metadata.

`SystemProcessClassifier` marks processes as:

- `系统关键`
- `系统服务`
- `系统组件`
- `普通`

Critical system processes such as `System`, `lsass`, `winlogon`, `services`, and `svchost` are protected from deep close.

### Deep Close

Simple process killing is often not enough. Some apps are relaunched by a Windows service, updater, scheduled task, or companion process.

`ProcessTerminator.TryDeepClose` handles the service-backed case:

1. Check that the process is not NetGuard itself.
2. Block critical system processes.
3. Ask `WindowsServiceManager` for Windows services hosted by the selected PID.
4. Stop those services through the Service Control Manager.
5. Kill the remaining process tree with `Process.Kill(entireProcessTree: true)`.
6. Report service stop failures, if any.

`WindowsServiceManager` uses native Windows Service Control Manager APIs via P/Invoke instead of parsing localized `sc.exe` output. This keeps the logic stable across Windows languages.

### Related Process Hints

After deep close, `ProcessInspector` scans running processes for likely related processes:

- same executable directory
- same file-company metadata

These are shown as hints only. NetGuard does not automatically close related processes, because doing that too aggressively can surprise users.

### Upload Limiting

`UploadLimiter` uses Windows Policy-based QoS through the built-in PowerShell `NetQos` module.

For the selected executable, NetGuard creates an ActiveStore QoS policy. The QoS application match uses the executable file name, while NetGuard keeps a full-path hash in the policy name so it can find and remove the policy later:

```powershell
New-NetQosPolicy `
  -Name NetGuard_<hash>_<rate>KBps `
  -AppPathNameMatchCondition <app.exe> `
  -ThrottleRateActionBitsPerSecond <bits-per-second> `
  -PolicyStore ActiveStore
```

The policy is stored in `ActiveStore`, so it is temporary and disappears after reboot. Users can also remove it with `取消限速`.

Windows QoS is policy-based, not a per-socket packet shaper. In practice it is most reliable for new connections, so if an app is already uploading heavily, apply the limit, deep-close the app, and then start it again.

NetGuard scans current `NetGuard_*` and legacy `NetClean_*` policies so limited apps are marked in the table even after the app process exits and starts again.

## Project Layout

```text
NetClean/
  MainForm.cs                 WinForms UI
  NetworkMonitor.cs           ETW-based traffic monitor
  ProcessInfoCache.cs         PID metadata cache
  SystemProcessClassifier.cs  System process classification
  ProcessTerminator.cs        Deep-close orchestration
  WindowsServiceManager.cs    Native Windows service control
  UploadLimiter.cs            Windows QoS upload limiting
  ProcessInspector.cs         Run-state and related-process hints
  LimitUploadDialog.cs        Upload limit input dialog
publish.ps1                   Self-contained single-file publish script
```

The project folder is still named `NetClean` from the initial prototype, but the product name and executable are now `NetGuard`.

## Notes

- SmartScreen may warn on unsigned builds. That is expected for a newly built unsigned executable.
- QoS limiting is Windows policy-based and may not behave exactly like commercial per-socket traffic shapers.
- Deep close intentionally avoids disabling services permanently. It stops services for the current session, but it does not change startup type.
