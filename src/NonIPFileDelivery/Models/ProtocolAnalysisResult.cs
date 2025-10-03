using System;
using System.Collections.Generic;

namespace NonIPFileDelivery.Models
{
    /// <summary>
    /// プロトコル解析結果の基底クラス
    /// Phase 2実装: プロトコル解析基盤
    /// </summary>
    public class ProtocolAnalysisResult
    {
        /// <summary>
        /// プロトコル種別
        /// </summary>
        public ProtocolType Protocol { get; set; }

        /// <summary>
        /// 解析が有効か（パース成功）
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// 解析タイムスタンプ
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// コマンド/メッセージタイプ（プロトコル固有）
        /// </summary>
        public string? Command { get; set; }

        /// <summary>
        /// パラメータ（プロトコル固有）
        /// </summary>
        public Dictionary<string, string> Parameters { get; set; } = new();

        /// <summary>
        /// エラーメッセージ（解析失敗時）
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 生データのサイズ（バイト）
        /// </summary>
        public int DataSize { get; set; }

        /// <summary>
        /// 抽出されたデータ（SQL文、FTPコマンド等）
        /// </summary>
        public string? ExtractedData { get; set; }
    }

    /// <summary>
    /// FTP解析結果
    /// </summary>
    public class FTPAnalysisResult : ProtocolAnalysisResult
    {
        /// <summary>
        /// FTPコマンド（USER, PASS, RETR等）
        /// </summary>
        public string? FtpCommand { get; set; }

        /// <summary>
        /// コマンド引数
        /// </summary>
        public string? Arguments { get; set; }

        /// <summary>
        /// ファイル名（RETR/STOR時）
        /// </summary>
        public string? FileName { get; set; }

        /// <summary>
        /// レスポンスコード（応答時）
        /// </summary>
        public int? ResponseCode { get; set; }

        /// <summary>
        /// レスポンスメッセージ
        /// </summary>
        public string? ResponseMessage { get; set; }

        /// <summary>
        /// コマンドが許可されているか
        /// </summary>
        public bool IsCommandAllowed { get; set; } = true;

        public FTPAnalysisResult()
        {
            Protocol = ProtocolType.FTP;
        }
    }

    /// <summary>
    /// PostgreSQL解析結果
    /// </summary>
    public class PostgreSQLAnalysisResult : ProtocolAnalysisResult
    {
        /// <summary>
        /// メッセージタイプ（Q, P, B, E等）
        /// </summary>
        public char? MessageType { get; set; }

        /// <summary>
        /// SQL文（Query/Parse時）
        /// </summary>
        public string? SqlQuery { get; set; }

        /// <summary>
        /// Prepared Statementの名前
        /// </summary>
        public string? StatementName { get; set; }

        /// <summary>
        /// バインドパラメータ
        /// </summary>
        public List<string> BindParameters { get; set; } = new();

        /// <summary>
        /// クエリタイプ（SELECT, INSERT, UPDATE, DELETE等）
        /// </summary>
        public string? QueryType { get; set; }

        /// <summary>
        /// SQLインジェクションの可能性
        /// </summary>
        public bool IsPotentialInjection { get; set; }

        /// <summary>
        /// 脅威レベル
        /// </summary>
        public ThreatLevel ThreatLevel { get; set; } = ThreatLevel.None;

        public PostgreSQLAnalysisResult()
        {
            Protocol = ProtocolType.PostgreSQL;
        }
    }

    /// <summary>
    /// SQLインジェクション検出結果
    /// </summary>
    public class SQLInjectionResult
    {
        /// <summary>
        /// インジェクション検出有無
        /// </summary>
        public bool IsInjection { get; set; }

        /// <summary>
        /// 脅威レベル
        /// </summary>
        public ThreatLevel ThreatLevel { get; set; } = ThreatLevel.None;

        /// <summary>
        /// 検出されたパターン
        /// </summary>
        public string? DetectedPattern { get; set; }

        /// <summary>
        /// パターン説明
        /// </summary>
        public string? PatternDescription { get; set; }

        /// <summary>
        /// マッチ位置（文字列内のインデックス）
        /// </summary>
        public int MatchIndex { get; set; }

        /// <summary>
        /// マッチした文字列
        /// </summary>
        public string? MatchedString { get; set; }

        /// <summary>
        /// 脅威であるか（ThreatLevel > Low）
        /// </summary>
        public bool IsThreat => ThreatLevel >= ThreatLevel.Medium;

        /// <summary>
        /// マッチしたパターン（DetectedPatternのエイリアス）
        /// </summary>
        public string? MatchedPattern => DetectedPattern;

        /// <summary>
        /// 説明（PatternDescriptionのエイリアス）
        /// </summary>
        public string? Description => PatternDescription;
    }
}