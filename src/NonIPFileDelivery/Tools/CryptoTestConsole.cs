using NonIpFileDelivery.Security;
using NonIpFileDelivery.Core;
using Serilog;
using System.Diagnostics;

namespace NonIpFileDelivery.Tools;

/// <summary>
/// 暗号化レイヤーのテストコンソールアプリ
/// </summary>
class CryptoTestConsole
{
    static void Main(string[] args)
    {
        // ロギング初期化
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();

        Log.Information("===== Crypto Engine Test Console =====");

        try
        {
            // テスト1: キー生成とDPAPI保護
            TestKeyGeneration();

            // テスト2: 基本的な暗号化・復号化
            TestBasicEncryption();

            // テスト3: リプレイ攻撃検知
            TestReplayAttackDetection();

            // テスト4: フレーム暗号化・復号化
            TestSecureFrameEncryption();

            // テスト5: パフォーマンステスト（2Gbps要件検証）
            TestPerformance();

            Log.Information("===== All Tests Passed =====");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Test failed");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    static void TestKeyGeneration()
    {
        Log.Information("--- Test 1: Key Generation and DPAPI Protection ---");

        var keyFile = "test_master.key";

        // キー生成
        CryptoEngine.GenerateAndSaveProtectedKey(keyFile);
        Log.Information("✓ Key generated and saved");

        // キー読み込み
        using var engine = CryptoEngine.FromProtectedKeyFile(keyFile);
        Log.Information("✓ Key loaded from protected file");

        // クリーンアップ
        File.Delete(keyFile);
    }

    static void TestBasicEncryption()
    {
        Log.Information("--- Test 2: Basic Encryption/Decryption ---");

        var keyFile = "test_basic.key";
        CryptoEngine.GenerateAndSaveProtectedKey(keyFile);

        using var engine = CryptoEngine.FromProtectedKeyFile(keyFile);

        var plaintext = "Hello, Non-IP File Delivery System! 日本語テスト 🍣"u8.ToArray();
        Log.Information("Plaintext: {Size} bytes", plaintext.Length);

        // 暗号化
        var encrypted = engine.Encrypt(plaintext);
        Log.Information("✓ Encrypted: {Size} bytes", encrypted.Length);

        // 復号化
        var decrypted = engine.Decrypt(encrypted);
        Log.Information("✓ Decrypted: {Size} bytes", decrypted.Length);

        // 検証
        if (!plaintext.SequenceEqual(decrypted))
        {
            throw new Exception("Decryption mismatch!");
        }

        Log.Information("✓ Plaintext matches decrypted data");

        File.Delete(keyFile);
    }

    static void TestReplayAttackDetection()
    {
        Log.Information("--- Test 3: Replay Attack Detection ---");

        var keyFile = "test_replay.key";
        CryptoEngine.GenerateAndSaveProtectedKey(keyFile);

        using var engine = CryptoEngine.FromProtectedKeyFile(keyFile);

        var plaintext = "Secret Message"u8.ToArray();

        // 最初の暗号化
        var encrypted1 = engine.Encrypt(plaintext);
        Log.Information("✓ First encryption successful");

        // 正常な復号化
        var decrypted1 = engine.Decrypt(encrypted1);
        Log.Information("✓ First decryption successful");

        // 2回目の暗号化（異なるNonce）
        var encrypted2 = engine.Encrypt(plaintext);
        Log.Information("✓ Second encryption successful");

        // リプレイ攻撃シミュレーション（古いパケットを再送）
        try
        {
            var decryptedReplay = engine.Decrypt(encrypted1);
            Log.Error("✗ Replay attack NOT detected (this is a security issue!)");
            throw new Exception("Replay attack should have been detected");
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            Log.Information("✓ Replay attack detected: {Message}", ex.Message);
        }

        File.Delete(keyFile);
    }

    static void TestSecureFrameEncryption()
    {
        Log.Information("--- Test 4: Secure Ethernet Frame Encryption ---");

        var keyFile = "test_frame.key";
        CryptoEngine.GenerateAndSaveProtectedKey(keyFile);

        using var engine = CryptoEngine.FromProtectedKeyFile(keyFile);

        // FTPコマンドをシミュレート
        var ftpCommand = "USER anonymous\r\n"u8.ToArray();

        // フレーム作成
        var frame = SecureEthernetFrame.CreateEncrypted(
            ftpCommand,
            engine,
            protocolType: 1, // FTP
            sequenceNumber: 12345
        );

        Log.Information("✓ Frame created: Seq={Seq}, Encrypted Size={Size}",
            frame.Header.SequenceNumber,
            frame.EncryptedPayload.Length);

        // シリアライズ
        var serialized = frame.Serialize();
        Log.Information("✓ Frame serialized: {Size} bytes", serialized.Length);

        // デシリアライズ
        var deserialized = SecureEthernetFrame.Deserialize(serialized);
        Log.Information("✓ Frame deserialized");

        // 復号化
        var decrypted = deserialized.DecryptPayload(engine);
        Log.Information("✓ Frame decrypted: {Size} bytes", decrypted.Length);

        // 検証
        if (!ftpCommand.SequenceEqual(decrypted))
        {
            throw new Exception("Frame decryption mismatch!");
        }

        Log.Information("✓ Original payload matches decrypted payload");

        File.Delete(keyFile);
    }

    static void TestPerformance()
    {
        Log.Information("--- Test 5: Performance Test (2Gbps Requirement) ---");

        var keyFile = "test_perf.key";
        CryptoEngine.GenerateAndSaveProtectedKey(keyFile);

        using var engine = CryptoEngine.FromProtectedKeyFile(keyFile);

        // 1MBのテストデータ
        var testData = new byte[1024 * 1024];
        new Random().NextBytes(testData);

        const int iterations = 1000;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            var encrypted = engine.Encrypt(testData);
            var decrypted = engine.Decrypt(encrypted);
        }

        sw.Stop();

        var totalBytes = testData.Length * iterations * 2L; // 暗号化+復号化
        var throughputMbps = (totalBytes * 8.0 / sw.Elapsed.TotalSeconds) / (1024 * 1024);
        var throughputGbps = throughputMbps / 1024;

        Log.Information("Total processed: {TotalGB:F2} GB in {Elapsed:F2} seconds",
            totalBytes / (1024.0 * 1024 * 1024),
            sw.Elapsed.TotalSeconds);

        Log.Information("Throughput: {ThroughputMbps:F2} Mbps ({ThroughputGbps:F2} Gbps)",
            throughputMbps,
            throughputGbps);

        if (throughputGbps >= 2.0)
        {
            Log.Information("✓ Performance requirement met (>= 2Gbps)");
        }
        else
        {
            Log.Warning("✗ Performance requirement NOT met (< 2Gbps)");
            Log.Warning("  Consider enabling hardware AES-NI acceleration");
        }

        File.Delete(keyFile);
    }
}