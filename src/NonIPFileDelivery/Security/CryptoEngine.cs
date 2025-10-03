using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace NonIpFileDelivery.Security;

/// <summary>
/// AES-256-GCM暗号化エンジン
/// Raw Ethernetフレームの暗号化・復号化・認証を担当
/// </summary>
public class CryptoEngine : IDisposable
{
    private readonly AesGcm _aesGcm;
    private readonly byte[] _masterKey;
    private readonly object _nonceLock = new();
    private ulong _nonceCounter;

    // AES-GCM標準仕様
    private const int KEY_SIZE_BYTES = 32;      // 256ビット
    private const int NONCE_SIZE_BYTES = 12;    // 96ビット（推奨）
    private const int TAG_SIZE_BYTES = 16;      // 128ビット認証タグ

    /// <summary>
    /// 暗号化エンジンを初期化
    /// </summary>
    /// <param name="masterKey">32バイトのマスターキー（AES-256）</param>
    /// <param name="initialNonce">初期Nonce値（リプレイ攻撃対策）</param>
    public CryptoEngine(byte[] masterKey, ulong initialNonce = 0)
    {
        if (masterKey == null || masterKey.Length != KEY_SIZE_BYTES)
        {
            throw new ArgumentException($"Master key must be exactly {KEY_SIZE_BYTES} bytes (256 bits)", nameof(masterKey));
        }

        _masterKey = new byte[KEY_SIZE_BYTES];
        Array.Copy(masterKey, _masterKey, KEY_SIZE_BYTES);

        _aesGcm = new AesGcm(_masterKey, TAG_SIZE_BYTES);
        _nonceCounter = initialNonce;

        Log.Information("CryptoEngine initialized with AES-256-GCM (Nonce start: {NonceStart})", initialNonce);
    }

    /// <summary>
    /// Windows DPAPIで保護されたキーファイルから初期化
    /// </summary>
    /// <param name="keyFilePath">暗号化されたキーファイルのパス</param>
    /// <returns>CryptoEngineインスタンス</returns>
    public static CryptoEngine FromProtectedKeyFile(string keyFilePath)
    {
        if (!File.Exists(keyFilePath))
        {
            throw new FileNotFoundException("Protected key file not found", keyFilePath);
        }

        try
        {
            // DPAPIで保護されたキーを読み込み
            var encryptedKey = File.ReadAllBytes(keyFilePath);
            var decryptedKey = ProtectedData.Unprotect(
                encryptedKey,
                null, // オプショナルエントロピー（必要に応じて設定）
                DataProtectionScope.LocalMachine
            );

            if (decryptedKey.Length != KEY_SIZE_BYTES)
            {
                throw new CryptographicException($"Invalid key size: {decryptedKey.Length} bytes (expected {KEY_SIZE_BYTES})");
            }

            Log.Information("Loaded protected key from file: {KeyFile}", keyFilePath);
            return new CryptoEngine(decryptedKey);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load protected key file: {KeyFile}", keyFilePath);
            throw;
        }
    }

    /// <summary>
    /// 新しいマスターキーを生成してDPAPI保護付きで保存
    /// </summary>
    /// <param name="keyFilePath">保存先パス</param>
    public static void GenerateAndSaveProtectedKey(string keyFilePath)
    {
        // 暗号学的に安全な乱数生成器で256ビットキーを生成
        var masterKey = new byte[KEY_SIZE_BYTES];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(masterKey);
        }

        // Windows DPAPIで保護
        var encryptedKey = ProtectedData.Protect(
            masterKey,
            null,
            DataProtectionScope.LocalMachine
        );

        File.WriteAllBytes(keyFilePath, encryptedKey);

        // キーのハッシュをログに記録（キー本体はログに出さない）
        var keyHash = Convert.ToBase64String(SHA256.HashData(masterKey));
        Log.Information("Generated and saved protected key: {KeyFile}, KeyHash={KeyHash}", keyFilePath, keyHash);

