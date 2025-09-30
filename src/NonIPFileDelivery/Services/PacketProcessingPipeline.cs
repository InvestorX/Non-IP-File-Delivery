using System.Threading.Tasks.Dataflow;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// TPL Dataflow によるパケット処理パイプライン
/// </summary>
public class PacketProcessingPipeline : IDisposable
{
    private readonly ILoggingService _logger;
    private readonly IFrameService _frameService;
    private readonly ISecurityService _securityService;

    private TransformBlock<byte[], NonIPFrame?>? _deserializeBlock;
    private TransformBlock<NonIPFrame?, (NonIPFrame? Frame, ScanResult? ScanResult)>? _securityBlock;
    private ActionBlock<(NonIPFrame? Frame, ScanResult? ScanResult)>? _processBlock;

    private long _totalPacketsProcessed;
    private long _totalPacketsDropped;
    private long _totalSecurityBlocks;
    private long _totalBytesProcessed;

    private CancellationTokenSource? _pipelineCts;
    private bool _disposed;
    private readonly System.Diagnostics.Stopwatch _uptime = new();
    private DateTime _startTime;

    public PacketProcessingPipeline(
        ILoggingService logger,
        IFrameService frameService,
        ISecurityService securityService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _frameService = frameService ?? throw new ArgumentNullException(nameof(frameService));
        _securityService = securityService ?? throw new ArgumentNullException(nameof(securityService));
    }

