namespace NonIPFileDelivery.Models
{
    /// <summary>
    /// プロトコル種別
    /// Phase 2実装: プロトコル解析基盤
    /// </summary>
    public enum ProtocolType
    {
        /// <summary>
        /// 不明なプロトコル
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// FTP (File Transfer Protocol) - RFC 959
        /// ポート: 21 (制御), 20 (データ)
        /// </summary>
        FTP = 1,

        /// <summary>
        /// SFTP (SSH File Transfer Protocol)
        /// ポート: 22
        /// </summary>
        SFTP = 2,

        /// <summary>
        /// PostgreSQL
        /// デフォルトポート: 5432
        /// </summary>
        PostgreSQL = 3,

        /// <summary>
        /// HTTP (HyperText Transfer Protocol)
        /// ポート: 80
        /// </summary>
        HTTP = 4,

        /// <summary>
        /// HTTPS (HTTP Secure)
        /// ポート: 443
        /// </summary>
        HTTPS = 5,

        /// <summary>
        /// SMTP (Simple Mail Transfer Protocol)
        /// ポート: 25, 587, 465
        /// </summary>
        SMTP = 6,

        /// <summary>
        /// MySQL
        /// デフォルトポート: 3306
        /// </summary>
        MySQL = 7,

        /// <summary>
        /// その他のTCPプロトコル
        /// </summary>
        TCP = 100,

        /// <summary>
        /// その他のUDPプロトコル
        /// </summary>
        UDP = 101
    }
}