        // メモリからキーをクリア
        Array.Clear(masterKey, 0, masterKey.Length);
    }

    /// <summary>
    /// データを暗号化
    /// </summary>
    /// <param name="plaintext">平文データ</param>
    /// <param name="associatedData">関連データ（認証対象だが暗号化しない）</param>
    /// <returns>暗号化されたデータ（Nonce + Ciphertext + Tag）</returns>
    public byte[] Encrypt(byte[] plaintext, byte[]? associatedData = null)
    {
        if (plaintext == null || plaintext.Length == 0)
        {
            throw new ArgumentException("Plaintext cannot be null or empty", nameof(plaintext));
        }

        // Nonceを生成（リプレイ攻撃対策）
        var nonce = GenerateNonce();

        // 出力バッファ: [Nonce(12) | Ciphertext(N) | Tag(16)]
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TAG_SIZE_BYTES];

        try
        {
            _aesGcm.Encrypt(
                nonce,
                plaintext,
                ciphertext,
                tag,
                associatedData
            );

            // Nonce + Ciphertext + Tagを結合
            var result = new byte[NONCE_SIZE_BYTES + ciphertext.Length + TAG_SIZE_BYTES];
            Buffer.BlockCopy(nonce, 0, result, 0, NONCE_SIZE_BYTES);
            Buffer.BlockCopy(ciphertext, 0, result, NONCE_SIZE_BYTES, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, result, NONCE_SIZE_BYTES + ciphertext.Length, TAG_SIZE_BYTES);

            Log.Debug("Encrypted {PlaintextSize} bytes -> {CiphertextSize} bytes (Nonce: {Nonce})",
                plaintext.Length,
                result.Length,
                BitConverter.ToUInt64(nonce, 0));

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Encryption failed");
            throw;
        }
    }

    /// <summary>
    /// データを復号化
    /// </summary>
    /// <param name="encryptedData">暗号化されたデータ（Nonce + Ciphertext + Tag）</param>
    /// <param name="associatedData">関連データ（暗号化時と同じもの）</param>
    /// <returns>復号化された平文データ</returns>
    public byte[] Decrypt(byte[] encryptedData, byte[]? associatedData = null)
    {
        if (encryptedData == null || encryptedData.Length < NONCE_SIZE_BYTES + TAG_SIZE_BYTES)
        {
            throw new ArgumentException("Invalid encrypted data", nameof(encryptedData));
        }

        try
        {
            // Nonce、Ciphertext、Tagを分離
            var nonce = new byte[NONCE_SIZE_BYTES];
            var ciphertextLength = encryptedData.Length - NONCE_SIZE_BYTES - TAG_SIZE_BYTES;
            var ciphertext = new byte[ciphertextLength];
            var tag = new byte[TAG_SIZE_BYTES];

            Buffer.BlockCopy(encryptedData, 0, nonce, 0, NONCE_SIZE_BYTES);
            Buffer.BlockCopy(encryptedData, NONCE_SIZE_BYTES, ciphertext, 0, ciphertextLength);
            Buffer.BlockCopy(encryptedData, NONCE_SIZE_BYTES + ciphertextLength, tag, 0, TAG_SIZE_BYTES);

            // Nonce検証（リプレイ攻撃対策）
            if (!ValidateNonce(nonce))
            {
                Log.Warning("Replay attack detected: Invalid nonce {Nonce}", BitConverter.ToUInt64(nonce, 0));
                throw new CryptographicException("Replay attack detected: Invalid or reused nonce");
            }

            var plaintext = new byte[ciphertextLength];

            _aesGcm.Decrypt(
                nonce,
                ciphertext,
                tag,
                plaintext,
                associatedData
            );

            Log.Debug("Decrypted {CiphertextSize} bytes -> {PlaintextSize} bytes (Nonce: {Nonce})",
                encryptedData.Length,
                plaintext.Length,
                BitConverter.ToUInt64(nonce, 0));

            return plaintext;
        }
        catch (CryptographicException ex)
        {
            Log.Error(ex, "Decryption failed: Authentication tag mismatch or corrupted data");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Decryption failed");
            throw;
        }
    }

    /// <summary>
    /// 暗号学的に安全なNonceを生成
    /// </summary>
    private byte[] GenerateNonce()
    {
        lock (_nonceLock)
        {
            _nonceCounter++;

            // Nonce構造: [Counter(8 bytes) | Random(4 bytes)]
            var nonce = new byte[NONCE_SIZE_BYTES];
            BitConverter.GetBytes(_nonceCounter).CopyTo(nonce, 0);

            // 追加のランダム性を付与（衝突回避）
            using (var rng = RandomNumberGenerator.Create())
            {
                var randomPart = new byte[4];
                rng.GetBytes(randomPart);
                randomPart.CopyTo(nonce, 8);
            }

            return nonce;
        }
    }

    /// <summary>
    /// Nonceの妥当性を検証（リプレイ攻撃対策）
    /// </summary>
    /// <param name="nonce">検証対象のNonce</param>
    /// <returns>有効な場合はtrue</returns>
    private bool ValidateNonce(byte[] nonce)
    {
        if (nonce.Length != NONCE_SIZE_BYTES)
        {
            return false;
        }

        var receivedCounter = BitConverter.ToUInt64(nonce, 0);

        lock (_nonceLock)
        {
            // 受信したカウンターが現在のカウンター以下の場合はリプレイ攻撃の可能性
            // （簡易実装: 実運用ではスライディングウィンドウ方式を推奨）
            if (receivedCounter <= _nonceCounter)
            {
                return false;
            }

            // カウンターを更新
            _nonceCounter = Math.Max(_nonceCounter, receivedCounter);
            return true;
        }
    }

    /// <summary>
    /// キーローテーション: 新しいキーで再初期化
    /// </summary>
    /// <param name="newMasterKey">新しい32バイトのマスターキー</param>
    public void RotateKey(byte[] newMasterKey)
    {
        if (newMasterKey == null || newMasterKey.Length != KEY_SIZE_BYTES)
        {
            throw new ArgumentException($"New master key must be exactly {KEY_SIZE_BYTES} bytes", nameof(newMasterKey));
        }

        lock (_nonceLock)
        {
            // 古いキーをクリア
            Array.Clear(_masterKey, 0, _masterKey.Length);

            // 新しいキーをコピー
            Array.Copy(newMasterKey, _masterKey, KEY_SIZE_BYTES);

            // AesGcmインスタンスを再生成
            _aesGcm.Dispose();
            var newAesGcm = new AesGcm(_masterKey, TAG_SIZE_BYTES);

            // Nonceカウンターをリセット
            _nonceCounter = 0;

            Log.Warning("Key rotation completed. All previous encrypted data is now inaccessible.");
        }
    }

    public void Dispose()
    {
        _aesGcm?.Dispose();

        // メモリからキーをクリア（セキュリティ対策）
        if (_masterKey != null)
        {
            Array.Clear(_masterKey, 0, _masterKey.Length);
        }

        Log.Information("CryptoEngine disposed and keys cleared from memory");
    }
}