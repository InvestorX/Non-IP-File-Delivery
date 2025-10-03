using System;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// 暗号化サービスのインターフェース
    /// AES-256-GCM暗号化/復号化機能を提供
    /// </summary>
    public interface ICryptoService
    {
        /// <summary>
        /// データを暗号化
        /// </summary>
        /// <param name="plaintext">平文データ</param>
        /// <returns>暗号化データ（Nonce + 暗号文 + Tag）</returns>
        byte[] Encrypt(byte[] plaintext);

        /// <summary>
        /// データを復号化
        /// </summary>
        /// <param name="ciphertext">暗号化データ（Nonce + 暗号文 + Tag）</param>
        /// <returns>復号化された平文データ</returns>
        byte[] Decrypt(byte[] ciphertext);

        /// <summary>
        /// 新しいNonce（Number used once）を生成
        /// </summary>
        /// <returns>96-bit Nonce</returns>
        byte[] GenerateNonce();

        /// <summary>
        /// 暗号化鍵をローテーション（将来の拡張用）
        /// </summary>
        void RotateKey();
    }
}
