using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// SQLインジェクション検出器
    /// 15種類の正規表現パターンによる検出
    /// Phase 2実装
    /// </summary>
    public class SQLInjectionDetector
    {
        private readonly ILoggingService _logger;

        /// <summary>
        /// SQLインジェクションパターン定義
        /// </summary>
        private static readonly List<SQLInjectionPattern> Patterns = new()
        {
            // Critical: 破壊的なコマンド
            new SQLInjectionPattern
            {
                Name = "DropTable",
                Pattern = @";\s*DROP\s+TABLE",
                ThreatLevel = ThreatLevel.Critical,
                Description = "DROP TABLE command - destroys data"
            },
            new SQLInjectionPattern
            {
                Name = "TruncateTable",
                Pattern = @";\s*TRUNCATE\s+TABLE",
                ThreatLevel = ThreatLevel.Critical,
                Description = "TRUNCATE TABLE command - destroys data"
            },

            // High: データ抽出・改ざん
            new SQLInjectionPattern
            {
                Name = "UnionSelect",
                Pattern = @"UNION\s+SELECT",
                ThreatLevel = ThreatLevel.High,
                Description = "UNION SELECT - data exfiltration"
            },
            new SQLInjectionPattern
            {
                Name = "ClassicOrInjection",
                Pattern = @"'\s*OR\s*'1'\s*=\s*'1",
                ThreatLevel = ThreatLevel.High,
                Description = "Classic OR '1'='1' injection"
            },
            new SQLInjectionPattern
            {
                Name = "OrInjectionNumeric",
                Pattern = @"OR\s+1\s*=\s*1",
                ThreatLevel = ThreatLevel.High,
                Description = "Numeric OR 1=1 injection"
            },

            // Medium: コメント挿入、制御文字
            new SQLInjectionPattern
            {
                Name = "CommentInjection",
                Pattern = @"--",
                ThreatLevel = ThreatLevel.Medium,
                Description = "SQL comment injection"
            },
            new SQLInjectionPattern
            {
                Name = "MultiLineComment",
                Pattern = @"/\*.*?\*/",
                ThreatLevel = ThreatLevel.Medium,
                Description = "Multi-line comment injection"
            },

            // High: システムコマンド実行
            new SQLInjectionPattern
            {
                Name = "XpCmdShell",
                Pattern = @"xp_cmdshell",
                ThreatLevel = ThreatLevel.Critical,
                Description = "SQL Server xp_cmdshell execution"
            },
            new SQLInjectionPattern
            {
                Name = "ExecCommand",
                Pattern = @"EXEC\s*\(",
                ThreatLevel = ThreatLevel.High,
                Description = "Dynamic SQL execution (EXEC)"
            },

            // Medium: 情報漏洩
            new SQLInjectionPattern
            {
                Name = "InformationSchema",
                Pattern = @"information_schema",
                ThreatLevel = ThreatLevel.Medium,
                Description = "Accessing information_schema (metadata leak)"
            },
            new SQLInjectionPattern
            {
                Name = "SysTables",
                Pattern = @"sys\.tables|sys\.columns",
                ThreatLevel = ThreatLevel.Medium,
                Description = "Accessing sys.tables (metadata leak)"
            },

            // High: ストアドプロシージャ悪用
            new SQLInjectionPattern
            {
                Name = "StoredProcedure",
                Pattern = @"EXECUTE\s+sp_",
                ThreatLevel = ThreatLevel.High,
                Description = "Executing system stored procedures"
            },

            // Low: 疑わしいが確定的ではない
            new SQLInjectionPattern
            {
                Name = "SemicolonInjection",
                Pattern = @";\s*[A-Z]+\s+",
                ThreatLevel = ThreatLevel.Low,
                Description = "Semicolon with SQL command"
            },
            new SQLInjectionPattern
            {
                Name = "HexEncoding",
                Pattern = @"0x[0-9a-fA-F]+",
                ThreatLevel = ThreatLevel.Low,
                Description = "Hexadecimal encoding (evasion technique)"
            },

            // Critical: 権限昇格
            new SQLInjectionPattern
            {
                Name = "GrantRevoke",
                Pattern = @"GRANT|REVOKE",
                ThreatLevel = ThreatLevel.Critical,
                Description = "Permission manipulation (GRANT/REVOKE)"
            }
        };

        public SQLInjectionDetector(ILoggingService logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// SQLインジェクション検出
        /// </summary>
        /// <param name="sql">検査対象SQL文</param>
        /// <returns>検出結果</returns>
        public SQLInjectionResult Detect(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                return new SQLInjectionResult
                {
                    IsInjection = false,
                    ThreatLevel = ThreatLevel.None
                };
            }

            try
            {
                // 全パターンをチェック
                foreach (var pattern in Patterns)
                {
                    var match = Regex.Match(sql, pattern.Pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
                    if (match.Success)
                    {
                        _logger.Warning($"SQL Injection pattern matched: {pattern.Name} - {pattern.Description}");

                        return new SQLInjectionResult
                        {
                            IsInjection = true,
                            ThreatLevel = pattern.ThreatLevel,
                            DetectedPattern = pattern.Name,
                            PatternDescription = pattern.Description,
                            MatchIndex = match.Index,
                            MatchedString = match.Value
                        };
                    }
                }

                // 脅威なし
                return new SQLInjectionResult
                {
                    IsInjection = false,
                    ThreatLevel = ThreatLevel.None
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"SQL Injection detection error: {ex.Message}", ex);
                return new SQLInjectionResult
                {
                    IsInjection = false,
                    ThreatLevel = ThreatLevel.Unknown,
                    DetectedPattern = "ERROR",
                    PatternDescription = ex.Message
                };
            }
        }

        /// <summary>
        /// 複数パターンマッチ（全パターンをチェック）
        /// </summary>
        public List<SQLInjectionResult> DetectAll(string sql)
        {
            var results = new List<SQLInjectionResult>();

            if (string.IsNullOrWhiteSpace(sql))
                return results;

            foreach (var pattern in Patterns)
            {
                var match = Regex.Match(sql, pattern.Pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (match.Success)
                {
                    results.Add(new SQLInjectionResult
                    {
                        IsInjection = true,
                        ThreatLevel = pattern.ThreatLevel,
                        DetectedPattern = pattern.Name,
                        PatternDescription = pattern.Description,
                        MatchIndex = match.Index,
                        MatchedString = match.Value
                    });
                }
            }

            return results;
        }

        /// <summary>
        /// 統計情報取得
        /// </summary>
        public Dictionary<string, object> GetStatistics()
        {
            return new Dictionary<string, object>
            {
                { "TotalPatterns", Patterns.Count },
                { "CriticalPatterns", Patterns.Count(p => p.ThreatLevel == ThreatLevel.Critical) },
                { "HighPatterns", Patterns.Count(p => p.ThreatLevel == ThreatLevel.High) },
                { "MediumPatterns", Patterns.Count(p => p.ThreatLevel == ThreatLevel.Medium) },
                { "LowPatterns", Patterns.Count(p => p.ThreatLevel == ThreatLevel.Low) }
            };
        }
    }

    /// <summary>
    /// SQLインジェクションパターン定義
    /// </summary>
    internal class SQLInjectionPattern
    {
        public string Name { get; set; } = string.Empty;
        public string Pattern { get; set; } = string.Empty;
        public ThreatLevel ThreatLevel { get; set; }
        public string Description { get; set; } = string.Empty;
    }
}