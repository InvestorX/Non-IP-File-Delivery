using System;
using System.IO;
using System.Text.RegularExpressions;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

public class ConfigurationService : IConfigurationService
{
    public Configuration LoadConfiguration(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Configuration file not found: {configPath}");
        }

        var config = new Configuration();
        var lines = File.ReadAllLines(configPath);
        string? currentSection = null;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith('#'))
                continue;

            // Section header
            if (trimmedLine.StartsWith('[') && trimmedLine.EndsWith(']'))
            {
                currentSection = trimmedLine[1..^1];
                continue;
            }

            // Key-value pair
            var equalIndex = trimmedLine.IndexOf('=');
            if (equalIndex > 0)
            {
                var key = trimmedLine[..equalIndex].Trim();
                var value = trimmedLine[(equalIndex + 1)..].Split('#')[0].Trim();

                SetConfigValue(config, currentSection, key, value);
            }
        }

        return config;
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