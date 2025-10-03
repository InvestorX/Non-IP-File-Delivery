using System;
using System.Security.Cryptography;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// AES-256-GCM暗号化サービス実装
    /// </summary>
    public class CryptoService : ICryptoService, IDisposable
    {
        private readonly ILoggingService _logger;
        private byte[] _key; // 256-bit (32 bytes) encryption key
        private const int NonceSize = 12; // 96-bit nonce
        private const int TagSize = 16;   // 128-bit authentication tag
        private bool _disposed;

        public CryptoService(ILoggingService logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _key = GenerateKey();
            _logger.Info("CryptoService initialized with AES-256-GCM");
        }

        /// <summary>
        /// 256-bit暗号化鍵を生成
        /// </summary>
        private byte[] GenerateKey()
        {
            var key = new byte[32]; // 256-bit = 32 bytes
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(key);
            return key;
        }

        /// <summary>
        /// データを暗号化（AES-256-GCM）
        /// </summary>
        public byte[] Encrypt(byte[] plaintext)
        {
            if (plaintext == null || plaintext.Length == 0)
                throw new ArgumentException("Plaintext cannot be null or empty", nameof(plaintext));

            try
            {
                // Nonce生成
                var nonce = GenerateNonce();

                // 暗号化バッファ準備
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[TagSize];

                // AES-GCM暗号化実行
                using var aesGcm = new AesGcm(_key);
                aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

                // Nonce + Ciphertext + Tag を結合
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
                throw;
            }
        }

        /// <summary>
        /// データを復号化（AES-256-GCM）
        /// </summary>
        public byte[] Decrypt(byte[] ciphertext)
        {
            if (ciphertext == null || ciphertext.Length < NonceSize + TagSize)
                throw new ArgumentException("Ciphertext is invalid or too short", nameof(ciphertext));

            try
            {
                // Nonce、Ciphertext、Tagを分離
                var nonce = new byte[NonceSize];
                var encryptedData = new byte[ciphertext.Length - NonceSize - TagSize];
                var tag = new byte[TagSize];

                Buffer.BlockCopy(ciphertext, 0, nonce, 0, NonceSize);
                Buffer.BlockCopy(ciphertext, NonceSize, encryptedData, 0, encryptedData.Length);
                Buffer.BlockCopy(ciphertext, NonceSize + encryptedData.Length, tag, 0, TagSize);

                // 復号化バッファ準備
                var plaintext = new byte[encryptedData.Length];

                // AES-GCM復号化実行（認証タグ検証含む）
                using var aesGcm = new AesGcm(_key);
                aesGcm.Decrypt(nonce, encryptedData, tag, plaintext);

                _logger.Debug($"Decrypted {ciphertext.Length} bytes -> {plaintext.Length} bytes");
                return plaintext;
            }
            catch (CryptographicException ex)
            {
                _logger.Error($"Decryption failed (authentication tag verification failed): {ex.Message}", ex);
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Decryption error: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// 96-bit Nonce生成
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

        public void Dispose()
        {
            if (_disposed) return;

            // 鍵をゼロクリア（セキュリティ対策）
            if (_key != null)
            {
                Array.Clear(_key, 0, _key.Length);
            }

            _disposed = true;
            _logger.Info("CryptoService disposed");
        }
    }
}
