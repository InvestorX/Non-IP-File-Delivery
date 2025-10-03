using System;
using System.Security.Cryptography;
using NonIPFileDelivery.Services;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// AES-256-GCM暗号化サービス実装
    /// .NET 8標準のAesGcmクラスを使用
    /// </summary>
    public class CryptoService : ICryptoService
    {
        private readonly ILoggingService _logger;
        private byte[] _key; // 256-bit key (32 bytes)
        private const int NonceSize = 12; // 96-bit nonce
        private const int TagSize = 16;   // 128-bit authentication tag

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="logger">ロギングサービス</param>
        /// <param name="key">暗号化鍵（256-bit、32バイト）。nullの場合は自動生成</param>
        public CryptoService(ILoggingService logger, byte[]? key = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _key = key ?? GenerateKey();
            
            if (_key.Length != 32)
            {
                throw new ArgumentException("Encryption key must be 256-bit (32 bytes)", nameof(key));
            }

            _logger.Info("CryptoService initialized with AES-256-GCM");
        }

        /// <summary>
        /// 256-bit暗号化鍵を生成
        /// </summary>
        private static byte[] GenerateKey()
        {
            var key = new byte[32]; // 256-bit
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(key);
            return key;
        }

        /// <summary>
        /// データを暗号化
        /// </summary>
        public byte[] Encrypt(byte[] plaintext)
        {
            if (plaintext == null || plaintext.Length == 0)
            {
                _logger.Warning("Attempted to encrypt empty data");
                return Array.Empty<byte>();
            }

            try
            {
                var nonce = GenerateNonce();
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[TagSize];

                using var aesGcm = new AesGcm(_key);
                aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

                // フォーマット: [Nonce (12 bytes)][Ciphertext (variable)][Tag (16 bytes)]
                var result = new byte[NonceSize + ciphertext.Length + TagSize];
                Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
                Buffer.BlockCopy(ciphertext, 0, result, NonceSize, ciphertext.Length);
                Buffer.BlockCopy(tag, 0, result, NonceSize + ciphertext.Length, TagSize);

                _logger.Debug($"Encrypted {plaintext.Length} bytes -> {result.Length} bytes");
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Encryption failed: {ex.Message}", ex);
                throw new CryptographicException("Encryption failed", ex);
            }
        }

        /// <summary>
        /// データを復号化
        /// </summary>
        public byte[] Decrypt(byte[] ciphertext)
        {
            if (ciphertext == null || ciphertext.Length < NonceSize + TagSize)
            {
                _logger.Warning("Invalid ciphertext length");
                throw new ArgumentException("Invalid ciphertext format");
            }

            try
            {
                // フォーマット解析: [Nonce (12)][Ciphertext][Tag (16)]
                var nonce = new byte[NonceSize];
                var tag = new byte[TagSize];
                var encryptedData = new byte[ciphertext.Length - NonceSize - TagSize];

                Buffer.BlockCopy(ciphertext, 0, nonce, 0, NonceSize);
                Buffer.BlockCopy(ciphertext, NonceSize, encryptedData, 0, encryptedData.Length);
                Buffer.BlockCopy(ciphertext, NonceSize + encryptedData.Length, tag, 0, TagSize);

                var plaintext = new byte[encryptedData.Length];

                using var aesGcm = new AesGcm(_key);
                aesGcm.Decrypt(nonce, encryptedData, tag, plaintext);

                _logger.Debug($"Decrypted {ciphertext.Length} bytes -> {plaintext.Length} bytes");
                return plaintext;
            }
            catch (CryptographicException ex)
            {
                _logger.Error($"Decryption failed (authentication tag mismatch): {ex.Message}", ex);
                throw new CryptographicException("Decryption failed: data may be tampered", ex);
            }
            catch (Exception ex)
            {
                _logger.Error($"Decryption failed: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// 96-bit Nonceを生成
        /// </summary>
        public byte[] GenerateNonce()
        {
            var nonce = new byte[NonceSize];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(nonce);
            return nonce;
        }

        /// <summary>
        /// 暗号化鍵をローテーション
        /// </summary>
        public void RotateKey()
        {
            _logger.Info("Rotating encryption key...");
            _key = GenerateKey();
            _logger.Info("Encryption key rotated successfully");
        }
    }
}
