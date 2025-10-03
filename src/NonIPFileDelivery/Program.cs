using NonIpFileDelivery.Core;
using NonIpFileDelivery.Security;
using Serilog;
using Microsoft.Extensions.Configuration;

namespace NonIpFileDelivery;

class Program
{
    static async Task Main(string[] args)
    {
        // ロギング初期化
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File("logs/non-ip-delivery-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        Log.Information("========================================");
        Log.Information("Non-IP File Delivery System (Secure Edition)");
        Log.Information("========================================");

        try
        {
            // 設定読み込み
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false)
                .AddEnvironmentVariables()
                .Build();

            var interfaceName = config["Network:InterfaceName"] ?? throw new Exception("InterfaceName not configured");
            var remoteMac = config["Network:RemoteMacAddress"] ?? throw new Exception("RemoteMacAddress not configured");
            var keyFile = config["Security:Encryption:KeyFile"] ?? "keys/master.key";
            var keyPassword = config["Security:Encryption:KeyPassword"] ?? throw new Exception("KeyPassword not configured");

            // 暗号化エンジン初期化
            CryptoEngine cryptoEngine;
            if (File.Exists(keyFile))
            {
                Log.Information("Loading existing master key from {KeyFile}", keyFile);
                cryptoEngine = CryptoEngine.LoadKeyFromFile(keyFile, keyPassword);
            }
            else
            {
                Log.Information("Generating new master key and saving to {KeyFile}", keyFile);
                cryptoEngine = new CryptoEngine(keyPassword);
                
                Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
                cryptoEngine.SaveKeyToFile(keyFile, keyPassword);
            }

            // セキュアトランシーバー初期化
            using var transceiver = new SecureEthernetTransceiver(interfaceName, remoteMac, cryptoEngine);
            transceiver.Start();

            Log.Information("Secure transceiver started successfully");
            Log.Information("Press Ctrl+C to shutdown...");

            // シャットダウン待機
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Log.Information("Shutdown signal received");
            };

            // ハートビート送信デモ
            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var heartbeat = System.Text.Encoding.UTF8.GetBytes("HEARTBEAT");
                    await transceiver.SendAsync(heartbeat, SecureFrame.ProtocolType.Heartbeat, cancellationToken: cts.Token);
                    
                    Log.Debug("Sent heartbeat");
                    await Task.Delay(5000, cts.Token);
                }
            });

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