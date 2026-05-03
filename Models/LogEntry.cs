using System;

namespace InfoTransfer.Models;

public class LogEntry
{
    public DateTime Time { get; set; }
    public string Level { get; set; } = "INFO";
    public string Message { get; set; } = "";

    public override string ToString()
    {
        return $"[{Time:HH:mm:ss}] [{Level}] {Message}";
    }
}

public class ServiceStatus
{
    public bool IsServerRunning { get; set; }
    public bool IsPushServiceRunning { get; set; }
    public int Port { get; set; }
    public int ScanIntervalSeconds { get; set; }
    public int PendingCount { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }
    public DateTime? LastScanTime { get; set; }
}