    public void Initialize()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PacketProcessingPipeline));

        _pipelineCts = new CancellationTokenSource();
        _startTime = DateTime.UtcNow;
        _uptime.Start();

        var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };

        _deserializeBlock = new TransformBlock<byte[], NonIPFrame?>(
            raw =>
            {
                if (_pipelineCts!.Token.IsCancellationRequested) return null;
                try
                {
                    using (_logger.BeginPerformanceScope("FrameDeserialization"))
                    {
                        var frame = _frameService.DeserializeFrame(raw);
                        if (frame == null) Interlocked.Increment(ref _totalPacketsDropped);
                        else Interlocked.Add(ref _totalBytesProcessed, raw.Length);
                        return frame;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("Frame deserialization failed", ex);
                    Interlocked.Increment(ref _totalPacketsDropped);
                    return null;
                }
            },
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = 4,
                BoundedCapacity = 1000,
                CancellationToken = _pipelineCts.Token
            });

        _securityBlock = new TransformBlock<NonIPFrame?, (NonIPFrame?, ScanResult?)>(
            async frame =>
            {
                if (frame == null) return (null, null);
                try
                {
                    var result = await _securityService.ScanData(frame.Payload, $"frame_{frame.Header.SequenceNumber}");
                    if (!result.IsClean) Interlocked.Increment(ref _totalSecurityBlocks);
                    return (frame, result);
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
                CancellationToken = _pipelineCts.Token
            });

        _processBlock = new ActionBlock<(NonIPFrame?, ScanResult?)>(
            async tuple =>
            {
                var (frame, scan) = tuple;
                if (frame == null) return;

                try
                {
                    if (scan != null && !scan.IsClean)
                    {
                        _logger.Warning($"Dropping malicious frame {frame.Header.SequenceNumber}");
                        await _securityService.QuarantineFile($"frame_{frame.Header.SequenceNumber}.bin", scan.ThreatName ?? "Unknown threat");
                        Interlocked.Increment(ref _totalPacketsDropped);
                        return;
                    }

                    await ProcessFrameAsync(frame);
                    Interlocked.Increment(ref _totalPacketsProcessed);

                    _logger.LogWithProperties(
                        Models.LogLevel.Debug,
                        "Frame processed",
                        ("Seq", frame.Header.SequenceNumber),
                        ("Type", frame.Header.Type.ToString()),
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
                MaxDegreeOfParallelism = 1,
                BoundedCapacity = 100,
                CancellationToken = _pipelineCts.Token
            });

        _deserializeBlock.LinkTo(_securityBlock, linkOptions, f => f != null);
        _deserializeBlock.LinkTo(DataflowBlock.NullTarget<NonIPFrame?>(), f => f == null);
        _securityBlock.LinkTo(_processBlock, linkOptions);

        _logger.Info("Packet processing pipeline initialized");
    }

    public async Task<bool> EnqueuePacketAsync(byte[] rawData, TimeSpan? timeout = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PacketProcessingPipeline));
        if (_deserializeBlock == null) throw new InvalidOperationException("Pipeline not initialized.");
        ArgumentNullException.ThrowIfNull(rawData);
        if (rawData.Length == 0)
        {
            _logger.Warning("Attempted to enqueue empty packet");
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            return await _deserializeBlock.SendAsync(rawData, cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("Packet enqueue timeout");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to enqueue packet", ex);
            return false;
        }
    }

    public async Task CompleteAsync()
    {
        if (_disposed) return;
        _logger.Info("Completing packet processing pipeline...");
        _deserializeBlock?.Complete();
        if (_processBlock != null)
        {
            try { await _processBlock.Completion.ConfigureAwait(false); }
            catch (Exception ex) { _logger.Error("Error during pipeline completion", ex); }
        }

        _logger.Info($"Pipeline completed. Processed={_totalPacketsProcessed}, Dropped={_totalPacketsDropped}, SecBlocks={_totalSecurityBlocks}");
    }

    public PipelineStatistics GetStatistics()
    {
        var uptime = _uptime.Elapsed;
        var total = Interlocked.Read(ref _totalPacketsProcessed) + Interlocked.Read(ref _totalPacketsDropped);
        var bytes = Interlocked.Read(ref _totalBytesProcessed);

        return new PipelineStatistics
        {
            TotalPacketsProcessed = Interlocked.Read(ref _totalPacketsProcessed),
            TotalPacketsDropped = Interlocked.Read(ref _totalPacketsDropped),
            TotalSecurityBlocks = Interlocked.Read(ref _totalSecurityBlocks),
            TotalBytesProcessed = bytes,
            DeserializeQueueCount = _deserializeBlock?.InputCount ?? 0,
            SecurityQueueCount = _securityBlock?.InputCount ?? 0,
            ProcessQueueCount = _processBlock?.InputCount ?? 0,
            Uptime = uptime,
            StartTime = _startTime,
            PacketsPerSecond = uptime.TotalSeconds > 0 ? total / uptime.TotalSeconds : 0,
            ThroughputMbps = uptime.TotalSeconds > 0 ? (bytes * 8.0 / 1_000_000.0) / uptime.TotalSeconds : 0
        };
    }

    private async Task ProcessFrameAsync(NonIPFrame frame)
    {
        switch (frame.Header.Type)
        {
            case FrameType.Heartbeat:
                _logger.Debug($"Heartbeat received from {MacToString(frame.Header.SourceMac)}");
                break;
            case FrameType.Data:
                _logger.Debug($"Data frame received: {frame.Payload.Length} bytes");
                break;
            case FrameType.FileTransfer:
                _logger.Info("File transfer frame received");
                break;
            case FrameType.Acknowledgment:
                _logger.Debug($"ACK received: {frame.Header.SequenceNumber}");
                break;
            default:
                _logger.Warning($"Unknown frame type: {frame.Header.Type}");
                break;
        }
        await Task.Yield();
    }

    private static string MacToString(byte[] mac) => string.Join(":", mac.Select(b => b.ToString("X2")));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _pipelineCts?.Cancel();
            _deserializeBlock?.Complete();
            _securityBlock?.Complete();
            _processBlock?.Complete();

            Task.WhenAll(
                _deserializeBlock?.Completion ?? Task.CompletedTask,
                _securityBlock?.Completion ?? Task.CompletedTask,
                _processBlock?.Completion ?? Task.CompletedTask
            ).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger?.Error("Error during pipeline disposal", ex);
        }
        finally
        {
            _pipelineCts?.Dispose();
        }
    }
}

public sealed class PipelineStatistics
{
    public long TotalPacketsProcessed { get; set; }
    public long TotalPacketsDropped { get; set; }
    public long TotalSecurityBlocks { get; set; }
    public long TotalBytesProcessed { get; set; }
    public int DeserializeQueueCount { get; set; }
    public int SecurityQueueCount { get; set; }
    public int ProcessQueueCount { get; set; }
    public TimeSpan Uptime { get; set; }
    public DateTime StartTime { get; set; }
    public double PacketsPerSecond { get; set; }
    public double ThroughputMbps { get; set; }
    public double DropRate =>
        TotalPacketsProcessed + TotalPacketsDropped > 0
            ? (double)TotalPacketsDropped / (TotalPacketsProcessed + TotalPacketsDropped) * 100
            : 0;
    public double SecurityBlockRate =>
        TotalPacketsProcessed > 0
            ? (double)TotalSecurityBlocks / TotalPacketsProcessed * 100
            : 0;
    public int TotalQueuedPackets => DeserializeQueueCount + SecurityQueueCount + ProcessQueueCount;
}
