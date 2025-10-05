using System.Text.Json;
using System.Text;
using NonIPWebConfig.Models;

namespace NonIPWebConfig.Services;

/// <summary>
/// Service for managing configuration files
/// </summary>
public class ConfigurationService
{
    private readonly string _baseConfigPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public ConfigurationService(string baseConfigPath)
    {
        _baseConfigPath = baseConfigPath;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    /// Load configuration for Device A (appsettings.json)
    /// </summary>
    public async Task<AppSettingsModel> LoadDeviceAConfigAsync()
    {
        var path = Path.Combine(_baseConfigPath, "appsettings.json");
        return await LoadJsonConfigAsync<AppSettingsModel>(path);
    }

    /// <summary>
    /// Load configuration for Device B (appsettings.b.json)
    /// </summary>
    public async Task<AppSettingsModel> LoadDeviceBConfigAsync()
    {
        var path = Path.Combine(_baseConfigPath, "appsettings.b.json");
        return await LoadJsonConfigAsync<AppSettingsModel>(path);
    }

    /// <summary>
    /// Save configuration for Device A
    /// </summary>
    public async Task SaveDeviceAConfigAsync(AppSettingsModel config)
    {
        var path = Path.Combine(_baseConfigPath, "appsettings.json");
        await SaveJsonConfigAsync(path, config);
    }

    /// <summary>
    /// Save configuration for Device B
    /// </summary>
    public async Task SaveDeviceBConfigAsync(AppSettingsModel config)
    {
        var path = Path.Combine(_baseConfigPath, "appsettings.b.json");
        await SaveJsonConfigAsync(path, config);
    }

    /// <summary>
    /// Load INI configuration (config.ini)
    /// </summary>
    public async Task<IniConfigModel> LoadIniConfigAsync()
    {
        var path = Path.Combine(_baseConfigPath, "config.ini");
        
        if (!File.Exists(path))
        {
            return new IniConfigModel();
        }

        var config = new IniConfigModel();
        var lines = await File.ReadAllLinesAsync(path);
        string? currentSection = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                continue;

            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                currentSection = trimmed[1..^1];
                continue;
            }

            var parts = trimmed.Split('=', 2);
            if (parts.Length != 2)
                continue;

            var key = parts[0].Trim();
            var value = parts[1].Split('#')[0].Trim();

            ParseIniValue(config, currentSection, key, value);
        }

        return config;
    }

    /// <summary>
    /// Save INI configuration (config.ini)
    /// </summary>
    public async Task SaveIniConfigAsync(IniConfigModel config)
    {
        var path = Path.Combine(_baseConfigPath, "config.ini");
        var sb = new StringBuilder();

        sb.AppendLine("[General]");
        sb.AppendLine($"Mode={config.General.Mode}  # ActiveStandby | LoadBalancing");
        sb.AppendLine($"LogLevel={config.General.LogLevel}    # Debug | Info | Warning | Error");
        sb.AppendLine();

        sb.AppendLine("[Network]");
        sb.AppendLine($"Interface={config.Network.Interface}");
        sb.AppendLine($"FrameSize={config.Network.FrameSize}");
        sb.AppendLine($"Encryption={config.Network.Encryption.ToString().ToLower()}");
        sb.AppendLine($"EtherType={config.Network.EtherType}");
        sb.AppendLine();

        sb.AppendLine("[Security]");
        sb.AppendLine($"EnableVirusScan={config.Security.EnableVirusScan.ToString().ToLower()}");
        sb.AppendLine($"ScanTimeout={config.Security.ScanTimeout}    # milliseconds");
        sb.AppendLine($"QuarantinePath={config.Security.QuarantinePath}");
        sb.AppendLine($"PolicyFile={config.Security.PolicyFile}");
        sb.AppendLine();

        sb.AppendLine("[Performance]");
        sb.AppendLine($"MaxMemoryMB={config.Performance.MaxMemoryMB}");
        sb.AppendLine($"BufferSize={config.Performance.BufferSize}");
        sb.AppendLine($"ThreadPool={config.Performance.ThreadPool}");
        sb.AppendLine();

        sb.AppendLine("[Redundancy]");
        sb.AppendLine($"HeartbeatInterval={config.Redundancy.HeartbeatInterval}  # milliseconds");
        sb.AppendLine($"FailoverTimeout={config.Redundancy.FailoverTimeout}");
        sb.AppendLine($"DataSyncMode={config.Redundancy.DataSyncMode}");

        await File.WriteAllTextAsync(path, sb.ToString());
    }

    private async Task<T> LoadJsonConfigAsync<T>(string path) where T : new()
    {
        if (!File.Exists(path))
        {
            return new T();
        }

        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<T>(json, _jsonOptions) ?? new T();
    }

    private async Task SaveJsonConfigAsync<T>(string path, T config)
    {
        var json = JsonSerializer.Serialize(config, _jsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    private void ParseIniValue(IniConfigModel config, string? section, string key, string value)
    {
        switch (section)
        {
            case "General":
                if (key == "Mode") config.General.Mode = value;
                else if (key == "LogLevel") config.General.LogLevel = value;
                break;

            case "Network":
                if (key == "Interface") config.Network.Interface = value;
                else if (key == "FrameSize") config.Network.FrameSize = int.Parse(value);
                else if (key == "Encryption") config.Network.Encryption = bool.Parse(value);
                else if (key == "EtherType") config.Network.EtherType = value;
                break;

            case "Security":
                if (key == "EnableVirusScan") config.Security.EnableVirusScan = bool.Parse(value);
                else if (key == "ScanTimeout") config.Security.ScanTimeout = int.Parse(value);
                else if (key == "QuarantinePath") config.Security.QuarantinePath = value;
                else if (key == "PolicyFile") config.Security.PolicyFile = value;
                break;

            case "Performance":
                if (key == "MaxMemoryMB") config.Performance.MaxMemoryMB = int.Parse(value);
                else if (key == "BufferSize") config.Performance.BufferSize = int.Parse(value);
                else if (key == "ThreadPool") config.Performance.ThreadPool = value;
                break;

            case "Redundancy":
                if (key == "HeartbeatInterval") config.Redundancy.HeartbeatInterval = int.Parse(value);
                else if (key == "FailoverTimeout") config.Redundancy.FailoverTimeout = int.Parse(value);
                else if (key == "DataSyncMode") config.Redundancy.DataSyncMode = value;
                break;
        }
    }
}
