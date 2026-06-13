using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetClean;

internal sealed class NetworkMonitor : IDisposable
{
    private readonly ConcurrentDictionary<int, TrafficCounter> _counters = new();
    private readonly ProcessInfoCache _processInfoCache = new();
    private readonly object _lifetimeLock = new();
    private TraceEventSession? _session;
    private Task? _processingTask;
    private long _lastSnapshotTicks = Stopwatch.GetTimestamp();
    private bool _disposed;

    public event Action<string>? StatusChanged;

    public bool IsRunning { get; private set; }

    public void Start()
    {
        lock (_lifetimeLock)
        {
            if (IsRunning)
            {
                return;
            }

            if (TraceEventSession.IsElevated() != true)
            {
                throw new InvalidOperationException("需要以管理员身份运行，才能读取 Windows 内核网络事件。");
            }

            try
            {
                var sessionName = $"NetClean-Network-{Environment.ProcessId}-{Guid.NewGuid():N}";
                _session = new TraceEventSession(sessionName)
                {
                    StopOnDispose = true
                };

                _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
                AttachCallbacks(_session);
                IsRunning = true;
                _lastSnapshotTicks = Stopwatch.GetTimestamp();
                var activeSession = _session;

                _processingTask = Task.Run(() =>
                {
                    try
                    {
                        activeSession.Source.Process();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (Exception ex)
                    {
                        StatusChanged?.Invoke($"监控已停止：{ex.Message}");
                    }
                    finally
                    {
                        IsRunning = false;
                    }
                });
            }
            catch
            {
                IsRunning = false;
                _session?.Dispose();
                _session = null;
                throw;
            }

            StatusChanged?.Invoke("正在监控 TCP/UDP 实时流量。");
        }
    }

    public IReadOnlyList<TrafficSnapshot> ReadSnapshots()
    {
        var nowTicks = Stopwatch.GetTimestamp();
        var elapsedSeconds = Math.Max(0.2, (nowTicks - _lastSnapshotTicks) / (double)Stopwatch.Frequency);
        _lastSnapshotTicks = nowTicks;

        var now = DateTime.UtcNow;
        var snapshots = new List<TrafficSnapshot>();

        foreach (var (pid, counter) in _counters)
        {
            var uploadBytes = Interlocked.Exchange(ref counter.UploadBytesInWindow, 0);
            var downloadBytes = Interlocked.Exchange(ref counter.DownloadBytesInWindow, 0);
            var totalUpload = Interlocked.Read(ref counter.TotalUploadBytes);
            var totalDownload = Interlocked.Read(ref counter.TotalDownloadBytes);
            var lastSeenTicks = Interlocked.Read(ref counter.LastSeenUtcTicks);
            var lastSeen = new DateTime(lastSeenTicks, DateTimeKind.Utc);

            if (now - lastSeen > TimeSpan.FromSeconds(45))
            {
                _counters.TryRemove(pid, out _);
                continue;
            }

            var uploadRate = (long)Math.Round(uploadBytes / elapsedSeconds);
            var downloadRate = (long)Math.Round(downloadBytes / elapsedSeconds);
            var processInfo = _processInfoCache.Get(pid, counter.LastKnownName);

            snapshots.Add(new TrafficSnapshot(
                pid,
                processInfo.ProcessName,
                processInfo.DisplayName,
                processInfo.Path,
                processInfo.ProcessTag,
                processInfo.IsSystemProcess,
                processInfo.IsCriticalSystemProcess,
                uploadRate,
                downloadRate,
                totalUpload,
                totalDownload,
                lastSeen.ToLocalTime()));
        }

        _processInfoCache.TrimMissingProcesses();

        return snapshots
            .OrderByDescending(item => item.UploadBytesPerSecond)
            .ThenByDescending(item => item.DownloadBytesPerSecond)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public void Stop()
    {
        lock (_lifetimeLock)
        {
            if (_session is null)
            {
                IsRunning = false;
                return;
            }

            try
            {
                _session.Source.StopProcessing();
            }
            catch
            {
            }

            try
            {
                _session.Dispose();
            }
            catch
            {
            }

            _session = null;
            IsRunning = false;
            StatusChanged?.Invoke("监控已暂停。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        try
        {
            _processingTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }
    }

    private void AttachCallbacks(TraceEventSession session)
    {
        session.Source.Kernel.TcpIpSend += data => AddUpload(data.ProcessID, data.size, data.ProcessName);
        session.Source.Kernel.TcpIpSendIPV6 += data => AddUpload(data.ProcessID, data.size, data.ProcessName);
        session.Source.Kernel.TcpIpRetransmit += data => AddUpload(data.ProcessID, data.size, data.ProcessName);
        session.Source.Kernel.TcpIpRetransmitIPV6 += data => AddUpload(data.ProcessID, data.size, data.ProcessName);
        session.Source.Kernel.UdpIpSend += data => AddUpload(data.ProcessID, data.size, data.ProcessName);
        session.Source.Kernel.UdpIpSendIPV6 += data => AddUpload(data.ProcessID, data.size, data.ProcessName);

        session.Source.Kernel.TcpIpRecv += data => AddDownload(data.ProcessID, data.size, data.ProcessName);
        session.Source.Kernel.TcpIpRecvIPV6 += data => AddDownload(data.ProcessID, data.size, data.ProcessName);
        session.Source.Kernel.UdpIpRecv += data => AddDownload(data.ProcessID, data.size, data.ProcessName);
        session.Source.Kernel.UdpIpRecvIPV6 += data => AddDownload(data.ProcessID, data.size, data.ProcessName);
    }

    private void AddUpload(int pid, int bytes, string processName)
    {
        if (pid <= 0 || bytes <= 0 || pid == Environment.ProcessId)
        {
            return;
        }

        var counter = _counters.GetOrAdd(pid, _ => new TrafficCounter(processName));
        counter.RememberName(processName);
        Interlocked.Add(ref counter.UploadBytesInWindow, bytes);
        Interlocked.Add(ref counter.TotalUploadBytes, bytes);
        Interlocked.Exchange(ref counter.LastSeenUtcTicks, DateTime.UtcNow.Ticks);
    }

    private void AddDownload(int pid, int bytes, string processName)
    {
        if (pid <= 0 || bytes <= 0 || pid == Environment.ProcessId)
        {
            return;
        }

        var counter = _counters.GetOrAdd(pid, _ => new TrafficCounter(processName));
        counter.RememberName(processName);
        Interlocked.Add(ref counter.DownloadBytesInWindow, bytes);
        Interlocked.Add(ref counter.TotalDownloadBytes, bytes);
        Interlocked.Exchange(ref counter.LastSeenUtcTicks, DateTime.UtcNow.Ticks);
    }

    private sealed class TrafficCounter
    {
        public long UploadBytesInWindow;
        public long DownloadBytesInWindow;
        public long TotalUploadBytes;
        public long TotalDownloadBytes;
        public long LastSeenUtcTicks = DateTime.UtcNow.Ticks;
        private string _lastKnownName;

        public TrafficCounter(string name)
        {
            _lastKnownName = string.IsNullOrWhiteSpace(name) ? "未知进程" : name;
        }

        public string LastKnownName => _lastKnownName;

        public void RememberName(string name)
        {
            if (!string.IsNullOrWhiteSpace(name) && _lastKnownName != name)
            {
                _lastKnownName = name;
            }
        }
    }
}
