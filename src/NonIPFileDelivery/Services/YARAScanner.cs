using System;
using System.IO;
using System.Threading.Tasks;
using libyaraNET;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// YARAルールベースのマルウェアスキャナー
    /// libyara.NET v4.5.0を使用
    /// </summary>
    public class YARAScanner : IDisposable
    {
        private readonly ILoggingService _logger;
        private readonly string _rulesPath;
        private Rules? _compiledRules;
        private bool _disposed;

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="logger">ロギングサービス</param>
        /// <param name="rulesPath">YARAルールファイルのパス（.yarファイル）</param>
        public YARAScanner(ILoggingService logger, string rulesPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _rulesPath = rulesPath ?? throw new ArgumentNullException(nameof(rulesPath));

            if (!File.Exists(_rulesPath))
            {
                throw new FileNotFoundException($"YARA rules file not found: {_rulesPath}");
            }

            LoadRules();
        }

        /// <summary>
        /// YARAルールファイルを読み込み、コンパイル
        /// </summary>
        private void LoadRules()
        {
            try
            {
                _logger.Info($"Loading YARA rules from: {_rulesPath}");

                using var ctx = new YaraContext();
                using var compiler = new Compiler();

                // ルールファイルをコンパイル
                compiler.AddRuleFile(_rulesPath);
                _compiledRules = compiler.Compile();

                var ruleCount = _compiledRules?.Rules?.Count ?? 0;
                _logger.Info($"YARA rules loaded successfully: {ruleCount} rules");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load YARA rules: {ex.Message}", ex);
                throw new InvalidOperationException("YARA rules compilation failed", ex);
            }
        }

        /// <summary>
        /// データをYARAルールでスキャン
        /// </summary>
        /// <param name="data">スキャン対象データ</param>
        /// <param name="timeoutMs">タイムアウト（ミリ秒）</param>
        /// <returns>スキャン結果</returns>
        public async Task<YARAScanResult> ScanAsync(byte[] data, int timeoutMs = 5000)
        {
            if (_compiledRules == null)
            {
                _logger.Error("YARA rules not loaded");
                return new YARAScanResult
                {
                    IsMatch = false,
                    ErrorMessage = "YARA rules not loaded"
                };
            }

            if (data == null || data.Length == 0)
            {
                _logger.Warning("Attempted to scan empty data");
                return new YARAScanResult { IsMatch = false };
            }

            try
            {
                _logger.Debug($"Scanning {data.Length} bytes with YARA rules...");

                // タイムアウト付きスキャン実行
                var scanTask = Task.Run(() =>
                {
                    using var scanner = new Scanner();
                    var results = scanner.ScanMemory(data, _compiledRules);
                    return results;
                });

                var completedTask = await Task.WhenAny(scanTask, Task.Delay(timeoutMs));

                if (completedTask != scanTask)
                {
                    _logger.Warning($"YARA scan timeout ({timeoutMs}ms)");
                    return new YARAScanResult
                    {
                        IsMatch = false,
                        ErrorMessage = "Scan timeout"
                    };
                }

                var scanResults = await scanTask;

                if (scanResults != null && scanResults.Count > 0)
                {
                    var firstMatch = scanResults[0];
                    _logger.Warning($"YARA rule matched: {firstMatch.Rule.Identifier}");

                    return new YARAScanResult
                    {
                        IsMatch = true,
                        RuleName = firstMatch.Rule.Identifier,
                        MatchedStrings = firstMatch.Matches.Count,
                        Details = $"Rule: {firstMatch.Rule.Identifier}, Matches: {firstMatch.Matches.Count}"
                    };
                }

                _logger.Debug("YARA scan completed: No threats detected");
                return new YARAScanResult { IsMatch = false };
            }
            catch (Exception ex)
            {
                _logger.Error($"YARA scan error: {ex.Message}", ex);
                return new YARAScanResult
                {
                    IsMatch = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// YARAルールをリロード
        /// </summary>
        public void ReloadRules()
        {
            _logger.Info("Reloading YARA rules...");
            _compiledRules?.Dispose();
            LoadRules();
        }

        public void Dispose()
        {
            if (_disposed) return;

            _compiledRules?.Dispose();
            _disposed = true;
            _logger.Info("YARAScanner disposed");
        }
    }
}
