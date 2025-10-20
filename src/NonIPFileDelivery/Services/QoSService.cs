using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// QoS (Quality of Service) サービス実装
/// フレーム送信の優先度制御と帯域幅管理を提供
/// </summary>
public class QoSService : IQoSService, IDisposable
{
    private readonly ILoggingService _logger;
    private readonly PriorityQueue<NonIPFrame, int> _priorityQueue;
    private readonly SemaphoreSlim _queueSemaphore;
    private readonly object _queueLock = new();
    private TokenBucket? _tokenBucket;
    private bool _isEnabled;
    private bool _disposed;

    // 優先度レベル
    private const int HIGH_PRIORITY = 0;
    private const int NORMAL_PRIORITY = 100;
    private const int LOW_PRIORITY = 200;

    // 優先度別重み付け（Weighted Fair Queuing用）
    private int _highPriorityWeight = 70;
    private int _normalPriorityWeight = 20;
    private int _lowPriorityWeight = 10;

    // 統計情報
    private long _totalEnqueued;
    private long _totalDequeued;
    private long _highPriorityCount;
    private long _normalPriorityCount;
    private long _lowPriorityCount;
    private long _totalBytesTransmitted;
    private long _totalTokensConsumed;
    private DateTime _lastResetTime;
    private DateTime _lastRateCalcTime;
    private long _lastBytesTransmitted;

    public bool IsEnabled => _isEnabled;
    public int MaxBandwidthMbps => _tokenBucket?.MaxBandwidthMbps ?? 0;

    public QoSService(ILoggingService logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _priorityQueue = new PriorityQueue<NonIPFrame, int>();
        _queueSemaphore = new SemaphoreSlim(0);
        _isEnabled = false;
        _lastResetTime = DateTime.UtcNow;
        _lastRateCalcTime = DateTime.UtcNow;
        _lastBytesTransmitted = 0;

        _logger.Info("QoSService initialized (disabled by default)");
    }

    public void Enable()
    {
        lock (_queueLock)
        {
            _isEnabled = true;
            _logger.Info("QoS enabled");
        }
    }

    public void Disable()
    {
        lock (_queueLock)
        {
            _isEnabled = false;
            _logger.Info("QoS disabled");
        }
    }

    public void SetBandwidthLimit(int maxBandwidthMbps)
    {
        if (maxBandwidthMbps <= 0)
            throw new ArgumentException("Max bandwidth must be positive", nameof(maxBandwidthMbps));

        lock (_queueLock)
        {
            // 1秒分のデータをバースト許容サイズとする
            var burstSizeBytes = maxBandwidthMbps * 125_000L; // Mbps → bytes/sec
            _tokenBucket = new TokenBucket(maxBandwidthMbps, burstSizeBytes);
            _logger.Info($"Bandwidth limit set to {maxBandwidthMbps} Mbps (burst: {burstSizeBytes} bytes)");
        }
    }

    public void ConfigureWeights(int highPriorityWeight, int normalPriorityWeight, int lowPriorityWeight)
    {
        var total = highPriorityWeight + normalPriorityWeight + lowPriorityWeight;
        if (total != 100)
        {
            throw new ArgumentException(
                $"Weight sum must equal 100 (got {total}). " +
                $"High={highPriorityWeight}, Normal={normalPriorityWeight}, Low={lowPriorityWeight}");
        }

        lock (_queueLock)
        {
            _highPriorityWeight = highPriorityWeight;
            _normalPriorityWeight = normalPriorityWeight;
            _lowPriorityWeight = lowPriorityWeight;
            
            _logger.Info($"QoS weights configured: High={highPriorityWeight}%, " +
                        $"Normal={normalPriorityWeight}%, Low={lowPriorityWeight}%");
        }
    }

    public async Task<bool> EnqueueFrameAsync(NonIPFrame frame, FrameFlags? priority = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(QoSService));

        ArgumentNullException.ThrowIfNull(frame);

        var priorityValue = DeterminePriority(frame, priority);

        lock (_queueLock)
        {
            _priorityQueue.Enqueue(frame, priorityValue);
            _totalEnqueued++;

            // 統計更新
            switch (priorityValue)
            {
                case HIGH_PRIORITY:
                    _highPriorityCount++;
                    _logger.Debug($"High priority frame enqueued: Type={frame.Header.Type}, Seq={frame.Header.SequenceNumber}");
                    break;
                case NORMAL_PRIORITY:
                    _normalPriorityCount++;
                    break;
                case LOW_PRIORITY:
                    _lowPriorityCount++;
                    break;
            }
        }

