using NonIpFileDelivery.Security;
using NonIpFileDelivery.Core;
using NonIpFileDeliveryB.Protocols;
using Serilog;
using System.Net;

namespace NonIpFileDeliveryB;

/// <summary>
/// 非IP送受信機B側アプリケーション（受信側）
/// Raw Ethernetから受信 → TCP/IPでサーバへ転送
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        // ロガー初期化
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File("logs/nonip_b_.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            Log.Information("=== Non-IP File Delivery B (Receiver) Starting ===");

            // 設定（簡略化 - 実運用では設定ファイルから読み込む）
            var interfaceName = "eth1";
            var destinationMac = "00:11:22:33:44:55";
            var ftpServer = "192.168.2.100";
            var ftpPort = 21;
            var sftpServer = "192.168.2.101";
            var sftpPort = 22;
            var pgServer = "192.168.2.102";
            var pgPort = 5432;

            Log.Information("Configuration: Interface={Interface}", interfaceName);

            // 暗号化キー（A側と同じキーを使用する必要がある）
            // 優先順位: 1. 環境変数 2. 設定ファイル 3. エラー
            byte[] cryptoKey;
            var cryptoKeyBase64 = Environment.GetEnvironmentVariable("NONIP_CRYPTO_KEY");
            
            if (!string.IsNullOrEmpty(cryptoKeyBase64))
            {
                // 環境変数から読み込み
                cryptoKey = Convert.FromBase64String(cryptoKeyBase64);
                Log.Information("Crypto key loaded from environment variable");
            }
            else if (File.Exists("crypto.key"))
            {
                // ファイルから読み込み（Base64エンコード）
                var keyContent = await File.ReadAllTextAsync("crypto.key");
                cryptoKey = Convert.FromBase64String(keyContent.Trim());
                Log.Information("Crypto key loaded from crypto.key file");
            }
            else
            {
                Log.Fatal("Crypto key not found. Set NONIP_CRYPTO_KEY environment variable or create crypto.key file");
                Log.Information("To generate a key: openssl rand -base64 32");
                return 1;
            }

            if (cryptoKey.Length != 32)
            {
                Log.Fatal("Invalid crypto key length: {Length} bytes (expected 32 bytes)", cryptoKey.Length);
                return 1;
            }

            // CryptoEngine初期化
            var cryptoEngine = new CryptoEngine(cryptoKey);

            // SecureEthernetTransceiver初期化（受信モード）
            var transceiver = new SecureEthernetTransceiver(
                interfaceName: interfaceName,
                remoteMac: destinationMac,
                cryptoEngine: cryptoEngine,
                receiverMode: true  // B側は受信モード
            );

            Log.Information("SecureEthernetTransceiver initialized in receiver mode");

            // セキュリティインスペクター初期化（簡略化）
            var inspector = new SecurityInspector();

            Log.Information("SecurityInspector initialized");

            // プロトコルプロキシB側を初期化
            var ftpProxyB = new FtpProxyB(
                transceiver,
                inspector,
                targetFtpHost: ftpServer,
                targetFtpPort: ftpPort
            );

            var sftpProxyB = new SftpProxyB(
                transceiver,
                inspector,
                targetSftpHost: sftpServer,
                targetSftpPort: sftpPort
            );

            var pgProxyB = new PostgreSqlProxyB(
                transceiver,
                inspector,
                targetPgHost: pgServer,
                targetPgPort: pgPort
            );

            Log.Information("Protocol proxies initialized");

            // Raw Ethernet受信開始
            await transceiver.StartReceivingAsync();

            // 各プロトコルプロキシ開始
            await Task.WhenAll(
                ftpProxyB.StartAsync(),
                sftpProxyB.StartAsync(),
                pgProxyB.StartAsync()
            );

            Log.Information("All protocol proxies started");
            Log.Information("=== Non-IP File Delivery B is running ===");
            Log.Information("Press Ctrl+C to stop");

            // Ctrl+C待機
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Log.Information("Shutdown requested");
            };

            await Task.Delay(Timeout.Infinite, cts.Token);

            return 0;
        }
        catch (OperationCanceledException)
        {
            Log.Information("Shutdown requested (OperationCanceledException)");
            return 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Fatal(ex, "権限エラー: ファイルやデバイスへのアクセスに失敗しました");
            return 2;
        }
        catch (System.IO.FileNotFoundException ex)
        {
            Log.Fatal(ex, "ファイルが見つかりません: {FileName}", ex.FileName);
            return 3;
        }
        catch (FormatException ex)
        {
            Log.Fatal(ex, "暗号化キーの形式が不正です: {Message}", ex.Message);
            return 4;
        }
        catch (ArgumentException ex)
        {
            Log.Fatal(ex, "引数エラー: {Message}", ex.Message);
            return 5;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "予期しない致命的エラーが発生しました");
            return 1;
        }
        finally
        {
            Log.Information("=== Non-IP File Delivery B Stopped ===");
            Log.CloseAndFlush();
        }
    }
}
