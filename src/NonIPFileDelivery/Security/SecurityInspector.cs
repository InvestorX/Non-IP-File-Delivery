using dnYara;
using Serilog;
using System.Text;

namespace NonIpFileDelivery.Security;

/// <summary>
/// YARAルールを使用したセキュリティ検閲エンジン
/// ランサムウェア、マルウェア、不正なプロトコルパターンを検知
/// </summary>
public class SecurityInspector : IDisposable
{
    private readonly Context _yaraContext;
    private readonly Rules _compiledRules;
    private readonly HashSet<string> _detectedThreats;
    private readonly object _lockObject = new();

    /// <summary>
    /// YARAルールファイルからセキュリティインスペクターを初期化
    /// </summary>
    /// <param name="rulesPath">YARAルールファイルのパス（複数可）</param>
    public SecurityInspector(params string[] rulesPath)
    {
        _yaraContext = new Context();
        _detectedThreats = new HashSet<string>();

        try
        {
            using var compiler = new Compiler();

            foreach (var path in rulesPath)
            {
                if (File.Exists(path))
                {
                    compiler.AddRuleFile(path);
                    Log.Information("Loaded YARA rules: {RulePath}", path);
                }
                else
                {
                    Log.Warning("YARA rule file not found: {RulePath}", path);
                }
            }

            _compiledRules = compiler.Compile();
            Log.Information("YARA rules compiled successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to compile YARA rules");
            throw;
        }
    }

    /// <summary>
    /// バイトデータをYARAルールでスキャン
    /// </summary>
    /// <param name="data">検査対象データ</param>
    /// <param name="metadata">データのメタ情報（ログ用）</param>
    /// <returns>脅威が検出された場合はtrue</returns>
    public bool ScanData(byte[] data, string metadata = "")
    {
        try
        {
            var scanner = new Scanner();
            var results = scanner.ScanMemory(data, _compiledRules);

            if (results.Any())
            {
                lock (_lockObject)
                {
                    foreach (var match in results)
                    {
                        var threatSignature = $"{match.Rule.Identifier}:{metadata}";
                        _detectedThreats.Add(threatSignature);

                        Log.Warning("SECURITY ALERT: Threat detected - Rule: {Rule}, Meta: {Meta}, Matches: {Matches}",
                            match.Rule.Identifier,
                            metadata,
                            match.Matches.Count);
                    }
                }

                return true; // 脅威検出
            }

            return false; // クリーン
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during YARA scan");
            // スキャンエラー時は安全側に倒して脅威として扱う
            return true;
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
        _compiledRules?.Dispose();
        _yaraContext?.Dispose();
        Log.Information("SecurityInspector disposed. Total threats detected: {Count}",
            _detectedThreats.Count);
    }
}