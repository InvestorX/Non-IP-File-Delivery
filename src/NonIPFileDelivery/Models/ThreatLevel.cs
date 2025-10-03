namespace NonIPFileDelivery.Models
{
    /// <summary>
    /// 脅威レベル
    /// Phase 2実装: セキュリティ検閲強化
    /// </summary>
    public enum ThreatLevel
    {
        /// <summary>
        /// 脅威なし
        /// </summary>
        None = 0,

        /// <summary>
        /// 低レベルの脅威
        /// - 疑わしいが確定的ではない
        /// - 監視のみ、通信は許可
        /// </summary>
        Low = 1,

        /// <summary>
        /// 中レベルの脅威
        /// - 明確な脅威の兆候あり
        /// - 警告ログ記録、通信は許可（監視強化）
        /// </summary>
        Medium = 2,

        /// <summary>
        /// 高レベルの脅威
        /// - 確実な攻撃の試み
        /// - 通信をブロック、管理者に通知
        /// </summary>
        High = 3,

        /// <summary>
        /// 致命的な脅威
        /// - 即座にブロックすべき攻撃
        /// - システム全体に影響の可能性
        /// </summary>
        Critical = 4,

        /// <summary>
        /// 不明（解析エラー時）
        /// </summary>
        Unknown = 99
    }
}