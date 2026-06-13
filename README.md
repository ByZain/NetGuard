# NetGuard

NetGuard 是一个轻量级 Windows 图形工具，用来发现后台偷偷占用带宽的程序。它可以按进程查看实时上传/下载流量，标记系统进程和已限速进程，支持通过 Windows QoS 给指定程序限制上传速度，也支持“深度关闭”那些由服务反复拉起的后台上传进程。

这个工具最初是为一个很朴素的场景写的：打游戏时突然很卡，结果发现有后台软件偷偷上传，把上行带宽占满了。

## 功能

- 按进程显示实时上传和下载速度。
- 点击表格标题即可排序。
- 标记系统关键进程、系统服务、系统组件和普通进程，减少误操作。
- 标记进程状态，例如运行中或已退出。
- 标记已经被 NetGuard 限制上传速度的程序。
- 通过 Windows Policy-based QoS 给指定程序设置上传限速。
- 深度关闭：先停止选中进程对应的 Windows 服务，再结束残留进程树。
- 深度关闭后提示仍在运行的相关进程，例如同目录或同厂商进程。
- 发布为 `win-x64` 自包含单文件 exe，目标电脑不需要安装 .NET 运行时。

## 系统要求

- Windows 10/11 x64。
- 运行时需要管理员权限。
- 从源码构建需要 .NET SDK 9。

NetGuard 需要管理员权限，是因为它要读取 Windows 内核网络事件，并且会管理 Windows QoS 策略和服务状态。

## 构建

```powershell
.\publish.ps1
```

构建产物：

```text
.\publish\NetGuard.exe
```

生成的 `NetGuard.exe` 是自包含单文件，可以直接复制到另一台 Windows x64 电脑上运行。

## 使用建议

1. 运行 `NetGuard.exe`。
2. 同意管理员权限提示。
3. 按“上传速度”排序，找到正在大量上传的可疑程序。
4. 如果只是想压住上传，点击“限制上传”，输入上限，例如 `128 KB/s`。
5. 如果程序会偷偷复活，点击“深度关闭”。
6. 如果设置限速后流量没有立刻下降，建议先对该程序执行“深度关闭”，再重新打开它。

## 运行原理

### 实时流量监控

`NetworkMonitor` 使用 `Microsoft.Diagnostics.Tracing.TraceEvent` 启动 Windows 内核 ETW 会话，并启用 `NetworkTCPIP` 关键字。

它监听 TCP 和 UDP 的发送/接收事件，包括：

- `TcpIpSend`
- `TcpIpSendIPV6`
- `TcpIpRetransmit`
- `TcpIpRetransmitIPV6`
- `UdpIpSend`
- `UdpIpSendIPV6`
- 对应的接收事件，用于计算下载速度

这些网络事件里包含进程 ID 和字节数。NetGuard 按 PID 累加短时间窗口内的字节数，再换算成每秒上传/下载速度，显示到界面上。

有一个容易踩坑的细节：TraceEvent 的内核 provider 必须先启用，再访问 `session.Source.Kernel` 注册回调。否则会报错：

```text
Kernel sessions must be started (EnableKernelProvider called) before accessing the source.
```

所以代码里会先调用 `EnableKernelProvider(...)`，再绑定 TCP/UDP 事件回调。

### 进程信息和系统进程标记

`ProcessInfoCache` 会把 PID 映射成程序名、显示名、可执行文件路径和分类信息。

`SystemProcessClassifier` 会把进程分为：

- `系统关键`
- `系统服务`
- `系统组件`
- `普通`

对于 `System`、`lsass`、`winlogon`、`services`、`svchost` 等关键系统进程，NetGuard 会阻止深度关闭，避免误伤系统。

### 深度关闭

很多后台上传进程并不是单独存在的。它们可能由 Windows 服务、更新器、计划任务或守护进程拉起。如果只是杀掉当前 PID，过一会儿它可能又会出现。

`ProcessTerminator.TryDeepClose` 处理的是“由服务承载或拉起”的情况：

1. 检查目标进程不是 NetGuard 自己。
2. 阻止关闭系统关键进程。
3. 通过 `WindowsServiceManager` 查询当前 PID 承载的 Windows 服务。
4. 先通过 Windows Service Control Manager 停止这些服务。
5. 再用 `Process.Kill(entireProcessTree: true)` 结束残留进程树。
6. 把服务停止失败、进程结束失败等结果反馈给用户。

`WindowsServiceManager` 使用 Windows 原生 Service Control Manager API，也就是 P/Invoke 调用系统接口，而不是解析 `sc.exe` 的文本输出。这样不会受系统语言影响。

### 相关进程提示

深度关闭后，`ProcessInspector` 会扫描仍在运行的进程，提示一些可能相关的进程：

- 和目标程序在同一个目录。
- 文件版本信息里厂商相同。

这些只是提示，不会自动关闭。原因是“同厂商”或“同目录”不一定代表一定可以安全关闭，自动全关容易误伤。

### 上传限速

`UploadLimiter` 使用 Windows 自带 PowerShell `NetQos` 模块创建 Policy-based QoS 策略。

对选中的程序，NetGuard 会创建一个 ActiveStore QoS 策略，大致等价于：

```powershell
New-NetQosPolicy `
  -Name NetGuard_<hash>_<rate>KBps `
  -AppPathNameMatchCondition <app.exe> `
  -ThrottleRateActionBitsPerSecond <bits-per-second> `
  -PolicyStore ActiveStore
```

这里有几个设计点：

- QoS 的应用匹配使用 exe 文件名，例如 `Thunder.exe`。
- 策略名里保留完整路径的 hash，方便之后找到并删除对应策略。
- 策略保存在 `ActiveStore`，重启后会失效。
- 用户也可以点击“取消限速”手动移除策略。

需要注意：Windows QoS 是策略型限速，不是商业流量控制软件那种逐 socket 抓包限速。它对新建连接更稳定。如果某个程序已经在大量上传，设置限速后可能不会马上掉速。建议流程是：

1. 设置上传限速。
2. 确认表格“限速”列出现标记。
3. 对目标程序执行“深度关闭”。
4. 重新打开目标程序，让它建立新连接。

NetGuard 会扫描当前的 `NetGuard_*` 策略，也兼容早期版本创建的 `NetClean_*` 策略，所以程序退出后再启动，限速标记仍然可以显示出来。

## 项目结构

```text
NetClean/
  MainForm.cs                 WinForms 主界面
  NetworkMonitor.cs           基于 ETW 的实时流量监控
  ProcessInfoCache.cs         PID 信息缓存
  SystemProcessClassifier.cs  系统进程分类
  ProcessTerminator.cs        深度关闭流程
  WindowsServiceManager.cs    Windows 服务控制封装
  UploadLimiter.cs            Windows QoS 上传限速
  ProcessInspector.cs         运行状态和相关进程提示
  LimitUploadDialog.cs        上传限速输入窗口
publish.ps1                   自包含单文件发布脚本
```

项目目录仍然叫 `NetClean`，这是早期原型留下的名字；现在产品名和发布出来的可执行文件名是 `NetGuard`。

## 注意事项

- 未签名 exe 可能触发 SmartScreen 提示，这是新构建工具常见现象。
- QoS 限速不等同于强制抓包限速，某些程序或连接类型可能不完全受控。
- 深度关闭只会停止当前会话中的服务，不会永久禁用服务，也不会修改服务启动类型。
- 如果后续想做更强的限速能力，可能需要接入 WFP 驱动或 WinDivert 这类更底层的流量控制方案。
