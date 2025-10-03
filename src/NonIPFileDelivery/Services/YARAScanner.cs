using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using dnYara;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// YARAスキャナー実装（libyara.NET使用）
    /// </summary>
    public class YARAScanner : IDisposable
    {
        private readonly ILoggingService _logger;
        private readonly string _rulesPath;
        private Rules? _compiledRules;
        private bool _disposed;

        public YARAScanner(ILoggingService logger, string rulesPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _rulesPath = rulesPath ?? throw new ArgumentNullException(nameof(rulesPath));

            if (!File.Exists(_rulesPath))
                throw new FileNotFoundException($"YARA rules file not found: {_rulesPath}");

            LoadRules();
        }

        /// <summary>
        /// YARAルールファイルを読み込み・コンパイル
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
                _compiledRules = compiler.GetRules();

                _logger.Info($"YARA rules compiled successfully: {_rulesPath}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load YARA rules: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// データをYARAルールでスキャン（非同期・タイムアウト付き）
        /// </summary>
        public async Task<YARAScanResult> ScanAsync(byte[] data, int timeoutMs)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty", nameof(data));

            if (_compiledRules == null)
                throw new InvalidOperationException("YARA rules not loaded");

            try
            {
                _logger.Debug($"Starting YARA scan ({data.Length} bytes, timeout: {timeoutMs}ms)");

                using var cts = new CancellationTokenSource(timeoutMs);
                
                // 非同期スキャン実行
                var scanTask = Task.Run(() =>
                {
                    using var scanner = new Scanner();
                    return scanner.ScanMemory(data, _compiledRules);
                }, cts.Token);

                var matches = await scanTask;

                if (matches.Count > 0)
                {
                    var firstMatch = matches[0];
                    _logger.Warning($"YARA rule matched: {firstMatch.Rule.Identifier} " +
                                   $"({firstMatch.Matches.Count} string matches)");

                    return new YARAScanResult
                    {
                        IsMatch = true,
                        RuleName = firstMatch.Rule.Identifier,
                        MatchedStrings = firstMatch.Matches.Count,
                        Details = $"Rule: {firstMatch.Rule.Identifier}, Matches: {firstMatch.Matches.Count}"
                    };
                }

                _logger.Debug("YARA scan completed: No matches");
                return new YARAScanResult
                {
                    IsMatch = false,
                    RuleName = null,
                    MatchedStrings = 0,
                    Details = "No YARA rules matched"
                };
            }
            catch (OperationCanceledException)
            {
                _logger.Warning($"YARA scan timed out ({timeoutMs}ms)");
                return new YARAScanResult
                {
                    IsMatch = false,
                    RuleName = null,
                    MatchedStrings = 0,
                    Details = $"Scan timeout ({timeoutMs}ms)"
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"YARA scan error: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// YARAルールを再読み込み
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

    /// <summary>
    /// YARAスキャン結果
    /// </summary>
    public class YARAScanResult
    {
        public bool IsMatch { get; set; }
        public string? RuleName { get; set; }
        public int MatchedStrings { get; set; }
        public string? Details { get; set; }
    }
}
