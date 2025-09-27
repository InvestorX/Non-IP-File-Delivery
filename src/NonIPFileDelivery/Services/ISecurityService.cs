using System;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

public interface ISecurityService
{
    Task<bool> InitializeSecurity(SecurityConfig config);
    Task<ScanResult> ScanData(byte[] data, string fileName);
    Task<bool> QuarantineFile(string filePath, string reason);
    bool IsSecurityEnabled { get; }
}

public class ScanResult
{
    public bool IsClean { get; set; }
    public string? ThreatName { get; set; }
    public string? Details { get; set; }
    public TimeSpan ScanDuration { get; set; }
}