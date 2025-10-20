using Microsoft.Extensions.Configuration;
using NonIPFileDelivery.Models;
using System.Text;
using System.Threading;

namespace NonIPFileDelivery.Services;

/// <summary>
/// 設定ファイル管理サービス（INI/JSON 両対応）
/// </summary>
public class ConfigurationService : IConfigurationService
{
    private IConfiguration? _configuration;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public Configuration LoadConfiguration(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".ini" => LoadFromIni(path),
            ".json" => LoadFromJson(path),
            _ => throw new NotSupportedException($"Unsupported configuration file format: {ext}")
        };
    }

    public async Task<Configuration> LoadConfigurationAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Configuration file not found: {path}", path);

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".ini" => await LoadFromIniAsync(path, cancellationToken),
                ".json" => await LoadFromJsonAsync(path, cancellationToken),
                _ => throw new NotSupportedException($"Unsupported configuration file format: {ext}")
            };
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public void CreateDefaultConfiguration(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".json") { CreateDefaultJsonConfiguration(path); return; }

        var defaultIni = """
        [General]
        Mode=ActiveStandby
        LogLevel=Warning

        [Network]
        Interface=eth0
        FrameSize=9000
        Encryption=true
        EtherType=0x88B5

        [Security]
        EnableVirusScan=true
        ScanTimeout=5000
        QuarantinePath=C:\NonIP\Quarantine
        PolicyFile=security_policy.ini

        [Performance]
        MaxMemoryMB=8192
        BufferSize=65536
        ThreadPool=auto

        [Redundancy]
        HeartbeatInterval=1000
        FailoverTimeout=5000
        DataSyncMode=realtime
        """;

        File.WriteAllText(path, defaultIni, Encoding.UTF8);
    }

    public void CreateDefaultJsonConfiguration(string path)
    {
        var defaultConfig = new Configuration(); // 既定値はモデル側のデフォルト
        var json = System.Text.Json.JsonSerializer.Serialize(defaultConfig, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    public async Task CreateDefaultConfigurationAsync(string path, CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".json")
        {
            var defaultConfig = new Configuration();
            var json = System.Text.Json.JsonSerializer.Serialize(defaultConfig, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken);
        }
        else
        {
            var ini = """
            [General]
            Mode=ActiveStandby
            LogLevel=Warning

            [Network]
            Interface=eth0
            FrameSize=9000
            Encryption=true
            EtherType=0x88B5

            [Security]
            EnableVirusScan=true
            ScanTimeout=5000
            QuarantinePath=C:\NonIP\Quarantine
            PolicyFile=security_policy.ini

            [Performance]
            MaxMemoryMB=8192
            BufferSize=65536
            ThreadPool=auto

            [Redundancy]
            HeartbeatInterval=1000
            FailoverTimeout=5000
            DataSyncMode=realtime
            """;
            await File.WriteAllTextAsync(path, ini, Encoding.UTF8, cancellationToken);
        }
    }

    public async Task ConvertIniToJsonAsync(string iniPath, string jsonPath)
    {
        var config = LoadFromIni(iniPath);
        var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8);
    }

    private Configuration LoadFromIni(string path)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddIniFile(path, optional: false, reloadOnChange: true);

        _configuration = builder.Build();

        return new Configuration
        {
            General = new GeneralConfig
            {
                Mode = _configuration["General:Mode"] ?? "ActiveStandby",
                LogLevel = _configuration["General:LogLevel"] ?? "Warning"
            },
            Network = new NetworkConfig
            {
                Interface = _configuration["Network:Interface"] ?? "eth0",
                FrameSize = int.TryParse(_configuration["Network:FrameSize"], out var fs) ? fs : 9000,
                Encryption = bool.TryParse(_configuration["Network:Encryption"], out var enc) && enc,
                EtherType = _configuration["Network:EtherType"] ?? "0x88B5",
                RemoteMacAddress = _configuration["Network:RemoteMacAddress"],
                UseSecureTransceiver = bool.TryParse(_configuration["Network:UseSecureTransceiver"], out var ust) && ust
            },
            Security = new SecurityConfig
            {
                EnableVirusScan = bool.TryParse(_configuration["Security:EnableVirusScan"], out var ena) && ena,
                ScanTimeout = int.TryParse(_configuration["Security:ScanTimeout"], out var to) ? to : 5000,
                QuarantinePath = _configuration["Security:QuarantinePath"] ?? "C:\\NonIP\\Quarantine",
                PolicyFile = _configuration["Security:PolicyFile"] ?? "security_policy.ini"
            },
            Performance = new PerformanceConfig
            {
                MaxMemoryMB = int.TryParse(_configuration["Performance:MaxMemoryMB"], out var mm) ? mm : 8192,
                BufferSize = int.TryParse(_configuration["Performance:BufferSize"], out var bs) ? bs : 65536,
                ThreadPool = _configuration["Performance:ThreadPool"] ?? "auto"
            },
            Redundancy = new RedundancyConfig
            {
                HeartbeatInterval = int.TryParse(_configuration["Redundancy:HeartbeatInterval"], out var hb) ? hb : 1000,
                FailoverTimeout = int.TryParse(_configuration["Redundancy:FailoverTimeout"], out var fo) ? fo : 5000,
                DataSyncMode = _configuration["Redundancy:DataSyncMode"] ?? "realtime"
            }
        };
    }

    private async Task<Configuration> LoadFromIniAsync(string path, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"INI file not found: {path}");
        }, ct);

        return await Task.Run(() => LoadFromIni(path), ct);
    }

    private Configuration LoadFromJson(string path)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile(path, optional: false, reloadOnChange: true);

        _configuration = builder.Build();

        var config = new Configuration();
        _configuration.Bind(config);
        return config;
    }

    private async Task<Configuration> LoadFromJsonAsync(string path, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"JSON file not found: {path}");
        }, ct);

        return await Task.Run(() => LoadFromJson(path), ct);
    }

    /// <summary>
    /// 設定をファイルに保存
    /// </summary>
    public void SaveConfiguration(Configuration config, string configPath)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        
        var ext = Path.GetExtension(configPath).ToLowerInvariant();
        
        if (ext == ".json")
        {
            var json = System.Text.Json.JsonSerializer.Serialize(config, 
                new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
            File.WriteAllText(configPath, json, Encoding.UTF8);
        }
        else if (ext == ".ini")
        {
            // INI形式での保存実装
            var ini = new StringBuilder();
            
            ini.AppendLine("[General]");
            ini.AppendLine($"Mode={config.General?.Mode ?? "ActiveStandby"}");
            ini.AppendLine($"LogLevel={config.General?.LogLevel ?? "Warning"}");
            ini.AppendLine();
            
            ini.AppendLine("[Network]");
            ini.AppendLine($"Interface={config.Network?.Interface ?? "eth0"}");
            ini.AppendLine($"FrameSize={config.Network?.FrameSize ?? 9000}");
            ini.AppendLine($"Encryption={config.Network?.Encryption ?? true}");
            ini.AppendLine($"EtherType={config.Network?.EtherType ?? "0x88B5"}");
            if (!string.IsNullOrEmpty(config.Network?.RemoteMacAddress))
            {
                ini.AppendLine($"RemoteMacAddress={config.Network.RemoteMacAddress}");
            }
            ini.AppendLine($"UseSecureTransceiver={config.Network?.UseSecureTransceiver ?? false}");
            ini.AppendLine();
            
            ini.AppendLine("[Security]");
            ini.AppendLine($"EnableVirusScan={config.Security?.EnableVirusScan ?? true}");
            ini.AppendLine($"ScanTimeout={config.Security?.ScanTimeout ?? 5000}");
            ini.AppendLine($"QuarantinePath={config.Security?.QuarantinePath ?? "C:\\NonIP\\Quarantine"}");
            ini.AppendLine($"PolicyFile={config.Security?.PolicyFile ?? "security_policy.ini"}");
            ini.AppendLine();
            
            ini.AppendLine("[Performance]");
            ini.AppendLine($"MaxMemoryMB={config.Performance?.MaxMemoryMB ?? 8192}");
            ini.AppendLine($"BufferSize={config.Performance?.BufferSize ?? 65536}");
            ini.AppendLine($"ThreadPool={config.Performance?.ThreadPool ?? "auto"}");
            ini.AppendLine();
            
            ini.AppendLine("[Redundancy]");
            ini.AppendLine($"HeartbeatInterval={config.Redundancy?.HeartbeatInterval ?? 1000}");
            ini.AppendLine($"FailoverTimeout={config.Redundancy?.FailoverTimeout ?? 5000}");
            ini.AppendLine($"DataSyncMode={config.Redundancy?.DataSyncMode ?? "realtime"}");
            
            File.WriteAllText(configPath, ini.ToString(), Encoding.UTF8);
        }
        else
        {
            throw new NotSupportedException($"Unsupported configuration file format: {ext}");
        }
    }

    /// <summary>
    /// 設定の妥当性を検証
    /// </summary>
    public bool ValidateConfiguration(Configuration config)
    {
        if (config == null) return false;
        
        // General 検証
        if (config.General != null)
        {
            var validModes = new[] { "ActiveStandby", "LoadBalancing", "Standalone" };
            if (!string.IsNullOrWhiteSpace(config.General.Mode) && 
                !validModes.Contains(config.General.Mode))
            {
                return false;
            }
        }
        
        // Network 検証
        if (config.Network != null)
        {
            if (config.Network.FrameSize <= 0 || config.Network.FrameSize > 9000)
                return false;
            
            if (string.IsNullOrWhiteSpace(config.Network.Interface))
                return false;
        }
        
        // Security 検証
        if (config.Security != null)
        {
            if (config.Security.ScanTimeout < 0)
                return false;
            
            if (config.Security.EnableVirusScan && 
                string.IsNullOrWhiteSpace(config.Security.QuarantinePath))
            {
                return false;
            }
        }
        
        // Performance 検証
        if (config.Performance != null)
        {
            if (config.Performance.MaxMemoryMB <= 0 || config.Performance.MaxMemoryMB > 65536)
                return false;
            
            if (config.Performance.BufferSize <= 0)
                return false;
        }
        
        // Redundancy 検証
        if (config.Redundancy != null)
        {
            if (config.Redundancy.HeartbeatInterval < 100)
                return false;
            
            if (config.Redundancy.FailoverTimeout < config.Redundancy.HeartbeatInterval)
                return false;
        }
        
        return true;
    }
}
