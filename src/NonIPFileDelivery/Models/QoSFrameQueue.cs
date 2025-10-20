using NonIpFileDelivery.Core;
using Serilog;

namespace NonIPFileDelivery.Models;

/// <summary>
/// QoS（Quality of Service）対応のフレーム送信キュー
/// 優先度に基づいてフレームを管理し、優先度の高いフレームから順に送信
/// </summary>
public class QoSFrameQueue : IDisposable
{
    private readonly PriorityQueue<SecureFrame, int> _priorityQueue = new();
    private readonly SemaphoreSlim _semaphore = new(0);
    private readonly object _lock = new();
    private bool _disposed;

    // 優先度レベル（数値が小さいほど優先度が高い）
    private const int HIGH_PRIORITY = 0;
    private const int NORMAL_PRIORITY = 100;
    private const int LOW_PRIORITY = 200;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="queueDepthWarningThreshold">キュー深度の警告閾値（デフォルト: 1000）</param>
    /// <param name="queueDepthCriticalThreshold">キュー深度の緊急閾値（デフォルト: 5000）</param>
    public QoSFrameQueue(int queueDepthWarningThreshold = 1000, int queueDepthCriticalThreshold = 5000)
    {
        _queueDepthWarningThreshold = queueDepthWarningThreshold;
        _queueDepthCriticalThreshold = queueDepthCriticalThreshold;
        Log.Information("QoSFrameQueue initialized with WarningThreshold={Warning}, CriticalThreshold={Critical}",
            queueDepthWarningThreshold, queueDepthCriticalThreshold);
    }

    // 統計情報
    public long TotalEnqueued { get; private set; }
    public long TotalDequeued { get; private set; }
    public long HighPriorityCount { get; private set; }
    public long NormalPriorityCount { get; private set; }
    public long LowPriorityCount { get; private set; }
    
    // パフォーマンスメトリクス
    private int _peakQueueSize;
    private DateTime? _lastEnqueueTime;
    private DateTime? _lastDequeueTime;
    private readonly List<TimeSpan> _dequeuLatencies = new();
    private const int MAX_LATENCY_SAMPLES = 1000; // 保持するレイテンシサンプル数
    
    // 監視設定
    private readonly int _queueDepthWarningThreshold;
    private readonly int _queueDepthCriticalThreshold;
    private DateTime _lastWarningTime = DateTime.MinValue;
    private const int WARNING_COOLDOWN_SECONDS = 60; // 警告のクールダウン時間

    /// <summary>
    /// 現在のキューサイズを取得
    /// </summary>
    public int CurrentQueueSize
    {
        get
        {
            lock (_lock)
            {
                return _priorityQueue.Count;
            }
        }
    }

