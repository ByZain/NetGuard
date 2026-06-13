namespace NetClean;

internal sealed class TrafficRow
{
    public int Pid { get; init; }
    public string Program { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string ProcessTag { get; init; } = "";
    public string Status { get; init; } = "";
    public string UploadLimit { get; init; } = "";
    public string UploadSpeed { get; init; } = "";
    public string DownloadSpeed { get; init; } = "";
    public string TotalUpload { get; init; } = "";
    public string LastSeen { get; init; } = "";
    public string Path { get; init; } = "";
    public long UploadBytesPerSecond { get; init; }
    public long DownloadBytesPerSecond { get; init; }
    public long TotalUploadBytes { get; init; }
    public DateTime LastSeenAt { get; init; }
    public bool IsSystemProcess { get; init; }
    public bool IsCriticalSystemProcess { get; init; }
    public bool IsRunning { get; init; }
    public bool IsUploadLimited { get; init; }
}
