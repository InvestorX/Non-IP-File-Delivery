using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dnYara;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// YARAルールベースのマルウェアスキャナー (Full Implementation with dnYara 2.1.0)
    /// </summary>
    public class YARAScanner : IDisposable
    {
        private readonly ILoggingService _logger;
        private readonly string _rulesPath;
        private CompiledRules? _compiledRules;
        private bool _disposed;
        private readonly object _lockObject = new object();

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="logger">ロギングサービス</param>
        /// <param name="rulesPath">YARAルールファイルのパス（.yarファイル）</param>
        public YARAScanner(ILoggingService logger, string rulesPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _rulesPath = rulesPath ?? throw new ArgumentNullException(nameof(rulesPath));

            try
            {
                LoadRules();
                _logger.Info($"YARAScanner: Successfully loaded rules from {_rulesPath}");
            }
            catch (Exception ex)
            {
                _logger.Error($"YARAScanner: Failed to load rules from {_rulesPath}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// YARAルールをロード
        /// </summary>
        private void LoadRules()
        {
            if (!File.Exists(_rulesPath))
            {
                throw new FileNotFoundException($"YARA rules file not found: {_rulesPath}");
            }

            lock (_lockObject)
            {
                // 既存のルールを破棄
                _compiledRules?.Dispose();

                // 新しいルールをコンパイル
                using var compiler = new Compiler();
                compiler.AddRuleFile(_rulesPath);
                _compiledRules = compiler.Compile();

                _logger.Info($"YARAScanner: Compiled {_compiledRules.RuleCount} rules, {_compiledRules.StringsCount} strings");
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
            if (data == null || data.Length == 0)
            {
                _logger.Warning("Attempted to scan empty data");
                return new YARAScanResult { IsMatch = false };
            }

            if (_compiledRules == null)
            {
                _logger.Error("YARAScanner: Rules not loaded");
                return new YARAScanResult 
                { 
                    IsMatch = false, 
                    ErrorMessage = "YARA rules not loaded"
                };
            }

            return await Task.Run(() =>
            {
                try
                {
                    var scanner = new CustomScanner(_compiledRules, timeout: timeoutMs);
                    var results = scanner.ScanMemory(ref data, null);

                    if (results.Any())
                    {
                        var firstMatch = results.First();
                        var matchedStrings = firstMatch.Matches.Sum(m => m.Value.Count);

                        _logger.Warning($"YARAScanner: Threat detected - Rule: {firstMatch.MatchingRule.Identifier}, Matches: {matchedStrings}");

                        return new YARAScanResult
                        {
                            IsMatch = true,
                            RuleName = firstMatch.MatchingRule.Identifier,
                            MatchedStrings = matchedStrings,
                            Details = $"Matched rule: {firstMatch.MatchingRule.Identifier}, Tags: {string.Join(", ", firstMatch.MatchingRule.Tags)}",
                            ScanTime = DateTime.UtcNow
                        };
                    }

                    _logger.Debug($"YARAScanner: Scanned {data.Length} bytes - no threats detected");
                    return new YARAScanResult
                    {
                        IsMatch = false,
                        Details = "No threats detected",
                        ScanTime = DateTime.UtcNow
                    };
                }
                catch (Exception ex)
                {
                    _logger.Error($"YARAScanner: Scan error: {ex.Message}");
                    return new YARAScanResult
                    {
                        IsMatch = false,
                        ErrorMessage = ex.Message,
                        ScanTime = DateTime.UtcNow
                    };
                }
            });
        }

        /// <summary>
        /// YARAルールをリロード
        /// </summary>
        public void ReloadRules()
        {
            try
            {
                LoadRules();
                _logger.Info("YARAScanner: Rules reloaded successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"YARAScanner: Failed to reload rules: {ex.Message}");
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            lock (_lockObject)
            {
                _compiledRules?.Dispose();
                _compiledRules = null;
            }

            _disposed = true;
            _logger.Info("YARAScanner disposed");
        }
    }
}
