using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace NonIpFileDelivery.Security;

/// <summary>
/// AES-256-GCM認証付き暗号化エンジン
/// Raw Ethernetフレームの機密性・完全性・真正性を保証
/// </summary>
public class CryptoEngine : IDisposable
{
    private readonly byte[] _masterKey;
    private readonly RandomNumberGenerator _rng;
    private long _nonceCounter;
    private readonly object _nonceLock = new();
    
    // AES-GCMパラメータ
    private const int KEY_SIZE_BYTES = 32;        // AES-256
    private const int NONCE_SIZE_BYTES = 12;      // GCM推奨値
    private const int TAG_SIZE_BYTES = 16;        // 128ビット認証タグ
    private const int SALT_SIZE_BYTES = 32;       // PBKDF2ソルト
    private const int PBKDF2_ITERATIONS = 100000; // NIST推奨値

    /// <summary>
    /// パスワードから暗号化エンジンを初期化
    /// </summary>
    /// <param name="password">マスターパスワード</param>
    /// <param name="salt">ソルト（nullの場合はランダム生成）</param>
    public CryptoEngine(string password, byte[]? salt = null)
    {
        _rng = RandomNumberGenerator.Create();
        
        // ソルト生成またはロード
        salt ??= GenerateSalt();
        
        // PBKDF2で鍵導出
        _masterKey = DeriveKey(password, salt);
        
        // Nonceカウンターを初期化（タイムスタンプベース）
        _nonceCounter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() << 32;
        
        Log.Information("CryptoEngine initialized with AES-256-GCM");
    }

    /// <summary>
    /// 既存の鍵から暗号化エンジンを初期化（鍵ローテーション用）
    /// </summary>
    /// <param name="masterKey">32バイトのマスターキー</param>
    public CryptoEngine(byte[] masterKey)
    {
        if (masterKey.Length != KEY_SIZE_BYTES)
            throw new ArgumentException($"Master key must be {KEY_SIZE_BYTES} bytes", nameof(masterKey));

        _masterKey = new byte[KEY_SIZE_BYTES];
        Array.Copy(masterKey, _masterKey, KEY_SIZE_BYTES);
        
        _rng = RandomNumberGenerator.Create();
        _nonceCounter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() << 32;
        
        Log.Information("CryptoEngine initialized with provided master key");
    }

