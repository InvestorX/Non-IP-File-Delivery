// WindowsDefenderScanner.cs
// Windows Defender 統合スキャナー
// PowerShell/MpCmdRun.exe 経由でスキャン実行

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// Windows Defender 統合スキャナー
    /// PowerShell または MpCmdRun.exe を使用してスキャンを実行
    /// </summary>
    public class WindowsDefenderScanner : IDisposable
    {
        private readonly ILoggingService _logger;
        private readonly string _tempDirectory;
        private bool _isWindows;
        private bool _defenderAvailable;
        private string? _mpCmdRunPath;
        private int _totalScans = 0;
        private int _totalThreats = 0;

        /// <summary>
        /// Windows環境かどうか
        /// </summary>
        public bool IsWindows => _isWindows;

        /// <summary>
        /// Windows Defenderが利用可能かどうか
        /// </summary>
        public bool DefenderAvailable => _defenderAvailable;

        /// <summary>
        /// 総スキャン回数
        /// </summary>
        public int TotalScans => _totalScans;

        /// <summary>
        /// 総脅威検出数
        /// </summary>
        public int TotalThreats => _totalThreats;

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="logger">ロギングサービス</param>
        public WindowsDefenderScanner(ILoggingService logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tempDirectory = Path.Combine(Path.GetTempPath(), "NonIPDefenderScans");

            // Windows環境検出
            _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            if (_isWindows)
            {
                // 一時ディレクトリ作成
                if (!Directory.Exists(_tempDirectory))
                {
                    Directory.CreateDirectory(_tempDirectory);
                }

                // Windows Defender の可用性チェック
                CheckDefenderAvailability();
            }
            else
            {
                _logger.Warning("Windows Defender is only available on Windows OS");
                _defenderAvailable = false;
            }
        }

        /// <summary>
        /// Windows Defenderの可用性をチェック
        /// </summary>
        private void CheckDefenderAvailability()
        {
            try
            {
                // MpCmdRun.exe のパスを検索
                var possiblePaths = new[]
                {
                    @"C:\Program Files\Windows Defender\MpCmdRun.exe",
                    @"C:\ProgramData\Microsoft\Windows Defender\Platform\*\MpCmdRun.exe"
                };

                foreach (var path in possiblePaths)
                {
                    if (path.Contains("*"))
                    {
                        // ワイルドカードを含む場合はディレクトリ検索
                        var baseDir = Path.GetDirectoryName(path.Replace("*", ""));
                        if (Directory.Exists(baseDir))
                        {
                            var files = Directory.GetFiles(baseDir, "MpCmdRun.exe", SearchOption.AllDirectories);
                            if (files.Length > 0)
                            {
                                _mpCmdRunPath = files[0];
                                _defenderAvailable = true;
                                _logger.Info($"Windows Defender found: {_mpCmdRunPath}");
                                return;
                            }
                        }
                    }
                    else if (File.Exists(path))
                    {
                        _mpCmdRunPath = path;
                        _defenderAvailable = true;
                        _logger.Info($"Windows Defender found: {_mpCmdRunPath}");
                        return;
                    }
                }

                _logger.Warning("Windows Defender MpCmdRun.exe not found");
                _defenderAvailable = false;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to check Windows Defender availability: {ex.Message}", ex);
                _defenderAvailable = false;
            }
        }

        /// <summary>
        /// データをスキャン（非同期）
        /// </summary>
        /// <param name="data">スキャン対象データ</param>
        /// <param name="fileName">ファイル名（一時ファイル用）</param>
        /// <param name="timeoutMs">タイムアウト（ミリ秒）</param>
        /// <returns>スキャン結果</returns>
        public async Task<DefenderScanResult> ScanAsync(byte[] data, string fileName = "scan_data.bin", int timeoutMs = 60000)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                System.Threading.Interlocked.Increment(ref _totalScans);

                if (!_isWindows)
                {
                    return new DefenderScanResult
                    {
                        IsClean = true,
                        DefenderAvailable = false,
                        ErrorMessage = "Windows Defender is only available on Windows OS",
                        ScanDuration = stopwatch.Elapsed
                    };
                }

                if (!_defenderAvailable)
                {
                    return new DefenderScanResult
                    {
                        IsClean = true,
                        DefenderAvailable = false,
                        ErrorMessage = "Windows Defender is not available on this system",
                        ScanDuration = stopwatch.Elapsed
                    };
                }

                if (data == null || data.Length == 0)
                {
                    return new DefenderScanResult
                    {
                        IsClean = true,
                        DefenderAvailable = true,
                        Details = "Empty data (skipped scan)",
                        ScanDuration = stopwatch.Elapsed
                    };
                }

                _logger.Debug($"Scanning {fileName} ({data.Length} bytes) with Windows Defender...");

                // 一時ファイルに書き込み
                var tempFilePath = Path.Combine(_tempDirectory, $"{Guid.NewGuid()}_{Path.GetFileName(fileName)}");
                await File.WriteAllBytesAsync(tempFilePath, data);

                try
                {
                    // スキャン実行
                    var result = await ScanFileAsync(tempFilePath, timeoutMs);
                    result.ScanDuration = stopwatch.Elapsed;

                    if (!result.IsClean)
                    {
                        System.Threading.Interlocked.Increment(ref _totalThreats);
                        _logger.Warning($"Windows Defender detected threat: {result.ThreatName}");
                    }
                    else
                    {
                        _logger.Debug($"Windows Defender scan completed: {fileName} is clean");
                    }

                    return result;
                }
                finally
                {
                    // 一時ファイルを削除
                    try
                    {
                        if (File.Exists(tempFilePath))
                        {
                            File.Delete(tempFilePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Failed to delete temp file: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.Error($"Windows Defender scan error: {ex.Message}", ex);
                return new DefenderScanResult
                {
                    IsClean = false,
                    DefenderAvailable = _defenderAvailable,
                    ErrorMessage = ex.Message,
                    ScanDuration = stopwatch.Elapsed
                };
            }
        }

        /// <summary>
        /// ファイルをスキャン（MpCmdRun.exe使用）
        /// </summary>
        private async Task<DefenderScanResult> ScanFileAsync(string filePath, int timeoutMs)
        {
            try
            {
                // MpCmdRun.exe -Scan -ScanType 3 -File "filepath"
                // ScanType 3 = カスタムスキャン（指定ファイル/フォルダ）
                var startInfo = new ProcessStartInfo
                {
                    FileName = _mpCmdRunPath,
                    Arguments = $"-Scan -ScanType 3 -File \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                var output = new StringBuilder();
                var error = new StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        output.AppendLine(e.Data);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        error.AppendLine(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var completed = await Task.Run(() => process.WaitForExit(timeoutMs));

                if (!completed)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch { }

                    return new DefenderScanResult
                    {
                        IsClean = false,
                        DefenderAvailable = true,
                        ErrorMessage = $"Scan timeout ({timeoutMs}ms)",
                        ScanPath = filePath,
                        ScanMethod = "MpCmdRun.exe"
                    };
                }

                var exitCode = process.ExitCode;
                var outputText = output.ToString();
                var errorText = error.ToString();

                _logger.Debug($"MpCmdRun exit code: {exitCode}");
                _logger.Debug($"MpCmdRun output: {outputText}");

                // 結果パース
                return ParseScanResult(exitCode, outputText, errorText, filePath);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to run MpCmdRun.exe: {ex.Message}", ex);
                return new DefenderScanResult
                {
                    IsClean = false,
                    DefenderAvailable = true,
                    ErrorMessage = $"MpCmdRun execution failed: {ex.Message}",
                    ScanPath = filePath,
                    ScanMethod = "MpCmdRun.exe"
                };
            }
        }

        /// <summary>
        /// MpCmdRunの出力をパースして結果を返す
        /// </summary>
        private DefenderScanResult ParseScanResult(int exitCode, string output, string error, string filePath)
        {
            var result = new DefenderScanResult
            {
                DefenderAvailable = true,
                ScanPath = filePath,
                ScanMethod = "MpCmdRun.exe"
            };

            // Exit code 0 = クリーン
            // Exit code 2 = 脅威検出
            if (exitCode == 0)
            {
                result.IsClean = true;
                result.Details = "No threats detected";
                return result;
            }

            if (exitCode == 2)
            {
                result.IsClean = false;
                result.Severity = ThreatLevel.High;

                // 出力から脅威名を抽出
                var threatName = ExtractThreatName(output);
                result.ThreatName = threatName ?? "Unknown threat";
                result.Details = $"Threat detected: {result.ThreatName}";

                return result;
            }

            // その他のエラーコード
            result.IsClean = false;
            result.ErrorMessage = $"MpCmdRun exit code: {exitCode}. Error: {error}";
            result.Details = output;

            return result;
        }

        /// <summary>
        /// 出力から脅威名を抽出
        /// </summary>
        private string? ExtractThreatName(string output)
        {
            try
            {
                // "Threat" や "Virus" などのキーワードを含む行を探す
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.Contains("Threat", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Virus", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Malware", StringComparison.OrdinalIgnoreCase))
                    {
                        // コロンの後の部分を抽出
                        var parts = line.Split(':');
                        if (parts.Length >= 2)
                        {
                            return parts[1].Trim();
                        }

                        return line.Trim();
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Windows Defenderサービスの状態を取得
        /// </summary>
        public async Task<bool> CheckServiceStatusAsync()
        {
            if (!_isWindows || !_defenderAvailable)
            {
                return false;
            }

            try
            {
                // PowerShellでWindows Defenderサービスの状態を確認
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-Command \"Get-Service -Name WinDefend | Select-Object -ExpandProperty Status\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return false;
                }

                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                var status = output.Trim();
                _logger.Debug($"Windows Defender service status: {status}");

                return status.Equals("Running", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to check Windows Defender service status: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 統計情報を取得
        /// </summary>
        public string GetStats()
        {
            return $"WindowsDefenderScanner Stats: " +
                   $"Available={_defenderAvailable}, " +
                   $"Scans={_totalScans}, " +
                   $"Threats={_totalThreats}, " +
                   $"OS={(_isWindows ? "Windows" : "Non-Windows")}";
        }

        /// <summary>
        /// リソースのクリーンアップ
        /// </summary>
        public void Dispose()
        {
            try
            {
                // 一時ディレクトリをクリーンアップ
                if (Directory.Exists(_tempDirectory))
                {
                    Directory.Delete(_tempDirectory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to cleanup temp directory: {ex.Message}");
            }

            _logger.Info($"WindowsDefenderScanner disposed. {GetStats()}");
        }
    }
}
