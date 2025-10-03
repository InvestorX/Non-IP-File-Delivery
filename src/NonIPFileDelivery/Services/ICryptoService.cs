using System;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// 暗号化サービスのインターフェース
    /// AES-256-GCMによる暗号化/復号化機能を提供
    /// </summary>
    public interface ICryptoService
    {
        /// <summary>
        /// データを暗号化します
        /// </summary>
        /// <param name="plaintext">平文データ</param>
        /// <returns>暗号化されたデータ（Nonce + Ciphertext + Tag）</returns>
        byte[] Encrypt(byte[] plaintext);

        /// <summary>
        /// データを復号化します
        /// </summary>
        /// <param name="ciphertext">暗号化されたデータ（Nonce + Ciphertext + Tag）</param>
        /// <returns>復号化された平文データ</returns>
        /// <exception cref="System.Security.Cryptography.CryptographicException">認証タグの検証に失敗した場合</exception>
        byte[] Decrypt(byte[] ciphertext);

        /// <summary>
        /// 新しいNonce（Number used once）を生成します
        /// </summary>
        /// <returns>96-bit（12バイト）のランダムなNonce</returns>
        byte[] GenerateNonce();

        /// <summary>
        /// 暗号化鍵をローテーションします
        /// </summary>
        void RotateKey();
    }
}