    /// <summary>
    /// フレームをキューに追加
    /// </summary>
    /// <param name="frame">追加するフレーム</param>
    public void Enqueue(SecureFrame frame)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(QoSFrameQueue));

        lock (_lock)
        {
            var priority = DeterminePriority(frame);
            _priorityQueue.Enqueue(frame, priority);
            TotalEnqueued++;
            _lastEnqueueTime = DateTime.UtcNow;

            // 統計更新
            switch (priority)
            {
                case HIGH_PRIORITY:
                    HighPriorityCount++;
                    break;
                case NORMAL_PRIORITY:
                    NormalPriorityCount++;
                    break;
                case LOW_PRIORITY:
                    LowPriorityCount++;
                    break;
            }

            // ピークキューサイズ更新
            if (_priorityQueue.Count > _peakQueueSize)
            {
                _peakQueueSize = _priorityQueue.Count;
                Log.Debug("New peak queue size reached: {PeakSize}", _peakQueueSize);
            }

            // キュー深度監視
            CheckQueueDepth(_priorityQueue.Count);

            Log.Debug("Frame enqueued with priority {Priority}: Session={SessionId}, Protocol={Protocol}, CurrentQueueSize={CurrentSize}",
                priority, frame.SessionId, frame.Protocol, _priorityQueue.Count);
        }

        _semaphore.Release();
    }

    /// <summary>
    /// フレームを優先度順に取り出す（非同期）
    /// </summary>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>取り出されたフレーム</returns>
    public async Task<SecureFrame> DequeueAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(QoSFrameQueue));

        var enqueueTime = DateTime.UtcNow;
        await _semaphore.WaitAsync(cancellationToken);
        var dequeueTime = DateTime.UtcNow;

        lock (_lock)
        {
            if (_priorityQueue.TryDequeue(out var frame, out var priority))
            {
                TotalDequeued++;
                _lastDequeueTime = dequeueTime;

                // レイテンシ計測
                var latency = dequeueTime - enqueueTime;
                RecordLatency(latency);

                Log.Debug("Frame dequeued with priority {Priority}: Session={SessionId}, Protocol={Protocol}, Latency={Latency}ms, CurrentQueueSize={CurrentSize}",
                    priority, frame.SessionId, frame.Protocol, latency.TotalMilliseconds, _priorityQueue.Count);
                return frame;
            }
        }

        throw new InvalidOperationException("Queue is empty despite semaphore signal");
    }

    /// <summary>
    /// レイテンシを記録（サンプル数制限付き）
    /// </summary>
    private void RecordLatency(TimeSpan latency)
    {
        lock (_lock)
        {
            _dequeuLatencies.Add(latency);
            
            // サンプル数が上限を超えたら古いものを削除
            if (_dequeuLatencies.Count > MAX_LATENCY_SAMPLES)
            {
                _dequeuLatencies.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// キュー深度をチェックし、閾値を超えた場合は警告ログ出力
    /// </summary>
    private void CheckQueueDepth(int currentDepth)
    {
        var now = DateTime.UtcNow;
        
        // クールダウン期間チェック（連続警告を防ぐ）
        if ((now - _lastWarningTime).TotalSeconds < WARNING_COOLDOWN_SECONDS)
            return;

        if (currentDepth >= _queueDepthCriticalThreshold)
        {
            Log.Error("CRITICAL: Queue depth reached critical threshold: {CurrentDepth}/{CriticalThreshold}, " +
                      "HighPriority={HighPriority}, NormalPriority={NormalPriority}, LowPriority={LowPriority}",
                currentDepth, _queueDepthCriticalThreshold,
                HighPriorityCount, NormalPriorityCount, LowPriorityCount);
            _lastWarningTime = now;
        }
        else if (currentDepth >= _queueDepthWarningThreshold)
        {
            Log.Warning("Queue depth reached warning threshold: {CurrentDepth}/{WarningThreshold}, " +
                        "HighPriority={HighPriority}, NormalPriority={NormalPriority}, LowPriority={LowPriority}",
                currentDepth, _queueDepthWarningThreshold,
                HighPriorityCount, NormalPriorityCount, LowPriorityCount);
            _lastWarningTime = now;
        }
    }

    /// <summary>
    /// フレームの優先度を決定
    /// </summary>
    /// <param name="frame">フレーム</param>
    /// <returns>優先度（数値が小さいほど高優先度）</returns>
    private int DeterminePriority(SecureFrame frame)
    {
        // HighPriorityフラグが設定されている場合
        if ((frame.Flags & SecureFrame.FrameFlags.HighPriority) != 0)
        {
            return HIGH_PRIORITY;
        }

        // ACK要求フレームは高優先度
        if ((frame.Flags & SecureFrame.FrameFlags.RequireAck) != 0)
        {
            return HIGH_PRIORITY;
        }

        // ハートビートやコントロールメッセージは高優先度
        if (frame.Protocol == SecureFrame.ProtocolType.Heartbeat ||
            frame.Protocol == SecureFrame.ProtocolType.ControlMessage)
        {
            return HIGH_PRIORITY;
        }

        // データ転送は通常優先度
        if (frame.Protocol == SecureFrame.ProtocolType.FtpData ||
            frame.Protocol == SecureFrame.ProtocolType.SftpData ||
            frame.Protocol == SecureFrame.ProtocolType.PostgreSql)
        {
            return NORMAL_PRIORITY;
        }

        // その他は低優先度
        return LOW_PRIORITY;
    }

    /// <summary>
    /// 統計情報をログ出力
    /// </summary>
    public void LogStatistics()
    {
        lock (_lock)
        {
            var stats = GetStatistics();
            Log.Information(
                "QoS Queue Statistics: Enqueued={Enqueued}, Dequeued={Dequeued}, CurrentSize={CurrentSize}, " +
                "PeakSize={PeakSize}, HighPriority={HighPriority}, NormalPriority={NormalPriority}, LowPriority={LowPriority}, " +
                "AvgLatency={AvgLatency}ms, MaxLatency={MaxLatency}ms, MinLatency={MinLatency}ms",
                TotalEnqueued, TotalDequeued, CurrentQueueSize, stats.PeakQueueSize,
                HighPriorityCount, NormalPriorityCount, LowPriorityCount,
                stats.AverageLatencyMs, stats.MaxLatencyMs, stats.MinLatencyMs);
        }
    }

    /// <summary>
    /// 詳細な統計情報を取得
    /// </summary>
    /// <returns>統計情報オブジェクト</returns>
    public QoSStatistics GetStatistics()
    {
        lock (_lock)
        {
            var avgLatency = _dequeuLatencies.Count > 0
                ? _dequeuLatencies.Average(l => l.TotalMilliseconds)
                : 0;
            var maxLatency = _dequeuLatencies.Count > 0
                ? _dequeuLatencies.Max(l => l.TotalMilliseconds)
                : 0;
            var minLatency = _dequeuLatencies.Count > 0
                ? _dequeuLatencies.Min(l => l.TotalMilliseconds)
                : 0;

            return new QoSStatistics
            {
                TotalEnqueued = TotalEnqueued,
                TotalDequeued = TotalDequeued,
                CurrentQueueSize = CurrentQueueSize,
                PeakQueueSize = _peakQueueSize,
                HighPriorityCount = HighPriorityCount,
                NormalPriorityCount = NormalPriorityCount,
                LowPriorityCount = LowPriorityCount,
                AverageLatencyMs = avgLatency,
                MaxLatencyMs = maxLatency,
                MinLatencyMs = minLatency,
                LastEnqueueTime = _lastEnqueueTime,
                LastDequeueTime = _lastDequeueTime,
                LatencySampleCount = _dequeuLatencies.Count
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _semaphore.Dispose();

        Log.Information("QoSFrameQueue disposed: TotalEnqueued={TotalEnqueued}, TotalDequeued={TotalDequeued}, PeakQueueSize={PeakSize}",
            TotalEnqueued, TotalDequeued, _peakQueueSize);
    }
}

/// <summary>
/// QoSキューの統計情報
/// </summary>
public class QoSStatistics
{
    public long TotalEnqueued { get; init; }
    public long TotalDequeued { get; init; }
    public int CurrentQueueSize { get; init; }
    public int PeakQueueSize { get; init; }
    public long HighPriorityCount { get; init; }
    public long NormalPriorityCount { get; init; }
    public long LowPriorityCount { get; init; }
    public double AverageLatencyMs { get; init; }
    public double MaxLatencyMs { get; init; }
    public double MinLatencyMs { get; init; }
    public DateTime? LastEnqueueTime { get; init; }
    public DateTime? LastDequeueTime { get; init; }
    public int LatencySampleCount { get; init; }
}
