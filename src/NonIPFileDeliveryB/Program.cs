using NonIpFileDelivery.Core;
using NonIpFileDelivery.Protocols;
using NonIpFileDelivery.Security;
using Serilog;
using Microsoft.Extensions.Configuration;

namespace NonIpFileDelivery;

/// <summary>
/// 非IP送受信機B のメインエントリーポイント
/// FTP/SFTP/PostgreSQL対応版 (サーバー側)
/// Raw Ethernetから受信し、実際のサーバーに接続する
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        // ロギング初期化
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File("logs/non-ip-file-delivery-b-.log",
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("========================================");
        Log.Information("Non-IP File Delivery System B Starting...");
        Log.Information("Version: 1.0.0 - PostgreSQL/SFTP Support");
        Log.Information("Role: Server-side (B) - Receiver");
        Log.Information("========================================");

        try
        {
            // 設定読み込み
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.b.json", optional: true)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            var interfaceName = config["Network:InterfaceName"] ?? throw new Exception("InterfaceName not configured");
            var remoteMac = config["Network:RemoteMacAddress"] ?? throw new Exception("RemoteMacAddress not configured");
            var yaraRulesPath = config["Security:YaraRulesPath"] ?? "rules/*.yar";

            // コンポーネント初期化
            using var transceiver = new RawEthernetTransceiver(interfaceName, remoteMac);
            
            // YARA ルールファイルの検索
            string[] yaraRules = Array.Empty<string>();
            try
            {
                var yaraPattern = yaraRulesPath.Replace("*.yar", "");
                if (Directory.Exists(yaraPattern.TrimEnd('/', '\\')))
                {
                    yaraRules = Directory.GetFiles(yaraPattern, "*.yar", SearchOption.AllDirectories);
                }
                if (yaraRules.Length == 0)
                {
                    Log.Warning("No YARA rules found at {YaraRulesPath}, security inspection will be limited", yaraRulesPath);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load YARA rules from {YaraRulesPath}", yaraRulesPath);
            }

            using var inspector = new SecurityInspector(yaraRules);
            
            // プロトコルプロキシ初期化（B側）
            var proxies = new List<IDisposable>();

            // FTPプロキシ (B側)
            if (config.GetValue<bool>("Protocols:Ftp:Enabled"))
            {
                var ftpProxyB = new FtpProxyB(
                    transceiver,
                    inspector,
                    config["Protocols:Ftp:TargetHost"] ?? "192.168.1.100",
                    config.GetValue<int>("Protocols:Ftp:TargetPort"));
                
                proxies.Add(ftpProxyB);
                await ftpProxyB.StartAsync();
                Log.Information("FTP Proxy B enabled");
            }

            // SFTPプロキシ (B側)
            if (config.GetValue<bool>("Protocols:Sftp:Enabled"))
            {
                var sftpProxyB = new SftpProxyB(
                    transceiver,
                    inspector,
                    config["Protocols:Sftp:TargetHost"] ?? "192.168.1.100",
                    config.GetValue<int>("Protocols:Sftp:TargetPort"));
                
                proxies.Add(sftpProxyB);
                await sftpProxyB.StartAsync();
                Log.Information("SFTP Proxy B enabled");
            }

            // PostgreSQLプロキシ (B側)
            if (config.GetValue<bool>("Protocols:Postgresql:Enabled"))
            {
                var pgProxyB = new PostgreSqlProxyB(
                    transceiver,
                    inspector,
                    config["Protocols:Postgresql:TargetHost"] ?? "192.168.1.100",
                    config.GetValue<int>("Protocols:Postgresql:TargetPort"));
                
                proxies.Add(pgProxyB);
                await pgProxyB.StartAsync();
                Log.Information("PostgreSQL Proxy B enabled");
            }

            // Raw Ethernet送受信開始
            transceiver.Start();

            Log.Information("All services started successfully");
            Log.Information("Active Protocols: FTP={Ftp}, SFTP={Sftp}, PostgreSQL={Pg}",
                config.GetValue<bool>("Protocols:Ftp:Enabled"),
                config.GetValue<bool>("Protocols:Sftp:Enabled"),
                config.GetValue<bool>("Protocols:Postgresql:Enabled"));
            Log.Information("Waiting for Raw Ethernet packets from Transceiver A...");
            Log.Information("Press Ctrl+C to shutdown...");

            // シャットダウン待機
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Log.Information("Shutdown signal received");
            };

            await Task.Delay(Timeout.Infinite, cts.Token);

            // クリーンアップ
            foreach (var proxy in proxies)
            {
                proxy.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("System shutdown initiated");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error occurred");
            return;
        }
        finally
        {
            Log.Information("Non-IP File Delivery System B stopped");
            Log.CloseAndFlush();
        }
    }
}
