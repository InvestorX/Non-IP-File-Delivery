using NonIpFileDelivery.Core;
using NonIpFileDelivery.Protocols;
using NonIpFileDelivery.Security;
using Serilog;
using Microsoft.Extensions.Configuration;

namespace NonIpFileDelivery;

/// <summary>
/// 非IP送受信機のメインエントリーポイント
/// FTP/SFTP/PostgreSQL対応版
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        // ロギング初期化
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File("logs/non-ip-file-delivery-.log",
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("========================================");
        Log.Information("Non-IP File Delivery System Starting...");
        Log.Information("Version: 1.0.0 - PostgreSQL/SFTP Support");
        Log.Information("========================================");

        try
        {
            // 設定読み込み
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            var interfaceName = config["Network:InterfaceName"] ?? throw new Exception("InterfaceName not configured");
            var remoteMac = config["Network:RemoteMacAddress"] ?? throw new Exception("RemoteMacAddress not configured");
            var yaraRulesPath = config["Security:YaraRulesPath"] ?? "rules/*.yar";

            // コンポーネント初期化
            using var transceiver = new RawEthernetTransceiver(interfaceName, remoteMac);
            using var inspector = new SecurityInspector(Directory.GetFiles(yaraRulesPath));
            
            // プロトコルプロキシ初期化
            var proxies = new List<IDisposable>();

            // FTPプロキシ
            if (config.GetValue<bool>("Protocols:Ftp:Enabled"))
            {
                var ftpProxy = new FtpProxy(
                    transceiver,
                    inspector,
                    config.GetValue<int>("Protocols:Ftp:ListenPort"),
                    config["Protocols:Ftp:TargetHost"] ?? "192.168.1.100",
                    config.GetValue<int>("Protocols:Ftp:TargetPort"));
                
                proxies.Add(ftpProxy);
                await ftpProxy.StartAsync();
                Log.Information("FTP Proxy enabled");
            }

            // SFTPプロキシ
            if (config.GetValue<bool>("Protocols:Sftp:Enabled"))
            {
                var sftpProxy = new SftpProxy(
                    transceiver,
                    inspector,
                    config.GetValue<int>("Protocols:Sftp:ListenPort"),
                    config["Protocols:Sftp:TargetHost"] ?? "192.168.1.100",
                    config.GetValue<int>("Protocols:Sftp:TargetPort"));
                
                proxies.Add(sftpProxy);
                await sftpProxy.StartAsync();
                Log.Information("SFTP Proxy enabled");
            }

            // PostgreSQLプロキシ
            if (config.GetValue<bool>("Protocols:Postgresql:Enabled"))
            {
                var pgProxy = new PostgreSqlProxy(
                    transceiver,
                    inspector,
                    config.GetValue<int>("Protocols:Postgresql:ListenPort"),
                    config["Protocols:Postgresql:TargetHost"] ?? "192.168.1.100",
                    config.GetValue<int>("Protocols:Postgresql:TargetPort"));
                
                proxies.Add(pgProxy);
                await pgProxy.StartAsync();
                Log.Information("PostgreSQL Proxy enabled");
            }

            // Raw Ethernet送受信開始
            transceiver.Start();

            Log.Information("All services started successfully");
            Log.Information("Active Protocols: FTP={Ftp}, SFTP={Sftp}, PostgreSQL={Pg}",
                config.GetValue<bool>("Protocols:Ftp:Enabled"),
                config.GetValue<bool>("Protocols:Sftp:Enabled"),
                config.GetValue<bool>("Protocols:Postgresql:Enabled"));
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
            Log.Information("Non-IP File Delivery System stopped");
            Log.CloseAndFlush();
        }
    }
}