using NonIpFileDelivery.Core;
using NonIpFileDelivery.Protocols;
using NonIpFileDelivery.Security;
using Serilog;
using Microsoft.Extensions.Configuration;

namespace NonIpFileDelivery;

/// <summary>
/// 非IP送受信機のメインエントリーポイント（統合版）
/// FTP/SFTP/PostgreSQL完全対応
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        // ロギング初期化
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                "logs/non-ip-file-delivery-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("========================================");
        Log.Information("Non-IP File Delivery System v1.0");
        Log.Information("FTP/SFTP/PostgreSQL Proxy with Security Inspection");
        Log.Information("========================================");

        try
        {
            // 設定読み込み
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            var interfaceName = config["Network:InterfaceName"] 
                ?? throw new Exception("InterfaceName not configured");
            var remoteMac = config["Network:RemoteMacAddress"] 
                ?? throw new Exception("RemoteMacAddress not configured");
            var yaraRulesPath = config["Security:YaraRulesPath"] ?? "rules/*.yar";

            // YARAルールファイル取得
            var yaraFiles = Directory.Exists(Path.GetDirectoryName(yaraRulesPath))
                ? Directory.GetFiles(Path.GetDirectoryName(yaraRulesPath)!, 
                                   Path.GetFileName(yaraRulesPath))
                : Array.Empty<string>();

            if (yaraFiles.Length == 0)
            {
                Log.Warning("No YARA rules found at {Path}. Security inspection will be limited.",
                    yaraRulesPath);
            }

            // コンポーネント初期化
            Log.Information("Initializing components...");

            using var transceiver = new RawEthernetTransceiver(interfaceName, remoteMac);
            using var inspector = yaraFiles.Length > 0 
                ? new SecurityInspector(yaraFiles) 
                : null;

            // プロトコルプロキシ初期化
            var ftpEnabled = config.GetValue<bool>("Protocols:Ftp:Enabled", true);
            var sftpEnabled = config.GetValue<bool>("Protocols:Sftp:Enabled", true);
            var postgresqlEnabled = config.GetValue<bool>("Protocols:Postgresql:Enabled", true);

            using var ftpProxy = ftpEnabled && inspector != null
                ? new FtpProxy(
                    transceiver,
                    inspector,
                    config.GetValue<int>("Protocols:Ftp:ListenPort", 21),
                    config["Protocols:Ftp:TargetHost"] ?? "192.168.1.100",
                    config.GetValue<int>("Protocols:Ftp:TargetPort", 21))
                : null;

            using var sftpProxy = sftpEnabled && inspector != null
                ? new SftpProxy(
                    transceiver,
                    inspector,
                    config.GetValue<int>("Protocols:Sftp:ListenPort", 22),
                    config["Protocols:Sftp:TargetHost"] ?? "192.168.1.100",
                    config.GetValue<int>("Protocols:Sftp:TargetPort", 22))
                : null;

            using var postgresqlProxy = postgresqlEnabled && inspector != null
                ? new PostgreSqlProxy(
                    transceiver,
                    inspector,
                    config.GetValue<int>("Protocols:Postgresql:ListenPort", 5432),
                    config["Protocols:Postgresql:TargetHost"] ?? "192.168.1.100",
                    config.GetValue<int>("Protocols:Postgresql:TargetPort", 5432))
                : null;

            // 起動
            Log.Information("Starting services...");
            transceiver.Start();

            var tasks = new List<Task>();

            if (ftpProxy != null)
            {
                tasks.Add(ftpProxy.StartAsync());
                Log.Information("✓ FTP Proxy enabled on port {Port}", 
                    config.GetValue<int>("Protocols:Ftp:ListenPort", 21));
            }

            if (sftpProxy != null)
            {
                tasks.Add(sftpProxy.StartAsync());
                Log.Information("✓ SFTP Proxy enabled on port {Port}",
                    config.GetValue<int>("Protocols:Sftp:ListenPort", 22));
            }

            if (postgresqlProxy != null)
            {
                tasks.Add(postgresqlProxy.StartAsync());
                Log.Information("✓ PostgreSQL Proxy enabled on port {Port}",
                    config.GetValue<int>("Protocols:Postgresql:ListenPort", 5432));
            }

            await Task.WhenAll(tasks);

            Log.Information("========================================");
            Log.Information("All services started successfully");
            Log.Information("System ready for secure file transfer");
            Log.Information("Press Ctrl+C to shutdown...");
            Log.Information("========================================");

            // シャットダウン待機
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Log.Information("Shutdown signal received, stopping services...");
            };

            await Task.Delay(Timeout.Infinite, cts.Token);
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
            Log.Information("========================================");
            Log.Information("Non-IP File Delivery System stopped");
            Log.Information("========================================");
            Log.CloseAndFlush();
        }
    }
}