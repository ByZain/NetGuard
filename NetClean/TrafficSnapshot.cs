namespace NetClean;

internal sealed record TrafficSnapshot(
    int ProcessId,
    string ProcessName,
    string DisplayName,
    string Path,
    string ProcessTag,
    bool IsSystemProcess,
    bool IsCriticalSystemProcess,
    long UploadBytesPerSecond,
    long DownloadBytesPerSecond,
    long TotalUploadBytes,
    long TotalDownloadBytes,
    DateTime LastSeen);
