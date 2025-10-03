using System;

namespace NonIPFileDelivery.Models
{
    /// <summary>
    /// YARAスキャン結果
    /// </summary>
    public class YARAScanResult
    {
        /// <summary>
        /// YARAルールにマッチしたか
        /// </summary>
        public bool IsMatch { get; set; }

        /// <summary>
        /// マッチしたYARAルール名
        /// </summary>
        public string? RuleName { get; set; }

        /// <summary>
        /// マッチした文字列数
        /// </summary>
        public int MatchedStrings { get; set; }

        /// <summary>
        /// 詳細情報
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
