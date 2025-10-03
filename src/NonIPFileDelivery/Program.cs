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

            var interfaceName = config["Network:InterfaceName"] ?? throw new Exception("InterfaceName not configured");
            var remoteMac = config["Network:RemoteMacAddress"] ?? throw new Exception("RemoteMacAddress not configured");
            var yaraRulesPath = config["Security:YaraRulesPath"] ?? "rules/*.yar";

            // コンポーネント初期化
            using var transceiver = new RawEthernetTransceiver(interfaceName, remoteMac);
            using var inspector = new SecurityInspector(Directory.GetFiles(yaraRulesPath));
            using var ftpProxy = new FtpProxy(transceiver, inspector);

            // 起動
            transceiver.Start();
            await ftpProxy.StartAsync();

            Log.Information("All services started successfully");
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