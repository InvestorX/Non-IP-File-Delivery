// CustomSignatureScanner.cs
// カスタム署名ベースのマルウェアスキャナー
// クローズド環境向けの独立したセキュリティエンジン

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// カスタム署名ベースのマルウェアスキャナー
    /// 外部依存なしで動作する独立したセキュリティエンジン
    /// </summary>
    public class CustomSignatureScanner : IDisposable
    {
        private readonly ILoggingService _logger;
        private readonly string _signaturePath;
        private List<MalwareSignature> _signatures;
        private readonly object _lockObject = new object();
        private int _totalScans = 0;
        private int _totalThreats = 0;

        /// <summary>
        /// ロード済み署名数
        /// </summary>
        public int LoadedSignatureCount => _signatures?.Count(s => s.Enabled) ?? 0;

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
        /// <param name="signaturePath">署名ファイルパス（JSON形式）</param>
        public CustomSignatureScanner(ILoggingService logger, string signaturePath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _signaturePath = signaturePath ?? throw new ArgumentNullException(nameof(signaturePath));
            _signatures = new List<MalwareSignature>();

            LoadSignatures();
        }

        /// <summary>
        /// 署名データベースをロード
        /// </summary>
        public void LoadSignatures()
        {
            try
            {
                if (!File.Exists(_signaturePath))
                {
                    _logger.Warning($"Signature file not found: {_signaturePath}");
                    _logger.Info("Creating empty signature database...");
                    _signatures = new List<MalwareSignature>();
                    return;
                }

                var json = File.ReadAllText(_signaturePath, Encoding.UTF8);
                var signatures = JsonSerializer.Deserialize<List<MalwareSignature>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                });

                if (signatures == null || signatures.Count == 0)
                {
                    _logger.Warning("No signatures loaded from file");
                    _signatures = new List<MalwareSignature>();
                    return;
                }

                // 16進数パターンをバイト配列に変換
                foreach (var sig in signatures)
                {
                    if (!string.IsNullOrEmpty(sig.HexPattern))
                    {
                        sig.Pattern = HexStringToByteArray(sig.HexPattern);
                    }
                }

                lock (_lockObject)
                {
                    _signatures = signatures;
                }

                var enabledCount = _signatures.Count(s => s.Enabled);
                _logger.Info($"Loaded {enabledCount} enabled signatures from {_signaturePath} (Total: {signatures.Count})");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load signatures: {ex.Message}", ex);
                _signatures = new List<MalwareSignature>();
            }
        }

        /// <summary>
        /// 署名データベースを再読み込み
        /// </summary>
        public void ReloadSignatures()
        {
            _logger.Info("Reloading signature database...");
            LoadSignatures();
        }

        /// <summary>
        /// データをスキャン（非同期）
        /// </summary>
        /// <param name="data">スキャン対象データ</param>
        /// <param name="fileName">ファイル名（ログ用）</param>
        /// <param name="timeoutMs">タイムアウト（ミリ秒）</param>
        /// <returns>スキャン結果</returns>
        public async Task<CustomScanResult> ScanAsync(byte[] data, string fileName = "unknown", int timeoutMs = 30000)
        {
            return await Task.Run(() => Scan(data, fileName, timeoutMs));
        }

        /// <summary>
        /// データをスキャン（同期）
        /// </summary>
        /// <param name="data">スキャン対象データ</param>
        /// <param name="fileName">ファイル名（ログ用）</param>
        /// <param name="timeoutMs">タイムアウト（ミリ秒）</param>
        /// <returns>スキャン結果</returns>
        public CustomScanResult Scan(byte[] data, string fileName = "unknown", int timeoutMs = 30000)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                System.Threading.Interlocked.Increment(ref _totalScans);

                if (data == null || data.Length == 0)
                {
                    return new CustomScanResult
                    {
                        IsClean = true,
                        Details = "Empty data (skipped scan)",
                        ScanDuration = stopwatch.Elapsed
                    };
                }

                _logger.Debug($"Scanning {fileName} ({data.Length} bytes) with {LoadedSignatureCount} custom signatures...");

                List<MalwareSignature> signatures;
                lock (_lockObject)
                {
                    signatures = _signatures.Where(s => s.Enabled).ToList();
                }

                if (signatures.Count == 0)
                {
                    _logger.Warning("No enabled signatures available for scanning");
                    return new CustomScanResult
                    {
                        IsClean = true,
                        Details = "No signatures loaded",
                        ScanDuration = stopwatch.Elapsed
                    };
                }

                // 各署名でスキャン
                foreach (var sig in signatures)
                {
                    // タイムアウトチェック
                    if (stopwatch.ElapsedMilliseconds > timeoutMs)
                    {
                        _logger.Warning($"Custom signature scan timeout ({timeoutMs}ms) for {fileName}");
                        return new CustomScanResult
                        {
                            IsClean = false,
                            ErrorMessage = $"Scan timeout ({timeoutMs}ms)",
                            ScanDuration = stopwatch.Elapsed
                        };
                    }

                    if (sig.Pattern == null || sig.Pattern.Length == 0)
                    {
                        continue;
                    }

                    // パターンマッチング実行
                    int matchOffset = FindPattern(data, sig.Pattern, sig.Offset, sig.MaxSearchSize);

                    if (matchOffset >= 0)
                    {
                        System.Threading.Interlocked.Increment(ref _totalThreats);
                        stopwatch.Stop();

                        _logger.Warning($"Custom signature matched: {sig.Name} (ID: {sig.Id}) at offset {matchOffset} in {fileName}");

                        return new CustomScanResult
                        {
                            IsClean = false,
                            ThreatName = sig.Name,
                            Severity = sig.Severity,
                            MatchOffset = matchOffset,
                            SignatureId = sig.Id,
                            Details = $"Custom signature matched: {sig.Name} (ID: {sig.Id}) at offset {matchOffset}. {sig.Description}",
                            ScanDuration = stopwatch.Elapsed
                        };
                    }
                }

                stopwatch.Stop();
                _logger.Debug($"Custom signature scan completed: {fileName} is clean ({stopwatch.ElapsedMilliseconds}ms)");

                return new CustomScanResult
                {
                    IsClean = true,
                    Details = $"No threats detected (scanned with {signatures.Count} signatures)",
                    ScanDuration = stopwatch.Elapsed
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.Error($"Custom signature scan error: {ex.Message}", ex);
                return new CustomScanResult
                {
                    IsClean = false,
                    ErrorMessage = ex.Message,
                    ScanDuration = stopwatch.Elapsed
                };
            }
        }

        /// <summary>
        /// バイナリデータ内でパターンを検索
        /// </summary>
        /// <param name="data">検索対象データ</param>
        /// <param name="pattern">検索パターン</param>
        /// <param name="startOffset">検索開始オフセット（-1=先頭から）</param>
        /// <param name="maxSearchSize">最大検索サイズ（0=制限なし）</param>
        /// <returns>マッチしたオフセット位置（見つからない場合は-1）</returns>
        private int FindPattern(byte[] data, byte[] pattern, int startOffset, int maxSearchSize)
        {
            if (data == null || pattern == null || data.Length == 0 || pattern.Length == 0)
            {
                return -1;
            }

            if (pattern.Length > data.Length)
            {
                return -1;
            }

            int searchStart = startOffset < 0 ? 0 : startOffset;
            int searchEnd = data.Length - pattern.Length + 1;

            if (maxSearchSize > 0 && searchStart + maxSearchSize < searchEnd)
            {
                searchEnd = searchStart + maxSearchSize;
            }

            if (searchStart >= data.Length || searchStart >= searchEnd)
            {
                return -1;
            }

            // Boyer-Moore-Horspool アルゴリズムの簡易実装
            return BoyerMooreHorspoolSearch(data, pattern, searchStart, searchEnd);
        }

        /// <summary>
        /// Boyer-Moore-Horspool パターンマッチングアルゴリズム
        /// 高速なバイナリ検索アルゴリズム
        /// </summary>
        private int BoyerMooreHorspoolSearch(byte[] data, byte[] pattern, int start, int end)
        {
            int patternLength = pattern.Length;

            // 簡易実装: 線形検索 (確実に動作する)
            for (int i = start; i < end; i++)
            {
                bool match = true;
                for (int j = 0; j < patternLength; j++)
                {
                    if (i + j >= data.Length || data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 16進数文字列をバイト配列に変換
        /// </summary>
        /// <param name="hex">16進数文字列（例: "4D5A9000"）</param>
        /// <returns>バイト配列</returns>
        private byte[] HexStringToByteArray(string hex)
        {
            // スペース、ハイフン、改行などを削除
            hex = hex.Replace(" ", "").Replace("-", "").Replace("\n", "").Replace("\r", "").Replace("\t", "");

            if (hex.Length % 2 != 0)
            {
                throw new ArgumentException($"Invalid hex string length: {hex.Length}");
            }

            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < hex.Length; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }

            return bytes;
        }

        /// <summary>
        /// 新しい署名を追加
        /// </summary>
        public void AddSignature(MalwareSignature signature)
        {
            if (signature == null)
            {
                throw new ArgumentNullException(nameof(signature));
            }

            if (!string.IsNullOrEmpty(signature.HexPattern) && signature.Pattern == null)
            {
                signature.Pattern = HexStringToByteArray(signature.HexPattern);
            }

            lock (_lockObject)
            {
                _signatures.Add(signature);
            }

            _logger.Info($"Added new signature: {signature.Id} - {signature.Name}");
        }

        /// <summary>
        /// 署名を削除
        /// </summary>
        public bool RemoveSignature(string signatureId)
        {
            lock (_lockObject)
            {
                int removedCount = _signatures.RemoveAll(s => s.Id == signatureId);
                if (removedCount > 0)
                {
                    _logger.Info($"Removed signature: {signatureId}");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 署名を有効/無効化
        /// </summary>
        public bool SetSignatureEnabled(string signatureId, bool enabled)
        {
            lock (_lockObject)
            {
                var signature = _signatures.FirstOrDefault(s => s.Id == signatureId);
                if (signature != null)
                {
                    signature.Enabled = enabled;
                    _logger.Info($"Signature {signatureId} {(enabled ? "enabled" : "disabled")}");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// すべての署名を取得
        /// </summary>
        public List<MalwareSignature> GetAllSignatures()
        {
            lock (_lockObject)
            {
                return new List<MalwareSignature>(_signatures);
            }
        }

        /// <summary>
        /// 統計情報を取得
        /// </summary>
        public string GetStats()
        {
            return $"CustomSignatureScanner Stats: " +
                   $"Loaded={LoadedSignatureCount}, " +
                   $"Total={_signatures.Count}, " +
                   $"Scans={_totalScans}, " +
                   $"Threats={_totalThreats}";
        }

        /// <summary>
        /// リソースのクリーンアップ
        /// </summary>
        public void Dispose()
        {
            _logger.Info($"CustomSignatureScanner disposed. {GetStats()}");
        }
    }
}
