// SecurityService.cs（既存ファイルの修正版）

using System;
using System.IO;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;

namespace NonIPFileDelivery.Services
{
    public class SecurityService : ISecurityService
    {
        private readonly ILoggingService _logger;
        private readonly SecurityConfig _config;
        private YARAScanner? _yaraScanner;      // 🆕 追加
        private ClamAVScanner? _clamAvScanner;  // 🆕 追加

        public SecurityService(ILoggingService logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = new SecurityConfig(); // デフォルト設定
        }

        /// <summary>
        /// セキュリティモジュール初期化
        /// </summary>
        public async Task<bool> InitializeSecurity(SecurityConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            try
            {
                _logger.Info("Initializing security services...");

                // 隔離ディレクトリ作成
                if (!Directory.Exists(_config.QuarantinePath))
                {
                    Directory.CreateDirectory(_config.QuarantinePath);
                    _logger.Info($"Quarantine directory created: {_config.QuarantinePath}");
                }

                // 🆕 YARAスキャナー初期化
                if (_config.EnableVirusScan)
                {
                    var yaraRulesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yara_rules", "malware.yar");
                    if (File.Exists(yaraRulesPath))
                    {
                        _yaraScanner = new YARAScanner(_logger, yaraRulesPath);
                        _logger.Info("YARA scanner initialized");
                    }
                    else
                    {
                        _logger.Warning($"YARA rules file not found: {yaraRulesPath}");
                    }

                    // 🆕 ClamAVスキャナー初期化（オプショナル）
                    _clamAvScanner = new ClamAVScanner(_logger);
                    var clamAvConnected = await _clamAvScanner.TestConnectionAsync();
                    if (clamAvConnected)
                    {
                        _logger.Info("ClamAV scanner initialized");
                    }
                    else
                    {
                        _logger.Warning("ClamAV connection failed (continuing without ClamAV)");
                        _clamAvScanner = null;
                    }
                }

                _logger.Info("Security services initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize security services: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// データをスキャン（YARA + ClamAV統合版）
        /// 🔴 モック実装（85-107行目）を削除し、実装完成
        /// </summary>
        public async Task<ScanResult> ScanData(byte[] data, string fileName)
        {
            if (data == null || data.Length == 0)
            {
                return new ScanResult
                {
                    IsClean = true,
                    Details = "Empty data (skipped scan)"
                };
            }

            try
            {
                _logger.Info($"Scanning data: {fileName} ({data.Length} bytes)");

                // 🆕 YARAスキャン実行
                if (_yaraScanner != null)
                {
                    var yaraResult = await _yaraScanner.ScanAsync(data, _config.ScanTimeout);
                    if (yaraResult.IsMatch)
                    {
                        _logger.Warning($"YARA rule matched: {yaraResult.RuleName}");
                        return new ScanResult
                        {
                            IsClean = false,
                            ThreatName = yaraResult.RuleName,
                            Details = $"YARA rule matched: {yaraResult.RuleName} ({yaraResult.MatchedStrings} strings)"
                        };
                    }
                }

                // 🆕 ClamAVスキャン実行
                if (_clamAvScanner != null)
                {
                    var clamResult = await _clamAvScanner.ScanAsync(data, _config.ScanTimeout);
                    if (!clamResult.IsClean)
                    {
                        _logger.Warning($"ClamAV detected virus: {clamResult.VirusName}");
                        return new ScanResult
                        {
                            IsClean = false,
                            ThreatName = clamResult.VirusName,
                            Details = $"ClamAV detected: {clamResult.VirusName}"
                        };
                    }
                }

                _logger.Info($"Scan completed: {fileName} is clean");
                return new ScanResult
                {
                    IsClean = true,
                    Details = "No threats detected"
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Scan error: {ex.Message}", ex);
                return new ScanResult
                {
                    IsClean = false,
                    ThreatName = "Scan Error",
                    Details = ex.Message
                };
            }
        }

        /// <summary>
        /// ファイルを隔離
        /// </summary>
        public async Task<bool> QuarantineFile(string filePath, string reason)
        {
            if (!File.Exists(filePath))
            {
                _logger.Warning($"File not found for quarantine: {filePath}");
                return false;
            }

            try
            {
                var fileName = Path.GetFileName(filePath);
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var quarantineFileName = $"{timestamp}_{fileName}";
                var quarantinePath = Path.Combine(_config.QuarantinePath, quarantineFileName);

                File.Move(filePath, quarantinePath);
                _logger.Warning($"File quarantined: {quarantinePath} (Reason: {reason})");

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to quarantine file: {ex.Message}", ex);
                return false;
            }
        }
    }
}
