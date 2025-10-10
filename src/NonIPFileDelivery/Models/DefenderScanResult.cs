// DefenderScanResult.cs
// Windows Defender スキャン結果モデル

using System;

namespace NonIPFileDelivery.Models
{
    /// <summary>
    /// Windows Defender スキャンの結果
    /// </summary>
    public class DefenderScanResult
    {
        /// <summary>
        /// スキャン結果がクリーンかどうか
        /// </summary>
        public bool IsClean { get; set; } = true;

        /// <summary>
        /// 検出された脅威名
        /// </summary>
        public string? ThreatName { get; set; }

        /// <summary>
        /// 脅威の種類（例: Virus, Trojan, Malware）
        /// </summary>
        public string? ThreatType { get; set; }

        /// <summary>
        /// スキャンされたファイルパス
        /// </summary>
        public string? ScanPath { get; set; }

        /// <summary>
        /// 脅威レベル
        /// </summary>
        public ThreatLevel Severity { get; set; } = ThreatLevel.Low;

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

        /// <summary>
        /// Windows Defenderが利用可能かどうか
        /// </summary>
        public bool DefenderAvailable { get; set; } = false;

        /// <summary>
        /// スキャンに使用したメソッド（MpCmdRun, PowerShell, COM等）
        /// </summary>
        public string? ScanMethod { get; set; }
    }
}
