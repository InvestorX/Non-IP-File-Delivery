using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// FTPプロトコル解析器
    /// RFC 959準拠、40種類以上のFTPコマンド対応
    /// Phase 2実装
    /// </summary>
    public class FTPAnalyzer : IFTPAnalyzer
    {
        private readonly ILoggingService _logger;

        // RFC 959準拠のFTPコマンド一覧
        private static readonly HashSet<string> ValidFTPCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            // アクセス制御コマンド
            "USER", "PASS", "ACCT", "CWD", "CDUP", "SMNT",
            "QUIT", "REIN", "PORT", "PASV", "TYPE", "STRU",
            "MODE",

            // ファイル転送コマンド
            "RETR", "STOR", "STOU", "APPE", "ALLO", "REST",
            "RNFR", "RNTO", "ABOR", "DELE", "RMD", "MKD",
            "PWD", "LIST", "NLST", "SITE", "SYST", "STAT",
            "HELP", "NOOP",

            // 拡張コマンド（RFC 2389, RFC 2428等）
            "FEAT", "OPTS", "AUTH", "PBSZ", "PROT", "EPRT",
            "EPSV", "MLSD", "MLST", "SIZE", "MDTM",

            // その他の一般的な拡張
            "CLNT", "MFMT", "MFCT", "MFF", "AVBL", "CSID",
            "XCWD", "XCUP", "XMKD", "XRMD", "XPWD"
        };

        // 危険なコマンド（特別な監視が必要）
        private static readonly HashSet<string> DangerousCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "DELE", "RMD", "RNTO", "SITE", "STOR", "APPE"
        };

        public FTPAnalyzer(ILoggingService logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// FTPパケットを解析
        /// </summary>
        public async Task<ProtocolAnalysisResult> AnalyzeAsync(byte[] packetData)
        {
            return await AnalyzeFTPAsync(packetData);
        }

        /// <summary>
        /// FTP解析（詳細版）
        /// </summary>
        public async Task<FTPAnalysisResult> AnalyzeFTPAsync(byte[] data)
        {
            var result = new FTPAnalysisResult
            {
                IsValid = false,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                // TCP/IPヘッダーをスキップしてFTPペイロードを抽出
                var ftpPayload = ExtractFTPPayload(data);
                if (ftpPayload == null || ftpPayload.Length == 0)
                {
                    result.ErrorMessage = "No FTP payload found";
                    return result;
                }

                // ASCII文字列として解釈
                var ftpText = Encoding.ASCII.GetString(ftpPayload).TrimEnd('\r', '\n');
                _logger.Debug($"FTP payload: {ftpText}");

                // コマンド or レスポンスを判定
                if (IsResponse(ftpText))
                {
                    ParseResponse(ftpText, result);
                }
                else
                {
                    ParseCommand(ftpText, result);
                }

                result.IsValid = true;
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"FTP analysis error: {ex.Message}", ex);
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// ポート判定
        /// </summary>
        public bool CanAnalyze(int port)
        {
            return port == 21 || port == 20;  // FTP制御/データポート
        }

        /// <summary>
        /// FTPペイロードを抽出（TCP/IPヘッダーをスキップ）
        /// </summary>
        private byte[]? ExtractFTPPayload(byte[] data)
        {
            // Ethernet(14) + IP(20) + TCP(20) = 54バイト最小ヘッダー
            const int minHeaderSize = 54;
            if (data.Length <= minHeaderSize)
                return null;

            // TCP/IPヘッダーサイズは可変なので、簡易的に54バイト以降をペイロードとする
            // 本格実装ではPacketDotNetライブラリでパース
            return data.Skip(minHeaderSize).ToArray();
        }

        /// <summary>
        /// FTPレスポンスか判定（3桁数字で始まる）
        /// </summary>
        private bool IsResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 3)
                return false;

            // "220 Welcome" のような3桁数字で始まる
            return char.IsDigit(text[0]) && char.IsDigit(text[1]) && char.IsDigit(text[2]);
        }

        /// <summary>
        /// FTPレスポンスを解析
        /// </summary>
        private void ParseResponse(string text, FTPAnalysisResult result)
        {
            // "220 Service ready"
            var match = Regex.Match(text, @"^(\d{3})\s*(.*)$");
            if (match.Success)
            {
                result.ResponseCode = int.Parse(match.Groups[1].Value);
                result.ResponseMessage = match.Groups[2].Value;
                result.Command = "RESPONSE";

                _logger.Debug($"FTP Response: Code={result.ResponseCode}, Message={result.ResponseMessage}");
            }
        }

        /// <summary>
        /// FTPコマンドを解析
        /// </summary>
        private void ParseCommand(string text, FTPAnalysisResult result)
        {
            // "USER anonymous" or "RETR file.txt"
            var parts = text.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                result.ErrorMessage = "Empty command";
                return;
            }

            var command = parts[0].ToUpper();
            var arguments = parts.Length > 1 ? parts[1] : string.Empty;

            result.FtpCommand = command;
            result.Arguments = arguments;
            result.Command = command;

            // コマンド検証
            if (!ValidFTPCommands.Contains(command))
            {
                result.IsCommandAllowed = false;
                result.ErrorMessage = $"Unknown FTP command: {command}";
                _logger.Warning($"Unknown FTP command detected: {command}");
            }
            else
            {
                result.IsCommandAllowed = true;

                // 危険なコマンドの場合は警告
                if (DangerousCommands.Contains(command))
                {
                    _logger.Warning($"Dangerous FTP command detected: {command} {arguments}");
                }
            }

            // ファイル名抽出
            if (command == "RETR" || command == "STOR" || command == "APPE" || 
                command == "DELE" || command == "RNFR" || command == "RNTO")
            {
                result.FileName = arguments.Trim();
                _logger.Info($"FTP file operation: {command} -> {result.FileName}");
            }

            // パラメータ格納
            result.Parameters["Command"] = command;
            if (!string.IsNullOrEmpty(arguments))
            {
                result.Parameters["Arguments"] = arguments;
            }

            _logger.Debug($"FTP Command: {command} {arguments}");
        }

        /// <summary>
        /// プロトコルタイプを検出（同期版）
        /// </summary>
        public ProtocolType DetectProtocol(byte[] packetData)
        {
            return ProtocolType.FTP;
        }

        /// <summary>
        /// パケットを解析（同期版）
        /// </summary>
        public ProtocolAnalysisResult Analyze(byte[] packetData, ProtocolType protocolType)
        {
            return AnalyzeFTPAsync(packetData).GetAwaiter().GetResult();
        }
    }
}