        _queueSemaphore.Release();
        await Task.CompletedTask;
        return true;
    }

    public async Task<NonIPFrame?> DequeueFrameAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(QoSService));

        try
        {
            await _queueSemaphore.WaitAsync(cancellationToken);

            lock (_queueLock)
            {
                if (_priorityQueue.TryDequeue(out var frame, out var priority))
                {
                    _totalDequeued++;
                    
                    if (priority == HIGH_PRIORITY)
                    {
                        _logger.Debug($"High priority frame dequeued: Type={frame.Header.Type}, Seq={frame.Header.SequenceNumber}");
                    }

                    return frame;
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<bool> TryConsumeAsync(long sizeInBytes, CancellationToken cancellationToken = default)
    {
        if (!_isEnabled || _tokenBucket == null)
        {
            // QoS無効時は常に許可
            return true;
        }

        try
        {
            var consumed = await _tokenBucket.TryConsumeAsync(sizeInBytes, cancellationToken);
            
            if (consumed)
            {
                Interlocked.Add(ref _totalBytesTransmitted, sizeInBytes);
                Interlocked.Add(ref _totalTokensConsumed, sizeInBytes);
            }

            return consumed;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error consuming tokens: {ex.Message}");
            return false;
        }
    }

    public QoSStatistics GetStatistics()
    {
        lock (_queueLock)
        {
            var now = DateTime.UtcNow;
            var elapsedSeconds = (now - _lastRateCalcTime).TotalSeconds;
            var currentRate = 0.0;

            if (elapsedSeconds > 0)
            {
                var bytesDiff = _totalBytesTransmitted - _lastBytesTransmitted;
                currentRate = (bytesDiff * 8.0) / (elapsedSeconds * 1_000_000.0); // bps → Mbps
                
                // 1秒ごとにレート計算をリセット
                if (elapsedSeconds >= 1.0)
                {
                    _lastRateCalcTime = now;
                    _lastBytesTransmitted = _totalBytesTransmitted;
                }
            }

            return new QoSStatistics
            {
                IsEnabled = _isEnabled,
                MaxBandwidthMbps = MaxBandwidthMbps,
                TotalEnqueued = _totalEnqueued,
                TotalDequeued = _totalDequeued,
                HighPriorityCount = _highPriorityCount,
                NormalPriorityCount = _normalPriorityCount,
                LowPriorityCount = _lowPriorityCount,
                CurrentQueueSize = _priorityQueue.Count,
                TotalBytesTransmitted = _totalBytesTransmitted,
                TotalTokensConsumed = _totalTokensConsumed,
                CurrentTransmitRateMbps = currentRate,
                HighPriorityWeight = _highPriorityWeight,
                NormalPriorityWeight = _normalPriorityWeight,
                LowPriorityWeight = _lowPriorityWeight,
                LastResetTime = _lastResetTime
            };
        }
    }

    public void ClearQueue()
    {
        lock (_queueLock)
        {
            _priorityQueue.Clear();
            _logger.Info("QoS queue cleared");
        }
    }

    public void LogStatistics()
    {
        var stats = GetStatistics();
        
        _logger.Info(
            $"QoS Statistics: Enabled={stats.IsEnabled}, MaxBW={stats.MaxBandwidthMbps}Mbps, " +
            $"CurrentRate={stats.CurrentTransmitRateMbps:F2}Mbps, " +
            $"Queue={stats.CurrentQueueSize}, " +
            $"Enqueued={stats.TotalEnqueued}, Dequeued={stats.TotalDequeued}, " +
            $"High={stats.HighPriorityCount}, Normal={stats.NormalPriorityCount}, Low={stats.LowPriorityCount}, " +
            $"TotalBytes={stats.TotalBytesTransmitted}, " +
            $"Weights=H:{stats.HighPriorityWeight}%/N:{stats.NormalPriorityWeight}%/L:{stats.LowPriorityWeight}%");
    }

    /// <summary>
    /// フレームの優先度を判定
    /// </summary>
    private int DeterminePriority(NonIPFrame frame, FrameFlags? explicitPriority)
    {
        // 明示的な優先度指定がある場合はそれを使用
        if (explicitPriority.HasValue)
        {
            if (explicitPriority.Value.HasFlag(FrameFlags.Priority))
                return HIGH_PRIORITY;
        }

        // フラグに基づく判定
        if (frame.Header.Flags.HasFlag(FrameFlags.Priority))
            return HIGH_PRIORITY;

        if (frame.Header.Flags.HasFlag(FrameFlags.RequireAck))
            return HIGH_PRIORITY;

        // フレームタイプに基づく判定
        switch (frame.Header.Type)
        {
            case FrameType.Heartbeat:
            case FrameType.Control:
            case FrameType.Ack:
            case FrameType.Nack:
                return HIGH_PRIORITY;

            case FrameType.Data:
            case FrameType.FileTransfer:
                return NORMAL_PRIORITY;

            default:
                return LOW_PRIORITY;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _queueSemaphore.Dispose();
        _disposed = true;

        _logger.Info("QoSService disposed");
    }
}
