using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// プロトコル解析基盤クラス
    /// Phase 2実装: Strategy Patternによるマルチプロトコル解析
    /// </summary>
    public class ProtocolAnalyzer
    {
        private readonly ILoggingService _logger;
        private readonly Dictionary<int, IProtocolAnalyzer> _portAnalyzers;
        private readonly Dictionary<ProtocolType, IProtocolAnalyzer> _protocolAnalyzers;

        public ProtocolAnalyzer(ILoggingService logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _portAnalyzers = new Dictionary<int, IProtocolAnalyzer>();
            _protocolAnalyzers = new Dictionary<ProtocolType, IProtocolAnalyzer>();

            InitializeAnalyzers();
        }

        /// <summary>
        /// 解析器の初期化
        /// </summary>
        private void InitializeAnalyzers()
        {
            // FTP解析器登録
            var ftpAnalyzer = new FTPAnalyzer(_logger);
            RegisterAnalyzer(21, ftpAnalyzer);  // FTP制御ポート
            RegisterAnalyzer(20, ftpAnalyzer);  // FTPデータポート
            _protocolAnalyzers[ProtocolType.FTP] = ftpAnalyzer;

            // PostgreSQL解析器登録
            var pgAnalyzer = new PostgreSQLAnalyzer(_logger);
            RegisterAnalyzer(5432, pgAnalyzer);  // PostgreSQLデフォルトポート
            _protocolAnalyzers[ProtocolType.PostgreSQL] = pgAnalyzer;

            _logger.Info($"ProtocolAnalyzer initialized with {_portAnalyzers.Count} port mappings");
        }

        /// <summary>
        /// 解析器を登録
        /// </summary>
        /// <param name="port">ポート番号</param>
        /// <param name="analyzer">解析器</param>
        public void RegisterAnalyzer(int port, IProtocolAnalyzer analyzer)
        {
            _portAnalyzers[port] = analyzer;
            _logger.Debug($"Registered analyzer for port {port}: {analyzer.GetType().Name}");
        }

        /// <summary>
        /// パケットデータを解析
        /// </summary>
        /// <param name="packetData">生パケットデータ</param>
        /// <returns>解析結果</returns>
        public async Task<ProtocolAnalysisResult> AnalyzeAsync(byte[] packetData)
        {
            if (packetData == null || packetData.Length < 20)
            {
                _logger.Warning("Invalid packet data: too short");
                return new ProtocolAnalysisResult
                {
                    Protocol = ProtocolType.Unknown,
                    IsValid = false,
                    ErrorMessage = "Packet too short",
                    DataSize = packetData?.Length ?? 0
                };
            }

            try
            {
                // TCPヘッダー解析（簡易版）
                var destinationPort = ExtractDestinationPort(packetData);
                var sourcePort = ExtractSourcePort(packetData);

                _logger.Debug($"Analyzing packet: SrcPort={sourcePort}, DstPort={destinationPort}, Size={packetData.Length}");

                // ポート番号ベースで解析器を選択
                IProtocolAnalyzer? analyzer = null;

                if (_portAnalyzers.TryGetValue(destinationPort, out var dstAnalyzer))
                {
                    analyzer = dstAnalyzer;
                    _logger.Debug($"Selected analyzer by destination port {destinationPort}");
                }
                else if (_portAnalyzers.TryGetValue(sourcePort, out var srcAnalyzer))
                {
                    analyzer = srcAnalyzer;
                    _logger.Debug($"Selected analyzer by source port {sourcePort}");
                }

                if (analyzer != null)
                {
                    var result = await analyzer.AnalyzeAsync(packetData);
                    result.DataSize = packetData.Length;
                    return result;
                }

                // 未対応プロトコル
                _logger.Debug($"No analyzer found for ports {sourcePort}/{destinationPort}");
                return new ProtocolAnalysisResult
                {
                    Protocol = ProtocolType.Unknown,
                    IsValid = false,
                    ErrorMessage = $"Unsupported protocol (ports: {sourcePort}/{destinationPort})",
                    DataSize = packetData.Length
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Protocol analysis error: {ex.Message}", ex);
                return new ProtocolAnalysisResult
                {
                    Protocol = ProtocolType.Unknown,
                    IsValid = false,
                    ErrorMessage = ex.Message,
                    DataSize = packetData.Length
                };
            }
        }

        /// <summary>
        /// プロトコルタイプを指定して解析
        /// </summary>
        /// <param name="packetData">パケットデータ</param>
        /// <param name="protocol">プロトコルタイプ</param>
        /// <returns>解析結果</returns>
        public async Task<ProtocolAnalysisResult> AnalyzeByProtocolAsync(byte[] packetData, ProtocolType protocol)
        {
            if (_protocolAnalyzers.TryGetValue(protocol, out var analyzer))
            {
                return await analyzer.AnalyzeAsync(packetData);
            }

            _logger.Warning($"No analyzer registered for protocol: {protocol}");
            return new ProtocolAnalysisResult
            {
                Protocol = protocol,
                IsValid = false,
                ErrorMessage = $"No analyzer for {protocol}",
                DataSize = packetData.Length
            };
        }

        /// <summary>
        /// 送信元ポートを抽出（TCP/IPヘッダー解析）
        /// </summary>
        private int ExtractSourcePort(byte[] packetData)
        {
            // Ethernet(14) + IP(20) = 34バイト目から2バイトがTCP送信元ポート
            if (packetData.Length < 36)
                return 0;

            return (packetData[34] << 8) | packetData[35];
        }

        /// <summary>
        /// 宛先ポートを抽出（TCP/IPヘッダー解析）
        /// </summary>
        private int ExtractDestinationPort(byte[] packetData)
        {
            // Ethernet(14) + IP(20) + TCP送信元ポート(2) = 36バイト目から2バイトがTCP宛先ポート
            if (packetData.Length < 38)
                return 0;

            return (packetData[36] << 8) | packetData[37];
        }

        /// <summary>
        /// 登録済みプロトコルの一覧を取得
        /// </summary>
        public IEnumerable<ProtocolType> GetSupportedProtocols()
        {
            return _protocolAnalyzers.Keys;
        }

        /// <summary>
        /// 統計情報取得
        /// </summary>
        public Dictionary<string, object> GetStatistics()
        {
            return new Dictionary<string, object>
            {
                { "RegisteredPorts", _portAnalyzers.Count },
                { "RegisteredProtocols", _protocolAnalyzers.Count },
                { "SupportedProtocols", string.Join(", ", GetSupportedProtocols()) }
            };
        }

        /// <summary>
        /// パケットデータからプロトコルタイプを検出（同期版）
        /// </summary>
        public ProtocolType DetectProtocol(byte[] packetData)
        {
            if (packetData == null || packetData.Length < 20)
            {
                return ProtocolType.Unknown;
            }

            try
            {
                var destinationPort = ExtractDestinationPort(packetData);
                var sourcePort = ExtractSourcePort(packetData);

                // ポート番号ベースでプロトコル判定
                if (_portAnalyzers.TryGetValue(destinationPort, out var dstAnalyzer))
                {
                    // 解析器のタイプからプロトコルタイプを推定
                    foreach (var kvp in _protocolAnalyzers)
                    {
                        if (kvp.Value == dstAnalyzer)
                            return kvp.Key;
                    }
                }
                else if (_portAnalyzers.TryGetValue(sourcePort, out var srcAnalyzer))
                {
                    foreach (var kvp in _protocolAnalyzers)
                    {
                        if (kvp.Value == srcAnalyzer)
                            return kvp.Key;
                    }
                }

                return ProtocolType.Unknown;
            }
            catch
            {
                return ProtocolType.Unknown;
            }
        }

        /// <summary>
        /// パケットデータを解析（同期版）
        /// </summary>
        public ProtocolAnalysisResult Analyze(byte[] packetData, ProtocolType protocolType)
        {
            if (packetData == null || packetData.Length < 20)
            {
                return new ProtocolAnalysisResult
                {
                    Protocol = protocolType,
                    IsValid = false,
                    ErrorMessage = "Packet too short",
                    DataSize = packetData?.Length ?? 0
                };
            }

            try
            {
                if (_protocolAnalyzers.TryGetValue(protocolType, out var analyzer))
                {
                    // 非同期メソッドを同期的に実行（パフォーマンス最適化のため）
                    var result = analyzer.AnalyzeAsync(packetData).GetAwaiter().GetResult();
                    result.DataSize = packetData.Length;
                    return result;
                }

                return new ProtocolAnalysisResult
                {
                    Protocol = protocolType,
                    IsValid = false,
                    ErrorMessage = $"No analyzer for {protocolType}",
                    DataSize = packetData.Length
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Protocol analysis error: {ex.Message}", ex);
                return new ProtocolAnalysisResult
                {
                    Protocol = protocolType,
                    IsValid = false,
                    ErrorMessage = ex.Message,
                    DataSize = packetData.Length
                };
            }
        }
    }
}