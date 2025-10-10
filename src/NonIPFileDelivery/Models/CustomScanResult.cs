// CustomScanResult.cs
// カスタム署名スキャナーのスキャン結果モデル

using System;

namespace NonIPFileDelivery.Models
{
    /// <summary>
    /// カスタム署名スキャンの結果
    /// </summary>
    public class CustomScanResult
    {
        /// <summary>
        /// スキャン結果がクリーンかどうか
        /// </summary>
        public bool IsClean { get; set; } = true;

        /// <summary>
        /// 検出された脅威名（署名名）
        /// </summary>
        public string? ThreatName { get; set; }

        /// <summary>
        /// 脅威レベル
        /// </summary>
        public ThreatLevel Severity { get; set; } = ThreatLevel.Low;

        /// <summary>
        /// マッチしたパターンのオフセット位置（バイト）
        /// </summary>
        public int MatchOffset { get; set; } = -1;

        /// <summary>
        /// マッチした署名ID
        /// </summary>
        public string? SignatureId { get; set; }

        /// <summary>
        /// 詳細情報
        /// </summary>
        public string? Details { get; set; }

        /// <summary>
        /// スキャン所要時間
        /// </summary>
        public TimeSpan ScanDuration { get; set; }

        /// <summary>
        /// エラーメッセージ（スキャンエラー時）
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// マルウェア署名定義
    /// </summary>
    public class MalwareSignature
    {
        /// <summary>
        /// 署名ID（例: "SIG-0001"）
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// 脅威名（例: "Trojan.Generic.12345"）
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 16進数パターン（例: "4D5A90000300000004000000"）
        /// </summary>
        public string HexPattern { get; set; } = string.Empty;

        /// <summary>
        /// バイナリパターン（内部使用）
        /// </summary>
        public byte[]? Pattern { get; set; }

        /// <summary>
        /// 脅威レベル
        /// </summary>
        public ThreatLevel Severity { get; set; } = ThreatLevel.Medium;

        /// <summary>
        /// 検索開始オフセット（-1=任意位置、0=ファイル先頭、など）
        /// </summary>
        public int Offset { get; set; } = -1;

        /// <summary>
        /// 検索範囲の最大サイズ（0=制限なし）
        /// </summary>
        public int MaxSearchSize { get; set; } = 0;

        /// <summary>
        /// 署名の説明
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// 署名が有効かどうか
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 作成日時
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 更新日時
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
