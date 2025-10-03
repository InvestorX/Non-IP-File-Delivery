using System;

namespace NonIPFileDelivery.Models
{
    /// <summary>
    /// ClamAVスキャン結果
    /// </summary>
    public class ClamAVScanResult
    {
        /// <summary>
        /// クリーンな（脅威なし）データか
        /// </summary>
        public bool IsClean { get; set; }

        /// <summary>
        /// 検出されたウイルス名
        /// </summary>
        public string? VirusName { get; set; }

        /// <summary>
        /// 詳細情報（ClamAVの生応答）
        /// </summary>
        public string? Details { get; set; }

        /// <summary>
        /// エラーメッセージ（スキャン失敗時）
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// スキャン実行時刻
        /// </summary>
        public DateTime ScanTime { get; set; } = DateTime.UtcNow;
    }
}
