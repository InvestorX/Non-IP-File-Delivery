using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Exceptions;

namespace NonIPFileDelivery.Services;

/// <summary>
/// パケット処理パイプライン（TPL Dataflow使用）
/// 受信パケットを複数のステージで並列処理
/// </summary>
public class PacketProcessingPipeline : IDisposable
{
    private readonly ILoggingService _logger;
    private readonly IFrameService _frameService;
    private readonly ISecurityService _securityService;

    // パイプラインブロック
    private TransformBlock<byte[], NonIPFrame?>? _deserializeBlock;
    private TransformBlock<NonIPFrame?, (NonIPFrame? Frame, ScanResult? ScanResult)>? _securityBlock;
    private ActionBlock<(NonIPFrame? Frame, ScanResult? ScanResult)>? _processBlock;

    // パイプライン統計
    private long _totalPacketsProcessed;
    private long _totalPacketsDropped;
    private long _totalSecurityBlocks;

    public PacketProcessingPipeline(
        ILoggingService logger,
        IFrameService frameService,
        ISecurityService securityService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _frameService = frameService ?? throw new ArgumentNullException(nameof(frameService));
        _securityService = securityService ?? throw new ArgumentNullException(nameof(securityService));
    }

    /// <summary>
    /// パイプラインを初期化
    /// </summary>
    public void Initialize()
    {
        var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };

        // ステージ1: フレームのデシリアライズ（並列度: 4）
        _deserializeBlock = new TransformBlock<byte[], NonIPFrame?>(
            rawData =>
            {
                try
                {
                    using (_logger.BeginPerformanceScope("FrameDeserialization"))
                    {
                        return _frameService.DeserializeFrame(rawData);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Frame deserialization failed: {ex.Message}", ex);
                    Interlocked.Increment(ref _totalPacketsDropped);
                    return null;
                }
            },
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = 4,
                BoundedCapacity = 1000, // バックプレッシャー制御
                SingleProducerConstrained = false
            });

