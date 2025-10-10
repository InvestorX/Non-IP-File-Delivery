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

    /// <summary>
    /// スキャン実行時間
    /// </summary>
    public TimeSpan ScanDuration { get; set; }

    /// <summary>
    /// スキャンされたファイルサイズ（バイト）
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// 使用されたスキャンメソッド（INSTREAM, MULTISCAN, CONTSCAN, SCAN）
    /// </summary>
    public string ScanMethod { get; set; } = "INSTREAM";

    /// <summary>
    /// スキャンされたファイルパス（MULTISCAN/CONTSCAN時）
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// 並列スキャンで使用されたスレッド数（MULTISCAN時）
    /// </summary>
    public int ThreadCount { get; set; }

    /// <summary>
    /// スキャンされたファイル総数（MULTISCAN/CONTSCAN時）
    /// </summary>
    public int TotalFilesScanned { get; set; }

    /// <summary>
    /// 検出された脅威の総数（MULTISCAN/CONTSCAN時）
    /// </summary>
    public int TotalThreatsFound { get; set; }
}
}