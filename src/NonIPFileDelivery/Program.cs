using NonIpFileDelivery.Core;
using NonIpFileDelivery.Protocols;
using NonIpFileDelivery.Security;
using Serilog;
using Microsoft.Extensions.Configuration;

namespace NonIpFileDelivery;

/// <summary>
/// 非IP送受信機のメインエントリーポイント
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        // ロギング初期化
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File("logs/non-ip-file-delivery-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        Log.Information("========================================");
        Log.Information("Non-IP File Delivery System Starting...");
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

            // プロトコル有効化設定
            var ftpEnabled = config.GetValue<bool>("Protocols:Ftp:Enabled", true);
            var sftpEnabled = config.GetValue<bool>("Protocols:Sftp:Enabled", false);
            var postgresqlEnabled = config.GetValue<bool>("Protocols:Postgresql:Enabled", false);

            // コンポーネント初期化
            using var transceiver = new RawEthernetTransceiver(interfaceName, remoteMac);
            using var inspector = new SecurityInspector(Directory.GetFiles(yaraRulesPath));

            // プロキシサービス初期化
            FtpProxy? ftpProxy = null;
            SftpProxy? sftpProxy = null;
            PostgresqlProxy? postgresqlProxy = null;

            if (ftpEnabled)
            {
                ftpProxy = new FtpProxy(
                    transceiver,
                    inspector,
                    config.GetValue<int>("Protocols:Ftp:ListenPort", 21),
                    config["Protocols:Ftp:TargetHost"] ?? "192.168.1.100",
                    config.GetValue<int>("Protocols:Ftp:TargetPort", 21)
                );
            }

            if (sftpEnabled)
            {
                sftpProxy = new SftpProxy(
                    transceiver,
                    inspector,
                    config.GetValue<int>("Protocols:Sftp:ListenPort", 22),
                    config["Protocols:Sftp:TargetHost"] ?? "192.168.1.100",
                    config.GetValue<int>("Protocols:Sftp:TargetPort", 22),
                    config["Protocols:Sftp:Username"] ?? "sftpuser",
                    config["Protocols:Sftp:Password"] ?? "password"
                );
            }

            if (postgresqlEnabled)
            {
                postgresqlProxy = new PostgresqlProxy(
                    transceiver,
                    inspector,
                    config.GetValue<int>("Protocols:Postgresql:ListenPort", 5432),
                    config["Protocols:Postgresql:TargetHost"] ?? "192.168.1.100",
                    config.GetValue<int>("Protocols:Postgresql:TargetPort", 5432)
                );
            }

            // 起動
            transceiver.Start();

            if (ftpEnabled && ftpProxy != null)
            {
                await ftpProxy.StartAsync();
                Log.Information("FTP Proxy service started");
            }

            if (sftpEnabled && sftpProxy != null)
            {
                await sftpProxy.StartAsync();
                Log.Information("SFTP Proxy service started");
            }

            if (postgresqlEnabled && postgresqlProxy != null)
            {
                await postgresqlProxy.StartAsync();
                Log.Information("PostgreSQL Proxy service started");
            }

            Log.Information("All services started successfully");
            Log.Information("Enabled protocols: FTP={FtpEnabled}, SFTP={SftpEnabled}, PostgreSQL={PostgresqlEnabled}",
                ftpEnabled, sftpEnabled, postgresqlEnabled);
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
            ftpProxy?.Dispose();
            sftpProxy?.Dispose();
            postgresqlProxy?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error occurred");
            return;
        }
        finally
        {
            // 脅威統計を出力
            Log.Information("Non-IP File Delivery System stopped");
            Log.CloseAndFlush();
        }
    }
}