        // ステージ2: セキュリティ検閲（並列度: 2、CPU集約的）
        _securityBlock = new TransformBlock<NonIPFrame?, (NonIPFrame? Frame, ScanResult? ScanResult)>(
            async frame =>
            {
                if (frame == null)
                    return (null, null);

                try
                {
                    // ペイロードをスキャン
                    var scanResult = await _securityService.ScanData(
                        frame.Payload,
                        $"frame_{frame.Header.SequenceNumber}");

                    if (!scanResult.IsClean)
                    {
                        Interlocked.Increment(ref _totalSecurityBlocks);
                        _logger.Warning(
                            $"Security threat detected in frame {frame.Header.SequenceNumber}: {scanResult.ThreatName}");
                    }

                    return (frame, scanResult);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Security scan failed for frame {frame.Header.SequenceNumber}", ex);
                    return (frame, new ScanResult { IsClean = false, Details = ex.Message });
                }
            },
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = 2,
                BoundedCapacity = 500,
                SingleProducerConstrained = true // 前段から順次受信
            });

        // ステージ3: フレーム処理（並列度: 1、順序保証）
        _processBlock = new ActionBlock<(NonIPFrame? Frame, ScanResult? ScanResult)>(
            async tuple =>
            {
                var (frame, scanResult) = tuple;

                if (frame == null)
                    return;

                try
                {
                    // セキュリティチェック
                    if (scanResult != null && !scanResult.IsClean)
                    {
                        _logger.Warning($"Dropping malicious frame {frame.Header.SequenceNumber}");
                        await _securityService.QuarantineFile(
                            $"frame_{frame.Header.SequenceNumber}.bin",
                            scanResult.ThreatName ?? "Unknown threat");
                        
                        Interlocked.Increment(ref _totalPacketsDropped);
                        return;
                    }

                    // フレーム処理ロジック（既存処理を呼び出し）
                    await ProcessFrameAsync(frame);

                    Interlocked.Increment(ref _totalPacketsProcessed);

                    _logger.LogWithProperties(
                        LogLevel.Debug,
                        "Frame processed successfully",
                        ("SequenceNumber", frame.Header.SequenceNumber),
                        ("FrameType", frame.Header.Type.ToString()),
                        ("PayloadSize", frame.Payload.Length));
                }
                catch (Exception ex)
                {
                    _logger.Error($"Frame processing failed for {frame.Header.SequenceNumber}", ex);
                    Interlocked.Increment(ref _totalPacketsDropped);
                }
            },
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = 1, // 順序保証のため
                BoundedCapacity = 100,
                SingleProducerConstrained = true
            });

        // パイプライン接続
        _deserializeBlock.LinkTo(_securityBlock, linkOptions, frame => frame != null);
        _deserializeBlock.LinkTo(DataflowBlock.NullTarget<NonIPFrame?>(), frame => frame == null); // nullは破棄

        _securityBlock.LinkTo(_processBlock, linkOptions);

        _logger.Info("Packet processing pipeline initialized successfully");
    }

    /// <summary>
    /// パケットをパイプラインに投入
    /// </summary>
    /// <param name="rawData">生のパケットデータ</param>
    /// <returns>投入が成功したかどうか</returns>
    public async Task<bool> EnqueuePacketAsync(byte[] rawData)
    {
        if (_deserializeBlock == null)
        {
            throw new InvalidOperationException("Pipeline not initialized. Call Initialize() first.");
        }

        return await _deserializeBlock.SendAsync(rawData);
    }

    /// <summary>
    /// パイプラインを完了し、すべての処理を待機
    /// </summary>
    public async Task CompleteAsync()
    {
        _logger.Info("Completing packet processing pipeline...");

        _deserializeBlock?.Complete();

        if (_processBlock != null)
        {
            await _processBlock.Completion;
        }

        _logger.Info($"Pipeline completed. Processed: {_totalPacketsProcessed}, " +
                    $"Dropped: {_totalPacketsDropped}, " +
                    $"Security blocks: {_totalSecurityBlocks}");
    }

    /// <summary>
    /// パイプライン統計を取得
    /// </summary>
    public PipelineStatistics GetStatistics()
    {
        return new PipelineStatistics
        {
            TotalPacketsProcessed = Interlocked.Read(ref _totalPacketsProcessed),
            TotalPacketsDropped = Interlocked.Read(ref _totalPacketsDropped),
            TotalSecurityBlocks = Interlocked.Read(ref _totalSecurityBlocks),
            DeserializeQueueCount = _deserializeBlock?.InputCount ?? 0,
            SecurityQueueCount = _securityBlock?.InputCount ?? 0,
            ProcessQueueCount = _processBlock?.InputCount ?? 0
        };
    }

    /// <summary>
    /// フレームを処理（既存のロジックを呼び出す）
    /// </summary>
    private async Task ProcessFrameAsync(NonIPFrame frame)
    {
        // フレームタイプに応じた処理
        switch (frame.Header.Type)
        {
            case FrameType.Heartbeat:
                _logger.Debug($"Heartbeat received from {MacAddressToString(frame.Header.SourceMac)}");
                // ハートビート処理（冗長化機能で使用）
                break;

            case FrameType.Data:
                _logger.Debug($"Data frame received: {frame.Payload.Length} bytes");
                // データ処理
                break;

            case FrameType.FileTransfer:
                _logger.Info($"File transfer frame received");
                // ファイル転送処理
                break;

            case FrameType.Acknowledgment:
                _logger.Debug($"ACK received for sequence {frame.Header.SequenceNumber}");
                // ACK処理
                break;

            default:
                _logger.Warning($"Unknown frame type: {frame.Header.Type}");
                break;
        }

        // 処理を非同期にシミュレート
        await Task.Delay(1);
    }

    private string MacAddressToString(byte[] mac)
    {
        return string.Join(":", Array.ConvertAll(mac, b => b.ToString("X2")));
    }

    public void Dispose()
    {
        _deserializeBlock?.Complete();
        _securityBlock?.Complete();
        _processBlock?.Complete();
    }
}

/// <summary>
/// パイプライン統計情報
/// </summary>
public class PipelineStatistics
{
    public long TotalPacketsProcessed { get; set; }
    public long TotalPacketsDropped { get; set; }
    public long TotalSecurityBlocks { get; set; }
    public int DeserializeQueueCount { get; set; }
    public int SecurityQueueCount { get; set; }
    public int ProcessQueueCount { get; set; }

    public double DropRate =>
        TotalPacketsProcessed + TotalPacketsDropped > 0
            ? (double)TotalPacketsDropped / (TotalPacketsProcessed + TotalPacketsDropped) * 100
            : 0;
}
