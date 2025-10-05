using System.Text.Json;
using NonIPWebConfig.Services;
using NonIPWebConfig.Models;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Register services
var baseConfigPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..");
builder.Services.AddSingleton(new ConfigurationService(baseConfigPath));
builder.Services.AddSingleton<NetworkInterfaceService>();

var app = builder.Build();

// Configure the HTTP request pipeline
app.UseCors();

// Serve static files (HTML, CSS, JS)
var provider = new FileExtensionContentTypeProvider();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider
});
app.UseDefaultFiles();

// API: Get network interfaces
app.MapGet("/api/interfaces", (NetworkInterfaceService networkService) =>
{
    try
    {
        var interfaces = networkService.GetNetworkInterfaces();
        return Results.Json(interfaces);
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, message = $"Failed to get interfaces: {ex.Message}" });
    }
});

// API: Get configuration for Device A
app.MapGet("/api/config/a", async (ConfigurationService configService) =>
{
    try
    {
        var appConfig = await configService.LoadDeviceAConfigAsync();
        var iniConfig = await configService.LoadIniConfigAsync();
        
        var config = new
        {
            // General
            mode = iniConfig.General.Mode,
            logLevel = iniConfig.General.LogLevel,
            
            // Network
            interfaceName = appConfig.Network.InterfaceName,
            remoteMacAddress = appConfig.Network.RemoteMacAddress,
            frameSize = iniConfig.Network.FrameSize,
            encryption = iniConfig.Network.Encryption.ToString().ToLower(),
            etherType = appConfig.Network.CustomEtherType,
            
            // FTP
            ftpEnabled = appConfig.Protocols.Ftp.Enabled,
            ftpListenPort = appConfig.Protocols.Ftp.ListenPort,
            ftpTargetHost = appConfig.Protocols.Ftp.TargetHost,
            ftpTargetPort = appConfig.Protocols.Ftp.TargetPort,
            
            // SFTP
            sftpEnabled = appConfig.Protocols.Sftp.Enabled,
            sftpListenPort = appConfig.Protocols.Sftp.ListenPort,
            sftpTargetHost = appConfig.Protocols.Sftp.TargetHost,
            sftpTargetPort = appConfig.Protocols.Sftp.TargetPort,
            
            // PostgreSQL
            postgresqlEnabled = appConfig.Protocols.Postgresql.Enabled,
            postgresqlListenPort = appConfig.Protocols.Postgresql.ListenPort,
            postgresqlTargetHost = appConfig.Protocols.Postgresql.TargetHost,
            postgresqlTargetPort = appConfig.Protocols.Postgresql.TargetPort,
            
            // Security
            enableVirusScan = iniConfig.Security.EnableVirusScan,
            enableDeepInspection = appConfig.Security.EnableDeepInspection,
            scanTimeout = appConfig.Security.ScanTimeout,
            quarantinePath = iniConfig.Security.QuarantinePath,
            yaraRulesPath = appConfig.Security.YaraRulesPath,
            
            // Performance
            receiveBufferSize = appConfig.Performance.ReceiveBufferSize,
            maxConcurrentSessions = appConfig.Performance.MaxConcurrentSessions,
            enableZeroCopy = appConfig.Performance.EnableZeroCopy,
            maxMemoryMB = iniConfig.Performance.MaxMemoryMB,
            bufferSize = iniConfig.Performance.BufferSize,
            
            // Redundancy
            heartbeatInterval = iniConfig.Redundancy.HeartbeatInterval,
            failoverTimeout = iniConfig.Redundancy.FailoverTimeout,
            dataSyncMode = iniConfig.Redundancy.DataSyncMode
        };
        
        return Results.Json(config);
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, message = $"Failed to load config: {ex.Message}" });
    }
});

