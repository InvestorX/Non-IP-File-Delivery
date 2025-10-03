using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// PostgreSQLプロトコル解析器
    /// PostgreSQLワイヤプロトコル（メッセージフォーマット）解析
    /// Phase 2実装
    /// </summary>
    public class PostgreSQLAnalyzer : IPostgreSQLAnalyzer
    {
        private readonly ILoggingService _logger;
        private readonly SQLInjectionDetector _sqlDetector;

        public PostgreSQLAnalyzer(ILoggingService logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _sqlDetector = new SQLInjectionDetector(logger);
        }

        /// <summary>
        /// PostgreSQLパケット解析
        /// </summary>
        public async Task<ProtocolAnalysisResult> AnalyzeAsync(byte[] packetData)
        {
            return await AnalyzePostgreSQLAsync(packetData);
        }

        /// <summary>
        /// PostgreSQL解析（詳細版）
        /// </summary>
        public async Task<PostgreSQLAnalysisResult> AnalyzePostgreSQLAsync(byte[] data)
        {
            var result = new PostgreSQLAnalysisResult
            {
                IsValid = false,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                // PostgreSQLペイロードを抽出
                var pgPayload = ExtractPostgreSQLPayload(data);
                if (pgPayload == null || pgPayload.Length < 5)
                {
                    result.ErrorMessage = "Invalid PostgreSQL payload";
                    return result;
                }

                // メッセージタイプ（1バイト目）
                char messageType = (char)pgPayload[0];
                result.MessageType = messageType;

                // メッセージ長（2-5バイト目、big-endian）
                int messageLength = (pgPayload[1] << 24) | (pgPayload[2] << 16) | 
                                   (pgPayload[3] << 8) | pgPayload[4];

                _logger.Debug($"PostgreSQL message: Type={messageType}, Length={messageLength}");

                // メッセージタイプ別処理
                switch (messageType)
                {
                    case 'Q':  // Simple Query
                        await ParseSimpleQuery(pgPayload, result);
                        break;

                    case 'P':  // Parse (Prepared Statement)
                        ParsePreparedStatement(pgPayload, result);
                        break;

                    case 'B':  // Bind
                        ParseBind(pgPayload, result);
                        break;

                    case 'E':  // Execute
                        ParseExecute(pgPayload, result);
                        break;

                    case 'X':  // Terminate
                        result.Command = "TERMINATE";
                        _logger.Debug("PostgreSQL connection termination");
                        break;

                    default:
                        result.Command = $"UNKNOWN_TYPE_{messageType}";
                        _logger.Debug($"Unknown PostgreSQL message type: {messageType}");
                        break;
                }

                result.IsValid = true;
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"PostgreSQL analysis error: {ex.Message}", ex);
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// ポート判定
        /// </summary>
        public bool CanAnalyze(int port)
        {
            return port == 5432;  // PostgreSQLデフォルトポート
        }

        /// <summary>
        /// PostgreSQLペイロードを抽出
        /// </summary>
        private byte[]? ExtractPostgreSQLPayload(byte[] data)
        {
            // Ethernet(14) + IP(20) + TCP(20) = 54バイト最小ヘッダー
            const int minHeaderSize = 54;
            if (data.Length <= minHeaderSize)
                return null;

            return data.Skip(minHeaderSize).ToArray();
        }

        /// <summary>
        /// Simple Query解析（'Q'メッセージ）
        /// </summary>
        private async Task ParseSimpleQuery(byte[] payload, PostgreSQLAnalysisResult result)
        {
            // 'Q' + length(4) + SQL文（NULL終端）
            if (payload.Length < 6)
            {
                result.ErrorMessage = "Query too short";
                return;
            }

            // SQL文を抽出（5バイト目から、NULL終端まで）
            var sqlBytes = payload.Skip(5).TakeWhile(b => b != 0).ToArray();
            var sql = Encoding.UTF8.GetString(sqlBytes);

            result.SqlQuery = sql;
            result.Command = "QUERY";
            result.QueryType = DetermineQueryType(sql);

            _logger.Info($"PostgreSQL Query: {sql}");

            // SQLインジェクション検出
            var injectionResult = _sqlDetector.Detect(sql);
            if (injectionResult.IsInjection)
            {
                result.IsPotentialInjection = true;
                result.ThreatLevel = injectionResult.ThreatLevel;

                _logger.Warning($"SQL Injection detected: Pattern={injectionResult.DetectedPattern}, " +
                               $"ThreatLevel={injectionResult.ThreatLevel}");
            }
        }

        /// <summary>
        /// Prepared Statement解析（'P'メッセージ）
        /// </summary>
        private void ParsePreparedStatement(byte[] payload, PostgreSQLAnalysisResult result)
        {
            // 'P' + length(4) + statement_name(NULL終端) + query(NULL終端) + param_count(2)
            if (payload.Length < 7)
            {
                result.ErrorMessage = "Prepared statement too short";
                return;
            }

            var offset = 5;

            // Statement名抽出
            var stmtNameBytes = payload.Skip(offset).TakeWhile(b => b != 0).ToArray();
            result.StatementName = Encoding.UTF8.GetString(stmtNameBytes);
            offset += stmtNameBytes.Length + 1;

            // SQL文抽出
            if (offset < payload.Length)
            {
                var sqlBytes = payload.Skip(offset).TakeWhile(b => b != 0).ToArray();
                result.SqlQuery = Encoding.UTF8.GetString(sqlBytes);
                result.Command = "PARSE";
                result.QueryType = DetermineQueryType(result.SqlQuery);

                _logger.Debug($"PostgreSQL Prepared: {result.StatementName} -> {result.SqlQuery}");
            }
        }

        /// <summary>
        /// Bindメッセージ解析（'B'メッセージ）
        /// </summary>
        private void ParseBind(byte[] payload, PostgreSQLAnalysisResult result)
        {
            result.Command = "BIND";
            _logger.Debug("PostgreSQL Bind message");
            // Bind詳細解析は省略（パラメータ抽出は複雑）
        }

        /// <summary>
        /// Executeメッセージ解析（'E'メッセージ）
        /// </summary>
        private void ParseExecute(byte[] payload, PostgreSQLAnalysisResult result)
        {
            result.Command = "EXECUTE";
            _logger.Debug("PostgreSQL Execute message");
        }

        /// <summary>
        /// クエリタイプを判定（SELECT, INSERT, UPDATE, DELETE等）
        /// </summary>
        private string DetermineQueryType(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return "UNKNOWN";

            var upperSql = sql.TrimStart().ToUpper();

            if (upperSql.StartsWith("SELECT")) return "SELECT";
            if (upperSql.StartsWith("INSERT")) return "INSERT";
            if (upperSql.StartsWith("UPDATE")) return "UPDATE";
            if (upperSql.StartsWith("DELETE")) return "DELETE";
            if (upperSql.StartsWith("CREATE")) return "CREATE";
            if (upperSql.StartsWith("DROP")) return "DROP";
            if (upperSql.StartsWith("ALTER")) return "ALTER";
            if (upperSql.StartsWith("TRUNCATE")) return "TRUNCATE";
            if (upperSql.StartsWith("GRANT")) return "GRANT";
            if (upperSql.StartsWith("REVOKE")) return "REVOKE";

            return "OTHER";
        }
    }
}