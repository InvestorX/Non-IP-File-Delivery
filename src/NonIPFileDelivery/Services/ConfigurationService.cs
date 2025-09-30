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
                EtherType = _configuration["Network:EtherType"] ?? "0x88B5"
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
}