    /// <summary>
    /// データを暗号化（認証タグ付き）
    /// </summary>
    /// <param name="plaintext">平文データ</param>
    /// <param name="associatedData">認証対象の関連データ（オプション）</param>
    /// <returns>暗号化データ（Nonce + Ciphertext + Tag）</returns>
    public byte[] Encrypt(byte[] plaintext, byte[]? associatedData = null)
    {
        if (plaintext == null || plaintext.Length == 0)
            throw new ArgumentException("Plaintext cannot be null or empty", nameof(plaintext));

        try
        {
            // 一意なNonceを生成（カウンター方式）
            var nonce = GenerateNonce();
            
            // 暗号化バッファ確保
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TAG_SIZE_BYTES];

            // AES-GCM暗号化
            using var aesGcm = new AesGcm(_masterKey, TAG_SIZE_BYTES);
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            // Nonce + Ciphertext + Tag を結合
            var result = new byte[NONCE_SIZE_BYTES + ciphertext.Length + TAG_SIZE_BYTES];
            Buffer.BlockCopy(nonce, 0, result, 0, NONCE_SIZE_BYTES);
            Buffer.BlockCopy(ciphertext, 0, result, NONCE_SIZE_BYTES, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, result, NONCE_SIZE_BYTES + ciphertext.Length, TAG_SIZE_BYTES);

            Log.Debug("Encrypted {PlaintextSize} bytes -> {CiphertextSize} bytes", 
                plaintext.Length, result.Length);

            return result;
        }
        catch (ArgumentNullException ex)
        {
            Log.Error(ex, "Encryption failed: Null argument");
            throw new CryptographicException("Encryption failed: Invalid null input", ex);
        }
        catch (CryptographicException ex)
        {
            Log.Error(ex, "Cryptographic operation failed during encryption");
            throw;
        }
        catch (OutOfMemoryException ex)
        {
            Log.Error(ex, "Out of memory during encryption");
            throw new CryptographicException("Encryption failed: Insufficient memory", ex);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error during encryption");
            throw new CryptographicException("Encryption operation failed", ex);
        }
    }

    /// <summary>
    /// データを復号化（認証タグ検証付き）
    /// </summary>
    /// <param name="encryptedData">暗号化データ（Nonce + Ciphertext + Tag）</param>
    /// <param name="associatedData">認証対象の関連データ（オプション）</param>
    /// <returns>復号化された平文</returns>
    /// <exception cref="CryptographicException">認証タグ検証失敗時</exception>
    public byte[] Decrypt(byte[] encryptedData, byte[]? associatedData = null)
    {
        if (encryptedData == null || encryptedData.Length < NONCE_SIZE_BYTES + TAG_SIZE_BYTES)
            throw new ArgumentException("Encrypted data is too short", nameof(encryptedData));

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

            // 復号化バッファ確保
            var plaintext = new byte[ciphertextLength];

            // AES-GCM復号化（認証タグ検証を含む）
            using var aesGcm = new AesGcm(_masterKey, TAG_SIZE_BYTES);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

            Log.Debug("Decrypted {CiphertextSize} bytes -> {PlaintextSize} bytes", 
                encryptedData.Length, plaintext.Length);

            return plaintext;
        }
        catch (CryptographicException ex)
        {
            Log.Error(ex, "Decryption failed - authentication tag validation failed (possible tampering)");
            throw;
        }
        catch (ArgumentException ex)
        {
            Log.Error(ex, "Decryption failed: Invalid argument (nonce/ciphertext/tag size)");
            throw new CryptographicException("Decryption failed: Invalid encrypted data format", ex);
        }
        catch (OutOfMemoryException ex)
        {
            Log.Error(ex, "Out of memory during decryption");
            throw new CryptographicException("Decryption failed: Insufficient memory", ex);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error during decryption");
            throw new CryptographicException("Decryption operation failed", ex);
        }
    }

    /// <summary>
    /// PBKDF2でパスワードから鍵を導出
    /// </summary>
    private byte[] DeriveKey(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            password, 
            salt, 
            PBKDF2_ITERATIONS, 
            HashAlgorithmName.SHA256);
        
        var key = pbkdf2.GetBytes(KEY_SIZE_BYTES);
        
        Log.Debug("Derived {KeySize}-byte key using PBKDF2 with {Iterations} iterations", 
            KEY_SIZE_BYTES, PBKDF2_ITERATIONS);
        
        return key;
    }

    /// <summary>
    /// 暗号学的に安全なソルトを生成
    /// </summary>
    public byte[] GenerateSalt()
    {
        var salt = new byte[SALT_SIZE_BYTES];
        _rng.GetBytes(salt);
        return salt;
    }

    /// <summary>
    /// 一意なNonceを生成（カウンターベース）
    /// </summary>
    private byte[] GenerateNonce()
    {
        lock (_nonceLock)
        {
            // カウンターをインクリメント
            var counter = Interlocked.Increment(ref _nonceCounter);
            
            // 12バイトNonce: 8バイト（カウンター） + 4バイト（ランダム）
            var nonce = new byte[NONCE_SIZE_BYTES];
            BitConverter.GetBytes(counter).CopyTo(nonce, 0);
            
            // 追加のランダム性（オーバーフロー対策）
            var randomPart = new byte[4];
            _rng.GetBytes(randomPart);
            randomPart.CopyTo(nonce, 8);
            
            return nonce;
        }
    }

    /// <summary>
    /// マスターキーをエクスポート（鍵ローテーション用）
    /// </summary>
    /// <returns>32バイトのマスターキー</returns>
    public byte[] ExportMasterKey()
    {
        var keyCopy = new byte[KEY_SIZE_BYTES];
        Array.Copy(_masterKey, keyCopy, KEY_SIZE_BYTES);
        
        Log.Warning("Master key exported - handle with extreme care");
        
        return keyCopy;
    }

    /// <summary>
    /// セキュアに鍵をファイルに保存
    /// </summary>
    /// <param name="filePath">保存先ファイルパス</param>
    /// <param name="password">保護パスワード</param>
    public void SaveKeyToFile(string filePath, string password)
    {
        try
        {
            // 鍵を追加のパスワードで暗号化して保存
            var salt = GenerateSalt();
            var protectionKey = DeriveKey(password, salt);
            
            using var aesGcm = new AesGcm(protectionKey, TAG_SIZE_BYTES);
            var nonce = GenerateNonce();
            var ciphertext = new byte[_masterKey.Length];
            var tag = new byte[TAG_SIZE_BYTES];
            
            aesGcm.Encrypt(nonce, _masterKey, ciphertext, tag);
            
            // Salt + Nonce + Ciphertext + Tag
            using var fs = File.Create(filePath);
            fs.Write(salt, 0, salt.Length);
            fs.Write(nonce, 0, nonce.Length);
            fs.Write(ciphertext, 0, ciphertext.Length);
            fs.Write(tag, 0, tag.Length);
            
            // Windows DPAPI追加保護（オプション）
            if (OperatingSystem.IsWindows())
            {
                var fileInfo = new FileInfo(filePath);
                fileInfo.Encrypt(); // NTFS暗号化
            }
            
            Log.Information("Master key saved to {FilePath} (encrypted)", filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save master key to file");
            throw;
        }
    }

    /// <summary>
    /// ファイルから鍵を読み込み
    /// </summary>
    public static CryptoEngine LoadKeyFromFile(string filePath, string password)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            
            // Salt + Nonce + Ciphertext + Tag を読み込み
            var salt = new byte[SALT_SIZE_BYTES];
            var nonce = new byte[NONCE_SIZE_BYTES];
            var ciphertext = new byte[KEY_SIZE_BYTES];
            var tag = new byte[TAG_SIZE_BYTES];
            
            fs.Read(salt, 0, salt.Length);
            fs.Read(nonce, 0, nonce.Length);
            fs.Read(ciphertext, 0, ciphertext.Length);
            fs.Read(tag, 0, tag.Length);
            
            // パスワードで復号化
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, PBKDF2_ITERATIONS, HashAlgorithmName.SHA256);
            var protectionKey = pbkdf2.GetBytes(KEY_SIZE_BYTES);
            
            using var aesGcm = new AesGcm(protectionKey, TAG_SIZE_BYTES);
            var masterKey = new byte[KEY_SIZE_BYTES];
            aesGcm.Decrypt(nonce, ciphertext, tag, masterKey);
            
            Log.Information("Master key loaded from {FilePath}", filePath);
            
            return new CryptoEngine(masterKey);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load master key from file");
            throw new CryptographicException("Key file loading failed", ex);
        }
    }

    public void Dispose()
    {
        // マスターキーをメモリから安全に消去
        if (_masterKey != null)
        {
            Array.Clear(_masterKey, 0, _masterKey.Length);
        }
        
        _rng?.Dispose();
        
        Log.Information("CryptoEngine disposed - master key cleared from memory");
    }
}