// API: Get configuration for Device B
app.MapGet("/api/config/b", async (ConfigurationService configService) =>
{
    try
    {
        var appConfig = await configService.LoadDeviceBConfigAsync();
        var iniConfig = await configService.LoadIniConfigAsync();
        
        var config = new
        {
            // General
            mode = iniConfig.General.Mode,
            logLevel = iniConfig.General.LogLevel,
            
            // Network
            interfaceName = appConfig.Network.InterfaceName,
            remoteMacAddress = appConfig.Network.RemoteMacAddress,
            frameSize = iniConfig.Network.FrameSize,
            encryption = iniConfig.Network.Encryption.ToString().ToLower(),
            etherType = appConfig.Network.CustomEtherType,
            
            // FTP (B-side has no listen port)
            ftpEnabled = appConfig.Protocols.Ftp.Enabled,
            ftpTargetHost = appConfig.Protocols.Ftp.TargetHost,
            ftpTargetPort = appConfig.Protocols.Ftp.TargetPort,
            
            // SFTP
            sftpEnabled = appConfig.Protocols.Sftp.Enabled,
            sftpTargetHost = appConfig.Protocols.Sftp.TargetHost,
            sftpTargetPort = appConfig.Protocols.Sftp.TargetPort,
            
            // PostgreSQL
            postgresqlEnabled = appConfig.Protocols.Postgresql.Enabled,
            postgresqlTargetHost = appConfig.Protocols.Postgresql.TargetHost,
            postgresqlTargetPort = appConfig.Protocols.Postgresql.TargetPort,
            
            // Security
            enableVirusScan = iniConfig.Security.EnableVirusScan,
            enableDeepInspection = appConfig.Security.EnableDeepInspection,
            scanTimeout = appConfig.Security.ScanTimeout,
            quarantinePath = iniConfig.Security.QuarantinePath,
            yaraRulesPath = appConfig.Security.YaraRulesPath,
            
            // Performance
            receiveBufferSize = appConfig.Performance.ReceiveBufferSize,
            maxConcurrentSessions = appConfig.Performance.MaxConcurrentSessions,
            enableZeroCopy = appConfig.Performance.EnableZeroCopy,
            maxMemoryMB = iniConfig.Performance.MaxMemoryMB,
            bufferSize = iniConfig.Performance.BufferSize,
            
            // Redundancy
            heartbeatInterval = iniConfig.Redundancy.HeartbeatInterval,
            failoverTimeout = iniConfig.Redundancy.FailoverTimeout,
            dataSyncMode = iniConfig.Redundancy.DataSyncMode
        };
        
        return Results.Json(config);
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, message = $"Failed to load config: {ex.Message}" });
    }
});

// API: Save configuration for Device A
app.MapPost("/api/config/a", async (JsonElement configJson, ConfigurationService configService) =>
{
    try
    {
        var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(configJson.GetRawText());
        if (config == null)
        {
            return Results.Json(new { success = false, message = "Invalid configuration data" });
        }
        
        // Load existing configs
        var appConfig = await configService.LoadDeviceAConfigAsync();
        var iniConfig = await configService.LoadIniConfigAsync();
        
        // Update configs
        UpdateConfigFromDictionary(config, appConfig, iniConfig, true);
        
        // Save configs
        await configService.SaveDeviceAConfigAsync(appConfig);
        await configService.SaveIniConfigAsync(iniConfig);
        
        return Results.Json(new { success = true, message = "非IP送受信機Aの設定が正常に保存されました" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, message = $"設定の保存に失敗しました: {ex.Message}" });
    }
});

// API: Save configuration for Device B
app.MapPost("/api/config/b", async (JsonElement configJson, ConfigurationService configService) =>
{
    try
    {
        var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(configJson.GetRawText());
        if (config == null)
        {
            return Results.Json(new { success = false, message = "Invalid configuration data" });
        }
        
        // Load existing configs
        var appConfig = await configService.LoadDeviceBConfigAsync();
        var iniConfig = await configService.LoadIniConfigAsync();
        
        // Update configs
        UpdateConfigFromDictionary(config, appConfig, iniConfig, false);
        
        // Save configs
        await configService.SaveDeviceBConfigAsync(appConfig);
        await configService.SaveIniConfigAsync(iniConfig);
        
        return Results.Json(new { success = true, message = "非IP送受信機Bの設定が正常に保存されました" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, message = $"設定の保存に失敗しました: {ex.Message}" });
    }
});

// API: Get system status
app.MapGet("/api/status", () =>
{
    // TODO: Implement actual status monitoring
    // For now, return mock data
    return Results.Json(new SystemStatus
    {
        Status = "stopped",
        Version = "1.0.0",
        Uptime = "00:00:00",
        Throughput = "0 Gbps",
        Connections = 0,
        MemoryUsage = "0 MB",
        LastUpdated = DateTime.Now
    });
});

Console.WriteLine("🌐 Non-IP Web Configuration Tool が起動しました");
Console.WriteLine("📱 ブラウザで http://localhost:8080 を開いてください");

app.Run("http://localhost:8080");

