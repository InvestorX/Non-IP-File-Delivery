using System;
using System.IO;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// セキュリティサービス実装
    /// Phase 1完全版: YARAスキャン・ClamAV統合完了
    /// </summary>
    public class SecurityService : ISecurityService
    {
        private readonly ILoggingService _logger;
        private readonly SecurityConfig _config;
        private readonly YARAScanner? _yaraScanner;
        private readonly ClamAVScanner? _clamAvScanner;

        public SecurityService(ILoggingService logger, SecurityConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            // YARAスキャナー初期化
            var yaraRulesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yara_rules", "malware.yar");
            if (File.Exists(yaraRulesPath))
            {
                _yaraScanner = new YARAScanner(_logger, yaraRulesPath);
                _logger.Info("YARAScanner initialized");
            }
            else
            {
                _logger.Warning($"YARA rules file not found: {yaraRulesPath}");
            }

            // ClamAVスキャナー初期化（オプショナル）
            if (_config.UseClamAV)
            {
                _clamAvScanner = new ClamAVScanner(_logger);
                _logger.Info("ClamAVScanner initialized");
            }

            // 隔離ディレクトリ作成
            if (!Directory.Exists(_config.QuarantinePath))
            {
                Directory.CreateDirectory(_config.QuarantinePath);
                _logger.Info($"Quarantine directory created: {_config.QuarantinePath}");
            }
        }

        /// <summary>
        /// データをスキャン（Phase 1完全実装）
        /// </summary>
        public async Task<ScanResult> ScanData(byte[] data, string fileName)
        {
            if (data == null || data.Length == 0)
            {
                return new ScanResult
                {
                    IsClean = true,
                    Details = "Empty data"
                };
            }

            var scanStartTime = DateTime.UtcNow;

            try
            {
                // 1. YARAスキャン実行
                if (_yaraScanner != null)
                {
                    var yaraResult = await _yaraScanner.ScanAsync(data, _config.ScanTimeout);

                    if (yaraResult.IsMatch)
                    {
                        _logger.Warning($"YARA rule matched: {yaraResult.RuleName} in file {fileName}");

                        return new ScanResult
                        {
                            IsClean = false,
                            ThreatName = yaraResult.RuleName,
                            Details = $"YARA rule matched: {yaraResult.RuleName}",
                            ScanDuration = DateTime.UtcNow - scanStartTime
                        };
                    }
                }

                // 2. ClamAVスキャン実行（オプショナル）
                if (_clamAvScanner != null)
                {
                    var clamResult = await _clamAvScanner.ScanAsync(data, _config.ScanTimeout);

                    if (!clamResult.IsClean)
                    {
                        _logger.Warning($"ClamAV detected virus: {clamResult.VirusName} in file {fileName}");

                        return new ScanResult
                        {
                            IsClean = false,
                            ThreatName = clamResult.VirusName,
                            Details = $"ClamAV detected: {clamResult.VirusName}",
                            ScanDuration = DateTime.UtcNow - scanStartTime
                        };
                    }
                }

                // 脅威なし
                _logger.Debug($"File {fileName} is clean (scanned {data.Length} bytes)");

                return new ScanResult
                {
                    IsClean = true,
                    ThreatName = null,
                    Details = "No threats detected",
                    ScanDuration = DateTime.UtcNow - scanStartTime
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Error scanning file {fileName}: {ex.Message}", ex);

                return new ScanResult
                {
                    IsClean = false,
                    ThreatName = "ScanError",
                    Details = $"Scan error: {ex.Message}",
                    ScanDuration = DateTime.UtcNow - scanStartTime
                };
            }
        }

        /// <summary>
        /// ファイルを隔離
        /// </summary>
        public async Task<bool> QuarantineFile(string filePath, string reason)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    _logger.Error($"File not found for quarantine: {filePath}");
                    return false;
                }

                var fileName = Path.GetFileName(filePath);
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var quarantineFileName = $"{timestamp}_{fileName}";
                var quarantinePath = Path.Combine(_config.QuarantinePath, quarantineFileName);

                // ファイルを隔離ディレクトリに移動
                File.Copy(filePath, quarantinePath, true);

                // 隔離理由を記録
                var reasonFilePath = quarantinePath + ".reason.txt";
                await File.WriteAllTextAsync(reasonFilePath, $"Quarantined: {DateTime.UtcNow}\nReason: {reason}\nOriginal Path: {filePath}");

                _logger.Warning($"File quarantined: {fileName} -> {quarantinePath} (Reason: {reason})");

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error quarantining file {filePath}: {ex.Message}", ex);
                return false;
            }
        }
    }
}
