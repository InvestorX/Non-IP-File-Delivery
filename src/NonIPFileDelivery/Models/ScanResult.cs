namespace NonIPFileDelivery.Models;

/// <summary>
/// セキュリティスキャン結果
/// </summary>
public class ScanResult
{
    public bool IsClean { get; set; }
    public string? ThreatName { get; set; }
    public string? Details { get; set; }
    public TimeSpan ScanDuration { get; set; }
    public string? EngineName { get; set; }
    public DateTime ScannedAt { get; set; } = DateTime.UtcNow;
    public string? FileName { get; set; }
    public long DataSize { get; set; }
}
