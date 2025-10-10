using System.Text.Json.Serialization;

namespace NonIPWebConfig.Models;

/// <summary>
/// Configuration model for appsettings.json (Device A) and appsettings.b.json (Device B)
/// </summary>
public class AppSettingsModel
{
    public NetworkConfig Network { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public ProtocolsConfig Protocols { get; set; } = new();
    public PerformanceConfig Performance { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
}

public class NetworkConfig
{
    public string InterfaceName { get; set; } = "eth0";
    public string RemoteMacAddress { get; set; } = "00:11:22:33:44:55";
    public string CustomEtherType { get; set; } = "0x88B5";
}

public class SecurityConfig
{
    public string YaraRulesPath { get; set; } = "rules/*.yar";
    public bool EnableDeepInspection { get; set; } = true;
    public int ScanTimeout { get; set; } = 5000;
}

public class ProtocolsConfig
{
    public FtpConfig Ftp { get; set; } = new();
    public SftpConfig Sftp { get; set; } = new();
    public PostgresqlConfig Postgresql { get; set; } = new();
}

public class FtpConfig
{
    public bool Enabled { get; set; } = true;
    public int ListenPort { get; set; } = 21;
    public string TargetHost { get; set; } = "192.168.1.100";
    public int TargetPort { get; set; } = 21;
}

public class SftpConfig
{
    public bool Enabled { get; set; } = true;
    public int ListenPort { get; set; } = 22;
    public string TargetHost { get; set; } = "192.168.1.100";
    public int TargetPort { get; set; } = 22;
}

public class PostgresqlConfig
{
    public bool Enabled { get; set; } = true;
    public int ListenPort { get; set; } = 5432;
    public string TargetHost { get; set; } = "192.168.1.100";
    public int TargetPort { get; set; } = 5432;
}

public class PerformanceConfig
{
    public int ReceiveBufferSize { get; set; } = 10000;
    public int MaxConcurrentSessions { get; set; } = 100;
    public bool EnableZeroCopy { get; set; } = true;
}

public class LoggingConfig
{
    public string MinimumLevel { get; set; } = "Debug";
    public int RetentionDays { get; set; } = 30;
}

/// <summary>
/// Configuration model for config.ini
/// </summary>
public class IniConfigModel
{
    public GeneralSection General { get; set; } = new();
    public NetworkSection Network { get; set; } = new();
    public SecuritySection Security { get; set; } = new();
    public PerformanceSection Performance { get; set; } = new();
    public RedundancySection Redundancy { get; set; } = new();
}

public class GeneralSection
{
    public string Mode { get; set; } = "ActiveStandby";
    public string LogLevel { get; set; } = "Warning";
}

public class NetworkSection
{
    public string Interface { get; set; } = "eth0";
    public int FrameSize { get; set; } = 9000;
    public bool Encryption { get; set; } = true;
    public string EtherType { get; set; } = "0x88B5";
}

public class SecuritySection
{
    public bool EnableVirusScan { get; set; } = true;
    public int ScanTimeout { get; set; } = 5000;
    public string QuarantinePath { get; set; } = "C:\\NonIP\\Quarantine";
    public string PolicyFile { get; set; } = "security_policy.ini";
}

public class PerformanceSection
{
    public int MaxMemoryMB { get; set; } = 8192;
    public int BufferSize { get; set; } = 65536;
    public string ThreadPool { get; set; } = "auto";
}

public class RedundancySection
{
    public int HeartbeatInterval { get; set; } = 1000;
    public int FailoverTimeout { get; set; } = 5000;
    public string DataSyncMode { get; set; } = "realtime";
}

/// <summary>
/// Network interface information
/// </summary>
public class NetworkInterfaceInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long Speed { get; set; }
}

/// <summary>
/// System status information
/// </summary>
public class SystemStatus
{
    public string Status { get; set; } = "stopped";
    public string Version { get; set; } = "1.0.0";
    public string Uptime { get; set; } = "00:00:00";
    public string Throughput { get; set; } = "0 Gbps";
    public int Connections { get; set; } = 0;
    public string MemoryUsage { get; set; } = "0 MB";
    public DateTime LastUpdated { get; set; } = DateTime.Now;
}
