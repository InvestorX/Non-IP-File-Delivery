using System;
using System.IO;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// 設定ファイル管理サービス（INIとJSON両対応）
/// </summary>
public class ConfigurationService : IConfigurationService
{
    private IConfiguration? _configuration;
    private readonly SemaphoreSlim _loadLock = new(1, 1); // 追加

    /// <summary>
    /// 設定ファイルを非同期で読み込む（推奨）
    /// </summary>
    /// <param name="path">設定ファイルのパス</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>設定オブジェクト</returns>
    public async Task<Configuration> LoadConfigurationAsync(
        string path, 
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Configuration file not found: {path}", path);
        }
        
        await _loadLock.WaitAsync(cancellationToken);
        
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            
            return extension switch
            {
                ".ini" => await LoadFromIniAsync(path, cancellationToken),
                ".json" => await LoadFromJsonAsync(path, cancellationToken),
                _ => throw new NotSupportedException($"Unsupported configuration file format: {extension}")
            };
        }
        finally
        {
            _loadLock.Release();
        }
    }

        /// <summary>
    /// INI形式の設定ファイルを非同期で読み込む
    /// </summary>
    private async Task<Configuration> LoadFromIniAsync(
        string path, 
        CancellationToken cancellationToken)
    {
        // ファイル存在確認を非同期で実行
        await Task.Run(() =>
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"INI file not found: {path}");
        }, cancellationToken);
        
        // ConfigurationBuilderは同期処理だが、別スレッドで実行
        return await Task.Run(() => LoadFromIni(path), cancellationToken);
    }

    /// <summary>
    /// JSON形式の設定ファイルを非同期で読み込む
    /// </summary>
    private async Task<Configuration> LoadFromJsonAsync(
        string path, 
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"JSON file not found: {path}");
        }, cancellationToken);
        
        return await Task.Run(() => LoadFromJson(path), cancellationToken);
    }

    

    /// <summary>
    /// デフォルト設定ファイルを非同期で生成
    /// </summary>
    public async Task CreateDefaultConfigurationAsync(
        string path, 
        CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        
        if (extension == ".json")
        {
            await CreateDefaultJsonConfigurationAsync(path, cancellationToken);
        }
        else
        {
            await CreateDefaultIniConfigurationAsync(path, cancellationToken);
        }
    }

    private async Task CreateDefaultIniConfigurationAsync(
        string path, 
        CancellationToken cancellationToken)
    {
        var defaultIni = @"[General]
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
";

        await File.WriteAllTextAsync(path, defaultIni, System.Text.Encoding.UTF8, cancellationToken);
    }

    private async Task CreateDefaultJsonConfigurationAsync(
        string path, 
        CancellationToken cancellationToken)
    {
        var defaultConfig = new Configuration(); // デフォルト値が設定済み
        
        var json = System.Text.Json.JsonSerializer.Serialize(defaultConfig, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        
        await File.WriteAllTextAsync(path, json, System.Text.Encoding.UTF8, cancellationToken);
    }

    /// <summary>
    /// 設定ファイルを読み込む（形式を自動判定）
    /// </summary>
    /// <param name="path">設定ファイルのパス</param>
    /// <returns>設定オブジェクト</returns>
    public Configuration LoadConfiguration(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        
        return extension switch
        {
            ".ini" => LoadFromIni(path),
            ".json" => LoadFromJson(path),
            _ => throw new NotSupportedException($"Unsupported configuration file format: {extension}")
        };
    }

     /// <summary>
    /// INI形式の設定ファイルを読み込む（既存機能）
    /// </summary>
    private Configuration LoadFromIni(string path)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddIniFile(path, optional: false, reloadOnChange: true);

        _configuration = builder.Build();

        // INI形式から設定オブジェクトにマッピング
        var config = new Configuration
        {
            General = new GeneralConfig
            {
                Mode = _configuration["General:Mode"] ?? "ActiveStandby",
                LogLevel = _configuration["General:LogLevel"] ?? "Warning"
            },
            Network = new NetworkConfig
            {
                Interface = _configuration["Network:Interface"] ?? "eth0",
                FrameSize = int.TryParse(_configuration["Network:FrameSize"], out var frameSize) ? frameSize : 9000,
                Encryption = bool.TryParse(_configuration["Network:Encryption"], out var encryption) && encryption,
                EtherType = _configuration["Network:EtherType"] ?? "0x88B5"
            },
            Security = new SecurityConfig
            {
                EnableVirusScan = bool.TryParse(_configuration["Security:EnableVirusScan"], out var enableScan) && enableScan,
                ScanTimeout = int.TryParse(_configuration["Security:ScanTimeout"], out var timeout) ? timeout : 5000,
                QuarantinePath = _configuration["Security:QuarantinePath"] ?? "C:\\NonIP\\Quarantine",
                PolicyFile = _configuration["Security:PolicyFile"] ?? "security_policy.ini"
            },
            Performance = new PerformanceConfig
            {
                MaxMemoryMB = int.TryParse(_configuration["Performance:MaxMemoryMB"], out var maxMem) ? maxMem : 8192,
                BufferSize = int.TryParse(_configuration["Performance:BufferSize"], out var bufSize) ? bufSize : 65536,
                ThreadPool = _configuration["Performance:ThreadPool"] ?? "auto"
            },
            Redundancy = new RedundancyConfig
            {
                HeartbeatInterval = int.TryParse(_configuration["Redundancy:HeartbeatInterval"], out var hbInterval) ? hbInterval : 1000,
                FailoverTimeout = int.TryParse(_configuration["Redundancy:FailoverTimeout"], out var foTimeout) ? foTimeout : 5000,
                DataSyncMode = _configuration["Redundancy:DataSyncMode"] ?? "realtime"
            }
        };

        return config;
    }

    /// <summary>
    /// JSON形式の設定ファイルを読み込む（新機能）
    /// </summary>
    private Configuration LoadFromJson(string path)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile(path, optional: false, reloadOnChange: true);

        _configuration = builder.Build();

        // JSON形式から設定オブジェクトにバインド
        var config = new Configuration();
        _configuration.Bind(config);

        return config;
    }

    /// <summary>
    /// デフォルトINI設定ファイルを生成（既存機能）
    /// </summary>
    public void CreateDefaultConfiguration(string path)
    {
        var defaultIni = @"[General]
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
";

        File.WriteAllText(path, defaultIni, Encoding.UTF8);
    }

    /// <summary>
    /// デフォルトJSON設定ファイルを生成（新機能）
    /// </summary>
    public void CreateDefaultJsonConfiguration(string path)
    {
        var defaultJson = @"{
  ""General"": {
    ""Mode"": ""ActiveStandby"",
    ""LogLevel"": ""Warning""
  },
  ""Network"": {
    ""Interface"": ""eth0"",
    ""FrameSize"": 9000,
    ""Encryption"": true,
    ""EtherType"": ""0x88B5""
  },
  ""Security"": {
    ""EnableVirusScan"": true,
    ""ScanTimeout"": 5000,
    ""QuarantinePath"": ""C:\\NonIP\\Quarantine"",
    ""PolicyFile"": ""security_policy.ini""
  },
  ""Performance"": {
    ""MaxMemoryMB"": 8192,
    ""BufferSize"": 65536,
    ""ThreadPool"": ""auto""
  },
  ""Redundancy"": {
    ""HeartbeatInterval"": 1000,
    ""FailoverTimeout"": 5000,
    ""DataSyncMode"": ""realtime""
  }
}";

        File.WriteAllText(path, defaultJson, Encoding.UTF8);
    }

    /// <summary>
    /// INI設定をJSON形式に変換（移行ツール）
    /// </summary>
    /// <param name="iniPath">変換元のINIファイルパス</param>
    /// <param name="jsonPath">変換先のJSONファイルパス</param>
    public async Task ConvertIniToJsonAsync(string iniPath, string jsonPath)
    {
        // INIファイルを読み込む
        var config = LoadFromIni(iniPath);

        // JSON形式でシリアライズ
        var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });

        // JSONファイルに書き込む
        await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8);
    }

    public void SaveConfiguration(Configuration config, string configPath)
    {
        var content = GenerateConfigContent(config);
        File.WriteAllText(configPath, content);
    }

    public bool ValidateConfiguration(Configuration config)
    {
        try
        {
            // Validate required settings
            if (string.IsNullOrEmpty(config.General.Mode) ||
                (config.General.Mode != "ActiveStandby" && config.General.Mode != "LoadBalancing"))
            {
                return false;
            }

            if (string.IsNullOrEmpty(config.Network.Interface))
                return false;

            if (config.Network.FrameSize < 64 || config.Network.FrameSize > 9000)
                return false;

            if (config.Security.ScanTimeout <= 0)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void CreateDefaultConfiguration(string configPath)
    {
        var defaultConfig = new Configuration();
        SaveConfiguration(defaultConfig, configPath);
    }

    private void SetConfigValue(Configuration config, string? section, string key, string value)
    {
        switch (section?.ToLower())
        {
            case "general":
                SetGeneralConfig(config.General, key, value);
                break;
            case "network":
                SetNetworkConfig(config.Network, key, value);
                break;
            case "security":
                SetSecurityConfig(config.Security, key, value);
                break;
            case "performance":
                SetPerformanceConfig(config.Performance, key, value);
                break;
            case "redundancy":
                SetRedundancyConfig(config.Redundancy, key, value);
                break;
        }
    }

    private void SetGeneralConfig(GeneralConfig config, string key, string value)
    {
        switch (key.ToLower())
        {
            case "mode":
                config.Mode = value;
                break;
            case "loglevel":
                config.LogLevel = value;
                break;
        }
    }

    private void SetNetworkConfig(NetworkConfig config, string key, string value)
    {
        switch (key.ToLower())
        {
            case "interface":
                config.Interface = value;
                break;
            case "framesize":
                if (int.TryParse(value, out int frameSize))
                    config.FrameSize = frameSize;
                break;
            case "encryption":
                if (bool.TryParse(value, out bool encryption))
                    config.Encryption = encryption;
                break;
            case "ethertype":
                config.EtherType = value;
                break;
        }
    }

    private void SetSecurityConfig(SecurityConfig config, string key, string value)
    {
        switch (key.ToLower())
        {
            case "enablevirusscan":
                if (bool.TryParse(value, out bool enableScan))
                    config.EnableVirusScan = enableScan;
                break;
            case "scantimeout":
                if (int.TryParse(value, out int timeout))
                    config.ScanTimeout = timeout;
                break;
            case "quarantinepath":
                config.QuarantinePath = value;
                break;
            case "policyfile":
                config.PolicyFile = value;
                break;
        }
    }

    private void SetPerformanceConfig(PerformanceConfig config, string key, string value)
    {
        switch (key.ToLower())
        {
            case "maxmemorymb":
                if (int.TryParse(value, out int maxMemory))
                    config.MaxMemoryMB = maxMemory;
                break;
            case "buffersize":
                if (int.TryParse(value, out int bufferSize))
                    config.BufferSize = bufferSize;
                break;
            case "threadpool":
                config.ThreadPool = value;
                break;
        }
    }

    private void SetRedundancyConfig(RedundancyConfig config, string key, string value)
    {
        switch (key.ToLower())
        {
            case "heartbeatinterval":
                if (int.TryParse(value, out int heartbeat))
                    config.HeartbeatInterval = heartbeat;
                break;
            case "failovertimeout":
                if (int.TryParse(value, out int failover))
                    config.FailoverTimeout = failover;
                break;
            case "datasyncmode":
                config.DataSyncMode = value;
                break;
        }
    }

    private string GenerateConfigContent(Configuration config)
    {
        return $@"[General]
Mode={config.General.Mode}  # ActiveStandby | LoadBalancing
LogLevel={config.General.LogLevel}    # Debug | Info | Warning | Error

[Network]
Interface={config.Network.Interface}
FrameSize={config.Network.FrameSize}
Encryption={config.Network.Encryption.ToString().ToLower()}
EtherType={config.Network.EtherType}

[Security]
EnableVirusScan={config.Security.EnableVirusScan.ToString().ToLower()}
ScanTimeout={config.Security.ScanTimeout}    # milliseconds
QuarantinePath={config.Security.QuarantinePath}
PolicyFile={config.Security.PolicyFile}

[Performance]
MaxMemoryMB={config.Performance.MaxMemoryMB}
BufferSize={config.Performance.BufferSize}
ThreadPool={config.Performance.ThreadPool}

[Redundancy]
HeartbeatInterval={config.Redundancy.HeartbeatInterval}  # milliseconds
FailoverTimeout={config.Redundancy.FailoverTimeout}
DataSyncMode={config.Redundancy.DataSyncMode}";
    }
}
