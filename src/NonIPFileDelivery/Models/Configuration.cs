using System;

namespace NonIPFileDelivery.Models;

public class Configuration
{
    public GeneralConfig General { get; set; } = new();
    public NetworkConfig Network { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public PerformanceConfig Performance { get; set; } = new();
    public RedundancyConfig Redundancy { get; set; } = new();
}

public class GeneralConfig
{
    public string Mode { get; set; } = "ActiveStandby";
    public string LogLevel { get; set; } = "Warning";
}

public class NetworkConfig
{
    public string Interface { get; set; } = "eth0";
    public int FrameSize { get; set; } = 9000;
    public bool Encryption { get; set; } = true;
    public string EtherType { get; set; } = "0x88B5";
}

public class SecurityConfig
{
    public bool EnableVirusScan { get; set; } = true;
    public int ScanTimeout { get; set; } = 5000;
    public string QuarantinePath { get; set; } = "C:\\NonIP\\Quarantine";
    public string PolicyFile { get; set; } = "security_policy.ini";
}

public class PerformanceConfig
{
    public int MaxMemoryMB { get; set; } = 8192;
    public int BufferSize { get; set; } = 65536;
    public string ThreadPool { get; set; } = "auto";
}

public class RedundancyConfig
{
    public int HeartbeatInterval { get; set; } = 1000;
    public int FailoverTimeout { get; set; } = 5000;
    public string DataSyncMode { get; set; } = "realtime";
    public string? PrimaryNode { get; set; }
    public string? StandbyNode { get; set; }
    public string? VirtualIP { get; set; }
    public string[]? Nodes { get; set; }
    public string Algorithm { get; set; } = "RoundRobin";
}