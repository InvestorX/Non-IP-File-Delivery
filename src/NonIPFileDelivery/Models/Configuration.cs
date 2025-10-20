using System;

namespace NonIPFileDelivery.Models;

public class Configuration
{
    public GeneralConfig General { get; set; } = new();
    public NetworkConfig Network { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public PerformanceConfig Performance { get; set; } = new();
    public RedundancyConfig Redundancy { get; set; } = new();
    public QoSConfig QoS { get; set; } = new();
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

public class QoSConfig
{
    /// <summary>
    /// QoS機能を有効化
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 最大帯域幅（Mbps）
    /// </summary>
    public int MaxBandwidthMbps { get; set; } = 2000;

    /// <summary>
    /// 高優先度フレームの重み（%）
    /// </summary>
    public int HighPriorityWeight { get; set; } = 70;

    /// <summary>
    /// 通常優先度フレームの重み（%）
    /// </summary>
    public int NormalPriorityWeight { get; set; } = 20;

    /// <summary>
    /// 低優先度フレームの重み（%）
    /// </summary>
    public int LowPriorityWeight { get; set; } = 10;

    /// <summary>
    /// QoSキューの最大サイズ
    /// </summary>
    public int MaxQueueSize { get; set; } = 10000;

    /// <summary>
    /// バースト許容サイズ（バイト）。0の場合は1秒分の帯域幅を使用
    /// </summary>
    public long BurstSizeBytes { get; set; } = 0;
}