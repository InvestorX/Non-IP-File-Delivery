using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// TPL Dataflowによるパケット処理パイプライン
    /// Phase 2でProtocolAnalyzer統合を追加
    /// </summary>
    public class PacketProcessingPipeline : IDisposable
    {
        private readonly ILoggingService _logger;
        private readonly ISecurityService _securityService;
        private readonly IFrameService _frameService;
        private readonly IProtocolAnalyzer _protocolAnalyzer;
        private readonly SQLInjectionDetector _sqlInjectionDetector;

        private TransformBlock<byte[], ProcessedPacket>? _captureBlock;
        private TransformBlock<ProcessedPacket, ProcessedPacket>? _protocolAnalysisBlock;
        private TransformBlock<ProcessedPacket, ProcessedPacket>? _securityBlock;
        private ActionBlock<ProcessedPacket>? _forwardBlock;

        private long _totalPacketsProcessed;
        private long _totalPacketsDropped;
        private long _totalSecurityBlocks;
        private long _totalBytesProcessed;
        private readonly Stopwatch _uptime;

        // Phase 2: プロトコル別統計
        private long _ftpPackets;
        private long _postgresqlPackets;
        private long _sqlInjectionDetections;
        private long _otherProtocolPackets;

        private bool _disposed;

        public PacketProcessingPipeline(
            ILoggingService logger,
            ISecurityService securityService,
            IFrameService frameService,
            IProtocolAnalyzer protocolAnalyzer,
            SQLInjectionDetector sqlInjectionDetector)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _securityService = securityService ?? throw new ArgumentNullException(nameof(securityService));
            _frameService = frameService ?? throw new ArgumentNullException(nameof(frameService));
            _protocolAnalyzer = protocolAnalyzer ?? throw new ArgumentNullException(nameof(protocolAnalyzer));
            _sqlInjectionDetector = sqlInjectionDetector ?? throw new ArgumentNullException(nameof(sqlInjectionDetector));

            _uptime = Stopwatch.StartNew();
        }

        /// <summary>
        /// パイプラインを初期化して開始
        /// </summary>
        public void Start(int maxDegreeOfParallelism = 4, int boundedCapacity = 1000)
        {
            _logger.Info($"PacketProcessingPipeline starting with MaxDOP={maxDegreeOfParallelism}, Capacity={boundedCapacity}");

            var dataflowOptions = new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                BoundedCapacity = boundedCapacity
            };

            // ステージ1: パケットキャプチャ・デシリアライズ
            _captureBlock = new TransformBlock<byte[], ProcessedPacket>(
                async rawData =>
                {
                    try
                    {
                        var frame = _frameService.DeserializeFrame(rawData);
                        if (frame == null)
                        {
                            Interlocked.Increment(ref _totalPacketsDropped);
                            _logger.Warning("Failed to deserialize frame");
                            return new ProcessedPacket { IsValid = false };
                        }

                        Interlocked.Increment(ref _totalPacketsProcessed);
                        Interlocked.Add(ref _totalBytesProcessed, rawData.Length);

                        return new ProcessedPacket
                        {
                            IsValid = true,
                            Frame = frame,
                            RawData = rawData,
                            ReceivedAt = DateTime.UtcNow
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Error in capture block: {ex.Message}", ex);
                        Interlocked.Increment(ref _totalPacketsDropped);
                        return new ProcessedPacket { IsValid = false };
                    }
                },
                dataflowOptions);

            // ステージ2: プロトコル解析（Phase 2で追加）
            _protocolAnalysisBlock = new TransformBlock<ProcessedPacket, ProcessedPacket>(
                async packet =>
                {
                    if (!packet.IsValid)
                        return packet;

                    try
                    {
                        // プロトコル判定
                        var protocolType = _protocolAnalyzer.DetectProtocol(packet.Frame.Payload);
                        packet.ProtocolType = protocolType;

                        // プロトコル解析実行
                        var analysisResult = _protocolAnalyzer.Analyze(packet.Frame.Payload, protocolType);
                        packet.AnalysisResult = analysisResult;

                        // 統計更新
                        switch (protocolType)
                        {
                            case ProtocolType.FTP:
                                Interlocked.Increment(ref _ftpPackets);
                                _logger.Debug($"FTP packet analyzed: Command={analysisResult.Command}");
                                break;

                            case ProtocolType.PostgreSQL:
                                Interlocked.Increment(ref _postgresqlPackets);
                                _logger.Debug($"PostgreSQL packet analyzed: SQL={analysisResult.ExtractedData}");

                                // SQLインジェクション検出
                                if (!string.IsNullOrEmpty(analysisResult.ExtractedData))
                                {
                                    var injectionResult = _sqlInjectionDetector.Detect(analysisResult.ExtractedData);
                                    if (injectionResult.IsInjection)
                                    {
                                        Interlocked.Increment(ref _sqlInjectionDetections);
                                        packet.IsSqlInjection = true;
                                        packet.SqlInjectionThreat = injectionResult;

                                        _logger.Warning($"SQL Injection detected: Level={injectionResult.ThreatLevel}, Pattern={injectionResult.MatchedPattern}");
                                    }
                                }
                                break;

                            case ProtocolType.Unknown:
                            default:
                                Interlocked.Increment(ref _otherProtocolPackets);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Error in protocol analysis block: {ex.Message}", ex);
                    }

                    return packet;
                },
                dataflowOptions);

            // ステージ3: セキュリティ検閲
            _securityBlock = new TransformBlock<ProcessedPacket, ProcessedPacket>(
                async packet =>
                {
                    if (!packet.IsValid)
                        return packet;

                    try
                    {
                        // SQLインジェクション検出時は即座にブロック
                        if (packet.IsSqlInjection && packet.SqlInjectionThreat != null)
                        {
                            if (packet.SqlInjectionThreat.ThreatLevel == ThreatLevel.Critical ||
                                packet.SqlInjectionThreat.ThreatLevel == ThreatLevel.High)
                            {
                                packet.IsBlocked = true;
                                packet.BlockReason = $"SQL Injection detected: {packet.SqlInjectionThreat.Description}";
                                Interlocked.Increment(ref _totalSecurityBlocks);

                                _logger.Warning($"Packet blocked: {packet.BlockReason}");
                                return packet;
                            }
                        }

                        // ペイロードのセキュリティスキャン
                        var scanResult = await _securityService.ScanData(
                            packet.Frame.Payload,
                            $"frame_{packet.Frame.Header.SequenceNumber}");

                        packet.ScanResult = scanResult;

                        if (!scanResult.IsClean)
                        {
                            packet.IsBlocked = true;
                            packet.BlockReason = $"Malware detected: {scanResult.ThreatName}";
                            Interlocked.Increment(ref _totalSecurityBlocks);

                            _logger.Warning($"Packet blocked: {packet.BlockReason}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Error in security block: {ex.Message}", ex);
                        packet.IsBlocked = true;
                        packet.BlockReason = "Security scan error";
                    }

                    return packet;
                },
                dataflowOptions);

            // ステージ4: 転送処理
            _forwardBlock = new ActionBlock<ProcessedPacket>(
                async packet =>
                {
                    if (!packet.IsValid || packet.IsBlocked)
                    {
                        _logger.Debug($"Packet dropped: Valid={packet.IsValid}, Blocked={packet.IsBlocked}, Reason={packet.BlockReason}");
                        return;
                    }

                    try
                    {
                        // 実際の転送処理はここに実装
                        // 例: NetworkService.SendFrame(packet.RawData);
                        _logger.Debug($"Packet forwarded: Seq={packet.Frame.Header.SequenceNumber}, Protocol={packet.ProtocolType}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Error forwarding packet: {ex.Message}", ex);
                    }
                },
                dataflowOptions);

            // パイプライン接続
            _captureBlock.LinkTo(_protocolAnalysisBlock, new DataflowLinkOptions { PropagateCompletion = true });
            _protocolAnalysisBlock.LinkTo(_securityBlock, new DataflowLinkOptions { PropagateCompletion = true });
            _securityBlock.LinkTo(_forwardBlock, new DataflowLinkOptions { PropagateCompletion = true });

            _logger.Info("PacketProcessingPipeline started successfully");
        }

        /// <summary>
        /// パケットをパイプラインに投入
        /// </summary>
        public async Task<bool> ProcessPacketAsync(byte[] rawData)
        {
            if (_captureBlock == null)
                throw new InvalidOperationException("Pipeline not started");

            return await _captureBlock.SendAsync(rawData);
        }

        /// <summary>
        /// 統計情報を取得
        /// </summary>
        public PipelineStatistics GetStatistics()
        {
            var uptime = _uptime.Elapsed;
            var totalPackets = _totalPacketsProcessed;
            var dropRate = totalPackets > 0 ? (_totalPacketsDropped / (double)totalPackets) * 100 : 0;
            var throughputMbps = uptime.TotalSeconds > 0 ? (_totalBytesProcessed * 8 / uptime.TotalSeconds / 1_000_000) : 0;
            var packetsPerSecond = uptime.TotalSeconds > 0 ? totalPackets / uptime.TotalSeconds : 0;

            return new PipelineStatistics
            {
                TotalPacketsProcessed = totalPackets,
                TotalPacketsDropped = _totalPacketsDropped,
                TotalSecurityBlocks = _totalSecurityBlocks,
                DropRate = dropRate,
                ThroughputMbps = throughputMbps,
                PacketsPerSecond = packetsPerSecond,
                Uptime = uptime,
                // Phase 2: プロトコル別統計
                FtpPackets = _ftpPackets,
                PostgreSqlPackets = _postgresqlPackets,
                SqlInjectionDetections = _sqlInjectionDetections,
                OtherProtocolPackets = _otherProtocolPackets
            };
        }

        /// <summary>
        /// パイプラインを停止
        /// </summary>
        public async Task StopAsync()
        {
            _logger.Info("Stopping PacketProcessingPipeline...");

            if (_captureBlock != null)
            {
                _captureBlock.Complete();
                await _captureBlock.Completion;
            }

            if (_protocolAnalysisBlock != null)
            {
                await _protocolAnalysisBlock.Completion;
            }

            if (_securityBlock != null)
            {
                await _securityBlock.Completion;
            }

            if (_forwardBlock != null)
            {
                await _forwardBlock.Completion;
            }

            _logger.Info("PacketProcessingPipeline stopped");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            StopAsync().GetAwaiter().GetResult();

            _uptime.Stop();
            _disposed = true;
        }
    }

    /// <summary>
    /// 処理済みパケット情報
    /// </summary>
    public class ProcessedPacket
    {
        public bool IsValid { get; set; }
        public NonIPFrame Frame { get; set; } = new();
        public byte[] RawData { get; set; } = Array.Empty<byte>();
        public DateTime ReceivedAt { get; set; }
        public ScanResult? ScanResult { get; set; }
        public bool IsBlocked { get; set; }
        public string? BlockReason { get; set; }

        // Phase 2: プロトコル解析情報
        public ProtocolType ProtocolType { get; set; } = ProtocolType.Unknown;
        public ProtocolAnalysisResult? AnalysisResult { get; set; }
        public bool IsSqlInjection { get; set; }
        public SQLInjectionResult? SqlInjectionThreat { get; set; }
    }

    /// <summary>
    /// パイプライン統計情報
    /// </summary>
    public class PipelineStatistics
    {
        public long TotalPacketsProcessed { get; set; }
        public long TotalPacketsDropped { get; set; }
        public long TotalSecurityBlocks { get; set; }
        public double DropRate { get; set; }
        public double ThroughputMbps { get; set; }
        public double PacketsPerSecond { get; set; }
        public TimeSpan Uptime { get; set; }

        // Phase 2: プロトコル別統計
        public long FtpPackets { get; set; }
        public long PostgreSqlPackets { get; set; }
        public long SqlInjectionDetections { get; set; }
        public long OtherProtocolPackets { get; set; }
    }
}