// Helper method to update configuration from dictionary
static void UpdateConfigFromDictionary(Dictionary<string, JsonElement> config, AppSettingsModel appConfig, IniConfigModel iniConfig, bool isDeviceA)
{
    // General
    if (config.ContainsKey("mode")) iniConfig.General.Mode = GetStringValue(config["mode"]);
    if (config.ContainsKey("logLevel")) iniConfig.General.LogLevel = GetStringValue(config["logLevel"]);
    
    // Network
    if (config.ContainsKey("interfaceName")) appConfig.Network.InterfaceName = GetStringValue(config["interfaceName"]);
    if (config.ContainsKey("remoteMacAddress")) appConfig.Network.RemoteMacAddress = GetStringValue(config["remoteMacAddress"]);
    if (config.ContainsKey("frameSize")) iniConfig.Network.FrameSize = GetIntValue(config["frameSize"]);
    if (config.ContainsKey("encryption")) iniConfig.Network.Encryption = GetBoolValue(config["encryption"]);
    if (config.ContainsKey("etherType")) appConfig.Network.CustomEtherType = GetStringValue(config["etherType"]);
    
    // FTP
    if (config.ContainsKey("ftpEnabled")) appConfig.Protocols.Ftp.Enabled = GetBoolValue(config["ftpEnabled"]);
    if (isDeviceA && config.ContainsKey("ftpListenPort")) appConfig.Protocols.Ftp.ListenPort = GetIntValue(config["ftpListenPort"]);
    if (config.ContainsKey("ftpTargetHost")) appConfig.Protocols.Ftp.TargetHost = GetStringValue(config["ftpTargetHost"]);
    if (config.ContainsKey("ftpTargetPort")) appConfig.Protocols.Ftp.TargetPort = GetIntValue(config["ftpTargetPort"]);
    
    // SFTP
    if (config.ContainsKey("sftpEnabled")) appConfig.Protocols.Sftp.Enabled = GetBoolValue(config["sftpEnabled"]);
    if (isDeviceA && config.ContainsKey("sftpListenPort")) appConfig.Protocols.Sftp.ListenPort = GetIntValue(config["sftpListenPort"]);
    if (config.ContainsKey("sftpTargetHost")) appConfig.Protocols.Sftp.TargetHost = GetStringValue(config["sftpTargetHost"]);
    if (config.ContainsKey("sftpTargetPort")) appConfig.Protocols.Sftp.TargetPort = GetIntValue(config["sftpTargetPort"]);
    
    // PostgreSQL
    if (config.ContainsKey("postgresqlEnabled")) appConfig.Protocols.Postgresql.Enabled = GetBoolValue(config["postgresqlEnabled"]);
    if (isDeviceA && config.ContainsKey("postgresqlListenPort")) appConfig.Protocols.Postgresql.ListenPort = GetIntValue(config["postgresqlListenPort"]);
    if (config.ContainsKey("postgresqlTargetHost")) appConfig.Protocols.Postgresql.TargetHost = GetStringValue(config["postgresqlTargetHost"]);
    if (config.ContainsKey("postgresqlTargetPort")) appConfig.Protocols.Postgresql.TargetPort = GetIntValue(config["postgresqlTargetPort"]);
    
    // Security
    if (config.ContainsKey("enableVirusScan")) iniConfig.Security.EnableVirusScan = GetBoolValue(config["enableVirusScan"]);
    if (config.ContainsKey("enableDeepInspection")) appConfig.Security.EnableDeepInspection = GetBoolValue(config["enableDeepInspection"]);
    if (config.ContainsKey("scanTimeout")) appConfig.Security.ScanTimeout = GetIntValue(config["scanTimeout"]);
    if (config.ContainsKey("quarantinePath")) iniConfig.Security.QuarantinePath = GetStringValue(config["quarantinePath"]);
    if (config.ContainsKey("yaraRulesPath")) appConfig.Security.YaraRulesPath = GetStringValue(config["yaraRulesPath"]);
    
    // Performance
    if (config.ContainsKey("receiveBufferSize")) appConfig.Performance.ReceiveBufferSize = GetIntValue(config["receiveBufferSize"]);
    if (config.ContainsKey("maxConcurrentSessions")) appConfig.Performance.MaxConcurrentSessions = GetIntValue(config["maxConcurrentSessions"]);
    if (config.ContainsKey("enableZeroCopy")) appConfig.Performance.EnableZeroCopy = GetBoolValue(config["enableZeroCopy"]);
    if (config.ContainsKey("maxMemoryMB")) iniConfig.Performance.MaxMemoryMB = GetIntValue(config["maxMemoryMB"]);
    if (config.ContainsKey("bufferSize")) iniConfig.Performance.BufferSize = GetIntValue(config["bufferSize"]);
    
    // Redundancy
    if (config.ContainsKey("heartbeatInterval")) iniConfig.Redundancy.HeartbeatInterval = GetIntValue(config["heartbeatInterval"]);
    if (config.ContainsKey("failoverTimeout")) iniConfig.Redundancy.FailoverTimeout = GetIntValue(config["failoverTimeout"]);
    if (config.ContainsKey("dataSyncMode")) iniConfig.Redundancy.DataSyncMode = GetStringValue(config["dataSyncMode"]);
}

// Helper methods for safe type conversion
static string GetStringValue(JsonElement element)
{
    return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
}

static int GetIntValue(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Number)
        return element.GetInt32();
    if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out int result))
        return result;
    return 0;
}

static bool GetBoolValue(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
        return element.GetBoolean();
    if (element.ValueKind == JsonValueKind.String)
    {
        var str = element.GetString()?.ToLower();
        return str == "true" || str == "1" || str == "yes";
    }
    return false;
}
