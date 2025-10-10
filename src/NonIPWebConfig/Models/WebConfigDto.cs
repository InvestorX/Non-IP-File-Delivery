using NonIPFileDelivery.Models;

namespace NonIPWebConfig.Models;

/// <summary>
/// Web UIとConfiguration間の変換を行うDTO
/// </summary>
public class WebConfigDto
{
    // 一般設定
    public string Mode { get; set; } = "ActiveStandby";
    public string LogLevel { get; set; } = "Warning";
    
    // ネットワーク設定
    public string Interface { get; set; } = "eth0";
    public string FrameSize { get; set; } = "9000";
    public string Encryption { get; set; } = "true";
    public string EtherType { get; set; } = "0x88B5";
    
    // セキュリティ設定
    public string EnableVirusScan { get; set; } = "true";
    public string ScanTimeout { get; set; } = "5000";
    public string QuarantinePath { get; set; } = "C:\\NonIP\\Quarantine";
    public string PolicyFile { get; set; } = "security_policy.ini";
    
    // パフォーマンス設定
    public string MaxMemoryMB { get; set; } = "8192";
    public string BufferSize { get; set; } = "65536";
    public string ThreadPool { get; set; } = "auto";
    
    // 冗長性設定
    public string HeartbeatInterval { get; set; } = "1000";
    public string FailoverTimeout { get; set; } = "5000";
    public string DataSyncMode { get; set; } = "realtime";
    public string? PrimaryNode { get; set; }
    public string? StandbyNode { get; set; }
    public string? VirtualIP { get; set; }
    public string? LoadBalancingAlgorithm { get; set; }

    /// <summary>
    /// WebConfigDtoからConfigurationモデルへ変換
    /// </summary>
    public Configuration ToConfiguration()
    {
        return new Configuration
        {
            General = new GeneralConfig
            {
                Mode = Mode,
                LogLevel = LogLevel
            },
            Network = new NetworkConfig
            {
                Interface = Interface,
                FrameSize = int.TryParse(FrameSize, out var fs) ? fs : 9000,
                Encryption = bool.TryParse(Encryption, out var enc) && enc,
                EtherType = EtherType
            },
            Security = new SecurityConfig
            {
                EnableVirusScan = bool.TryParse(EnableVirusScan, out var evs) && evs,
                ScanTimeout = int.TryParse(ScanTimeout, out var st) ? st : 5000,
                QuarantinePath = QuarantinePath,
                PolicyFile = PolicyFile
            },
            Performance = new PerformanceConfig
            {
                MaxMemoryMB = int.TryParse(MaxMemoryMB, out var mm) ? mm : 8192,
                BufferSize = int.TryParse(BufferSize, out var bs) ? bs : 65536,
                ThreadPool = ThreadPool
            },
            Redundancy = new RedundancyConfig
            {
                HeartbeatInterval = int.TryParse(HeartbeatInterval, out var hbi) ? hbi : 1000,
                FailoverTimeout = int.TryParse(FailoverTimeout, out var ft) ? ft : 5000,
                DataSyncMode = DataSyncMode,
                PrimaryNode = PrimaryNode,
                StandbyNode = StandbyNode,
                VirtualIP = VirtualIP,
                Algorithm = LoadBalancingAlgorithm ?? "RoundRobin"
            }
        };
    }

    /// <summary>
    /// ConfigurationモデルからWebConfigDtoへ変換
    /// </summary>
    public static WebConfigDto FromConfiguration(Configuration config)
    {
        return new WebConfigDto
        {
            Mode = config.General?.Mode ?? "ActiveStandby",
            LogLevel = config.General?.LogLevel ?? "Warning",
            Interface = config.Network?.Interface ?? "eth0",
            FrameSize = (config.Network?.FrameSize ?? 9000).ToString(),
            Encryption = (config.Network?.Encryption ?? true).ToString().ToLower(),
            EtherType = config.Network?.EtherType ?? "0x88B5",
            EnableVirusScan = (config.Security?.EnableVirusScan ?? true).ToString().ToLower(),
            ScanTimeout = (config.Security?.ScanTimeout ?? 5000).ToString(),
            QuarantinePath = config.Security?.QuarantinePath ?? "C:\\NonIP\\Quarantine",
            PolicyFile = config.Security?.PolicyFile ?? "security_policy.ini",
            MaxMemoryMB = (config.Performance?.MaxMemoryMB ?? 8192).ToString(),
            BufferSize = (config.Performance?.BufferSize ?? 65536).ToString(),
            ThreadPool = config.Performance?.ThreadPool ?? "auto",
            HeartbeatInterval = (config.Redundancy?.HeartbeatInterval ?? 1000).ToString(),
            FailoverTimeout = (config.Redundancy?.FailoverTimeout ?? 5000).ToString(),
            DataSyncMode = config.Redundancy?.DataSyncMode ?? "realtime",
            PrimaryNode = config.Redundancy?.PrimaryNode,
            StandbyNode = config.Redundancy?.StandbyNode,
            VirtualIP = config.Redundancy?.VirtualIP,
            LoadBalancingAlgorithm = config.Redundancy?.Algorithm
        };
    }
}
