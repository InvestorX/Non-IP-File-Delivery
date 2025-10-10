using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// ClamAVマルウェアスキャナー
    /// clamdソケット通信（INSTREAMプロトコル）を使用
    /// </summary>
    public class ClamAVScanner
    {
        private readonly ILoggingService _logger;
        private readonly string _clamdHost;
        private readonly int _clamdPort;

        // Statistics tracking
        private int _totalScans = 0;
        private int _totalThreats = 0;
        private int _totalErrors = 0;
        private readonly List<TimeSpan> _scanDurations = new List<TimeSpan>();
        private readonly object _statsLock = new object();

        /// <summary>
        /// 総スキャン数
        /// </summary>
        public int TotalScans
        {
            get { lock (_statsLock) return _totalScans; }
        }

        /// <summary>
        /// 総脅威検出数
        /// </summary>
        public int TotalThreats
        {
            get { lock (_statsLock) return _totalThreats; }
        }

        /// <summary>
        /// 総エラー数
        /// </summary>
        public int TotalErrors
        {
            get { lock (_statsLock) return _totalErrors; }
        }

        /// <summary>
        /// 平均スキャン時間
        /// </summary>
        public TimeSpan AverageScanDuration
        {
            get
            {
                lock (_statsLock)
                {
                    if (_scanDurations.Count == 0) return TimeSpan.Zero;
                    return TimeSpan.FromMilliseconds(_scanDurations.Average(d => d.TotalMilliseconds));
                }
            }
        }

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="logger">ロギングサービス</param>
        /// <param name="clamdHost">clamdホスト（デフォルト: localhost）</param>
        /// <param name="clamdPort">clamdポート（デフォルト: 3310）</param>
        public ClamAVScanner(ILoggingService logger, string clamdHost = "localhost", int clamdPort = 3310)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clamdHost = clamdHost;
            _clamdPort = clamdPort;

            _logger.Info($"ClamAVScanner initialized: {_clamdHost}:{_clamdPort}");
        }

        /// <summary>
        /// clamdへの接続テスト
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_clamdHost, _clamdPort);

                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.ASCII);

                await writer.WriteLineAsync("PING");
                var response = await reader.ReadLineAsync();

                var isConnected = response?.Trim() == "PONG";
                _logger.Info($"ClamAV connection test: {(isConnected ? "OK" : "FAIL")}");
                return isConnected;
            }
            catch (Exception ex)
            {
                _logger.Error($"ClamAV connection test failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// データをClamAVでスキャン
        /// </summary>
        /// <param name="data">スキャン対象データ</param>
        /// <param name="timeoutMs">タイムアウト（ミリ秒）</param>
        /// <returns>スキャン結果</returns>
        public async Task<ClamAVScanResult> ScanAsync(byte[] data, int timeoutMs = 5000)
        {
            var startTime = DateTime.UtcNow;

            if (data == null || data.Length == 0)
            {
                _logger.Warning("Attempted to scan empty data");
                return new ClamAVScanResult { IsClean = true, ScanMethod = "INSTREAM" };
            }

            try
            {
                _logger.Debug($"Scanning {data.Length} bytes with ClamAV...");

                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(_clamdHost, _clamdPort);
                var completedTask = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));

                if (completedTask != connectTask)
                {
                    _logger.Warning($"ClamAV connection timeout ({timeoutMs}ms)");
                    return new ClamAVScanResult
                    {
                        IsClean = false,
                        ErrorMessage = "Connection timeout"
                    };
                }

                using var stream = client.GetStream();
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;

                // INSTREAMプロトコル: データをチャンク単位で送信
                var command = Encoding.ASCII.GetBytes("zINSTREAM\0");
                await stream.WriteAsync(command, 0, command.Length);

                // データサイズを送信（4バイト、ネットワークバイトオーダー）
                var sizeBytes = BitConverter.GetBytes(data.Length);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(sizeBytes);
                }
                await stream.WriteAsync(sizeBytes, 0, sizeBytes.Length);

                // データ本体を送信
                await stream.WriteAsync(data, 0, data.Length);

                // 終了マーカー（サイズ0）を送信
                var endMarker = new byte[4] { 0, 0, 0, 0 };
                await stream.WriteAsync(endMarker, 0, endMarker.Length);

                // 応答を受信
                using var reader = new StreamReader(stream, Encoding.ASCII);
                var response = await reader.ReadLineAsync();

                if (string.IsNullOrEmpty(response))
                {
                    _logger.Warning("ClamAV returned empty response");
                    return new ClamAVScanResult
                    {
                        IsClean = false,
                        ErrorMessage = "Empty response from ClamAV"
                    };
                }

                _logger.Debug($"ClamAV response: {response}");

                // 応答解析: "stream: OK" or "stream: Win.Test.EICAR_HDB-1 FOUND"
                var duration = DateTime.UtcNow - startTime;
                lock (_statsLock)
                {
                    _totalScans++;
                    _scanDurations.Add(duration);
                }

                if (response.Contains("OK"))
                {
                    _logger.Debug("ClamAV scan completed: No threats detected");
                    return new ClamAVScanResult
                    {
                        IsClean = true,
                        ScanMethod = "INSTREAM",
                        ScanDuration = duration,
                        FileSize = data.Length
                    };
                }
                else if (response.Contains("FOUND"))
                {
                    var virusName = ExtractVirusName(response);
                    _logger.Warning($"ClamAV detected virus: {virusName}");

                    lock (_statsLock)
                    {
                        _totalThreats++;
                    }

                    return new ClamAVScanResult
                    {
                        IsClean = false,
                        VirusName = virusName,
                        Details = response,
                        ScanMethod = "INSTREAM",
                        ScanDuration = duration,
                        FileSize = data.Length
                    };
                }
                else
                {
                    _logger.Warning($"Unexpected ClamAV response: {response}");

                    lock (_statsLock)
                    {
                        _totalErrors++;
                    }

                    return new ClamAVScanResult
                    {
                        IsClean = false,
                        ErrorMessage = $"Unexpected response: {response}",
                        ScanMethod = "INSTREAM",
                        ScanDuration = duration,
                        FileSize = data.Length
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"ClamAV scan error: {ex.Message}", ex);

                var duration = DateTime.UtcNow - startTime;
                lock (_statsLock)
                {
                    _totalScans++;
                    _totalErrors++;
                    _scanDurations.Add(duration);
                }

                return new ClamAVScanResult
                {
                    IsClean = false,
                    ErrorMessage = ex.Message,
                    ScanMethod = "INSTREAM",
                    ScanDuration = duration,
                    FileSize = data.Length
                };
            }
        }

        /// <summary>
        /// ClamAV応答からウイルス名を抽出
        /// </summary>
        private string ExtractVirusName(string response)
        {
            // 例: "stream: Win.Test.EICAR_HDB-1 FOUND"
            var parts = response.Split(':');
            if (parts.Length >= 2)
            {
                var virusPart = parts[1].Trim();
                return virusPart.Replace(" FOUND", "").Trim();
            }
            return "Unknown";
        }

        /// <summary>
        /// ClamAVバージョンを取得
        /// </summary>
        public async Task<string?> GetVersionAsync()
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_clamdHost, _clamdPort);

                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.ASCII);

                await writer.WriteLineAsync("VERSION");
                var version = await reader.ReadLineAsync();

                _logger.Info($"ClamAV version: {version}");
                return version;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get ClamAV version: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// MULTISCANコマンド: 複数ファイルを並列スキャン
        /// </summary>
        /// <param name="filePaths">スキャン対象ファイルパス配列</param>
        /// <param name="timeoutMs">タイムアウト（ミリ秒）</param>
        /// <returns>スキャン結果</returns>
        public async Task<ClamAVScanResult> MultiScanAsync(string[] filePaths, int timeoutMs = 30000)
        {
            var startTime = DateTime.UtcNow;

            if (filePaths == null || filePaths.Length == 0)
            {
                _logger.Warning("No files specified for MULTISCAN");
                return new ClamAVScanResult
                {
                    IsClean = true,
                    ScanMethod = "MULTISCAN",
                    ErrorMessage = "No files specified"
                };
            }

            try
            {
                _logger.Info($"Starting MULTISCAN for {filePaths.Length} files...");

                var results = new List<string>();
                int threatsFound = 0;
                long totalSize = 0;

                foreach (var filePath in filePaths)
                {
                    if (!File.Exists(filePath))
                    {
                        _logger.Warning($"File not found: {filePath}");
                        continue;
                    }

                    var fileInfo = new FileInfo(filePath);
                    totalSize += fileInfo.Length;

                    using var client = new TcpClient();
                    await client.ConnectAsync(_clamdHost, _clamdPort);

                    using var stream = client.GetStream();
                    stream.ReadTimeout = timeoutMs;
                    stream.WriteTimeout = timeoutMs;

                    // MULTISCANコマンドを送信
                    var command = $"MULTISCAN {filePath}\n";
                    var commandBytes = Encoding.ASCII.GetBytes(command);
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length);

                    // 応答を受信
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    var response = await reader.ReadLineAsync();

                    if (!string.IsNullOrEmpty(response))
                    {
                        results.Add(response);
                        if (response.Contains("FOUND"))
                        {
                            threatsFound++;
                        }
                    }
                }

                var duration = DateTime.UtcNow - startTime;
                lock (_statsLock)
                {
                    _totalScans++;
                    _scanDurations.Add(duration);
                    if (threatsFound > 0)
                    {
                        _totalThreats += threatsFound;
                    }
                }

                var isClean = threatsFound == 0;
                _logger.Info($"MULTISCAN completed: {filePaths.Length} files, {threatsFound} threats found");

                return new ClamAVScanResult
                {
                    IsClean = isClean,
                    Details = string.Join("\n", results),
                    ScanMethod = "MULTISCAN",
                    ScanDuration = duration,
                    FileSize = totalSize,
                    TotalFilesScanned = filePaths.Length,
                    TotalThreatsFound = threatsFound
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"MULTISCAN error: {ex.Message}", ex);

                var duration = DateTime.UtcNow - startTime;
                lock (_statsLock)
                {
                    _totalScans++;
                    _totalErrors++;
                    _scanDurations.Add(duration);
                }

                return new ClamAVScanResult
                {
                    IsClean = false,
                    ErrorMessage = ex.Message,
                    ScanMethod = "MULTISCAN",
                    ScanDuration = duration
                };
            }
        }

        /// <summary>
        /// CONTSCANコマンド: ファイル/ディレクトリを連続スキャン
        /// </summary>
        /// <param name="path">スキャン対象パス</param>
        /// <param name="timeoutMs">タイムアウト（ミリ秒）</param>
        /// <returns>スキャン結果</returns>
        public async Task<ClamAVScanResult> ContScanAsync(string path, int timeoutMs = 60000)
        {
            var startTime = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(path))
            {
                _logger.Warning("No path specified for CONTSCAN");
                return new ClamAVScanResult
                {
                    IsClean = true,
                    ScanMethod = "CONTSCAN",
                    ErrorMessage = "No path specified"
                };
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                _logger.Warning($"Path not found: {path}");
                return new ClamAVScanResult
                {
                    IsClean = false,
                    ScanMethod = "CONTSCAN",
                    ErrorMessage = "Path not found",
                    FilePath = path
                };
            }

            try
            {
                _logger.Info($"Starting CONTSCAN for: {path}");

                using var client = new TcpClient();
                await client.ConnectAsync(_clamdHost, _clamdPort);

                using var stream = client.GetStream();
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;

                // CONTSCANコマンドを送信
                var command = $"CONTSCAN {path}\n";
                var commandBytes = Encoding.ASCII.GetBytes(command);
                await stream.WriteAsync(commandBytes, 0, commandBytes.Length);

                // 応答を受信（複数行の可能性あり）
                using var reader = new StreamReader(stream, Encoding.ASCII);
                var results = new List<string>();
                string? line;
                int threatsFound = 0;
                int filesScanned = 0;

                while ((line = await reader.ReadLineAsync()) != null)
                {
                    results.Add(line);
                    if (line.Contains("FOUND"))
                    {
                        threatsFound++;
                    }
                    filesScanned++;
                }

                var duration = DateTime.UtcNow - startTime;
                lock (_statsLock)
                {
                    _totalScans++;
                    _scanDurations.Add(duration);
                    if (threatsFound > 0)
                    {
                        _totalThreats += threatsFound;
                    }
                }

                var isClean = threatsFound == 0;
                _logger.Info($"CONTSCAN completed: {filesScanned} files, {threatsFound} threats found");

                return new ClamAVScanResult
                {
                    IsClean = isClean,
                    Details = string.Join("\n", results),
                    ScanMethod = "CONTSCAN",
                    ScanDuration = duration,
                    FilePath = path,
                    TotalFilesScanned = filesScanned,
                    TotalThreatsFound = threatsFound
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"CONTSCAN error: {ex.Message}", ex);

                var duration = DateTime.UtcNow - startTime;
                lock (_statsLock)
                {
                    _totalScans++;
                    _totalErrors++;
                    _scanDurations.Add(duration);
                }

                return new ClamAVScanResult
                {
                    IsClean = false,
                    ErrorMessage = ex.Message,
                    ScanMethod = "CONTSCAN",
                    ScanDuration = duration,
                    FilePath = path
                };
            }
        }

        /// <summary>
        /// STATSコマンド: clamdの統計情報を取得
        /// </summary>
        /// <returns>統計情報（文字列）</returns>
        public async Task<string?> GetStatsAsync()
        {
            try
            {
                _logger.Debug("Requesting ClamAV stats...");

                using var client = new TcpClient();
                await client.ConnectAsync(_clamdHost, _clamdPort);

                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.ASCII);

                await writer.WriteLineAsync("STATS");

                // 複数行の統計情報を受信
                var stats = new StringBuilder();
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    stats.AppendLine(line);
                }

                var statsText = stats.ToString();
                _logger.Info($"ClamAV stats retrieved: {statsText.Length} bytes");
                return statsText;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get ClamAV stats: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// RELOADコマンド: clamdのウイルス定義データベースを再読み込み
        /// </summary>
        /// <returns>成功した場合true</returns>
        public async Task<bool> ReloadDatabaseAsync()
        {
            try
            {
                _logger.Info("Reloading ClamAV virus database...");

                using var client = new TcpClient();
                await client.ConnectAsync(_clamdHost, _clamdPort);

                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.ASCII);

                await writer.WriteLineAsync("RELOAD");
                var response = await reader.ReadLineAsync();

                var success = response?.Trim() == "RELOADING";
                _logger.Info($"ClamAV database reload: {(success ? "SUCCESS" : "FAILED")} - {response}");
                return success;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to reload ClamAV database: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// ローカル統計情報を取得
        /// </summary>
        /// <returns>統計情報の辞書</returns>
        public Dictionary<string, object> GetLocalStatistics()
        {
            lock (_statsLock)
            {
                return new Dictionary<string, object>
                {
                    { "TotalScans", _totalScans },
                    { "TotalThreats", _totalThreats },
                    { "TotalErrors", _totalErrors },
                    { "AverageScanDuration", AverageScanDuration.TotalMilliseconds },
                    { "ScanCount", _scanDurations.Count }
                };
            }
        }
    }
}
