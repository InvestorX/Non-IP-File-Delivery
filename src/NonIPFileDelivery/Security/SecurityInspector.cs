using Serilog;
using System.Text;

namespace NonIpFileDelivery.Security;

/// <summary>
/// セキュリティ検閲エンジン
/// YARAルールをサポート予定（現在は基本的な検証のみ実装）
/// </summary>
public class SecurityInspector : IDisposable
{
    private readonly HashSet<string> _detectedThreats;
    private readonly object _lockObject = new();
    private readonly bool _yaraEnabled;

    /// <summary>
    /// YARAルールファイルからセキュリティインスペクターを初期化
    /// </summary>
    /// <param name="rulesPath">YARAルールファイルのパス（複数可）</param>
    public SecurityInspector(params string[] rulesPath)
    {
        _detectedThreats = new HashSet<string>();
        _yaraEnabled = false;

        // YARA統合は今後の実装
        // dnYara 2.1.0 API との互換性の問題があるため、現在は無効化
        if (rulesPath?.Length > 0)
        {
            Log.Warning("YARA scanning is not yet fully implemented - basic validation only");
        }
    }

    /// <summary>
    /// バイトデータをスキャン（現在は基本チェックのみ）
    /// </summary>
    /// <param name="data">検査対象データ</param>
    /// <param name="metadata">データのメタ情報（ログ用）</param>
    /// <returns>脅威が検出された場合はtrue</returns>
    public bool ScanData(byte[] data, string metadata = "")
    {
        if (data == null || data.Length == 0)
        {
            return false;
        }

        // 基本的なパターンマッチング（将来的にYARAに置き換え）
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 8192));
            
            // 危険なパターンの簡易検出
            var suspiciousPatterns = new[]
            {
                "eval(", "exec(", "system(", "cmd.exe", "powershell.exe",
                "<script", "javascript:", "data:text/html",
                "../../../", "..\\..\\..\\", // パストラバーサル
            };

            foreach (var pattern in suspiciousPatterns)
            {
                if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("Suspicious pattern detected: {Pattern} in {Metadata}", pattern, metadata);
                    lock (_lockObject)
                    {
                        _detectedThreats.Add($"{pattern}:{metadata}");
                    }
                    return true;
                }
            }

            return false; // クリーン
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during data scan");
            return false;
        }
    }

    /// <summary>
    /// ファイルをYARAルールでスキャン
    /// </summary>
    /// <param name="filePath">スキャン対象ファイルパス</param>
    /// <returns>脅威が検出された場合はtrue</returns>
    public bool ScanFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Log.Warning("File not found for scanning: {FilePath}", filePath);
            return false;
        }

        try
        {
            var data = File.ReadAllBytes(filePath);
            return ScanData(data, Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error scanning file: {FilePath}", filePath);
            return true; // エラー時は安全側
        }
    }

    /// <summary>
    /// FTPコマンドプロトコルの妥当性検証
    /// </summary>
    /// <param name="ftpCommand">FTPコマンド文字列</param>
    /// <returns>不正なコマンドの場合はtrue</returns>
    public bool ValidateFtpCommand(string ftpCommand)
    {
        // FTP RFCに準拠した基本コマンドのホワイトリスト
        var allowedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "USER", "PASS", "ACCT", "CWD", "CDUP", "SMNT", "QUIT",
            "REIN", "PORT", "PASV", "TYPE", "STRU", "MODE", "RETR",
            "STOR", "STOU", "APPE", "ALLO", "REST", "RNFR", "RNTO",
            "ABOR", "DELE", "RMD", "MKD", "PWD", "LIST", "NLST",
            "SITE", "SYST", "STAT", "HELP", "NOOP"
        };

        var commandParts = ftpCommand.Trim().Split(' ', 2);
        var command = commandParts[0].ToUpperInvariant();

        if (!allowedCommands.Contains(command))
        {
            Log.Warning("Suspicious FTP command detected: {Command}", ftpCommand);
            return true; // 不正
        }

        // コマンドインジェクション検知
        if (ftpCommand.Contains("$(") || ftpCommand.Contains("`") ||
            ftpCommand.Contains(";") || ftpCommand.Contains("&&") ||
            ftpCommand.Contains("||") || ftpCommand.Contains("|"))
        {
            Log.Warning("Potential FTP command injection: {Command}", ftpCommand);
            return true; // 不正
        }

        return false; // 正常
    }

    /// <summary>
    /// 検出された脅威の統計情報を取得
    /// </summary>
    public (int TotalThreats, IEnumerable<string> ThreatList) GetThreatStatistics()
    {
        lock (_lockObject)
        {
            return (_detectedThreats.Count, _detectedThreats.ToList());
        }
    }

    public void Dispose()
    {
        Log.Information("SecurityInspector disposed. Total threats detected: {Count}",
            _detectedThreats.Count);
    }
}