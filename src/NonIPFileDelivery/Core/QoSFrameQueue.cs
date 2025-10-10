using NonIpFileDelivery.Core;
using System.Collections.Concurrent;
using Serilog;

namespace NonIPFileDelivery.Core;

/// <summary>
/// QoS（Quality of Service）優先度ベースのフレーム送信キュー
/// HighPriorityフラグを持つフレームを優先的に送信
/// </summary>
public class QoSFrameQueue : IDisposable
{
    private readonly PriorityQueue<SecureFrame, int> _priorityQueue;
    private readonly SemaphoreSlim _queueSemaphore;
    private readonly CancellationTokenSource _cts;
    private readonly object _queueLock = new();
    private bool _disposed;

    // 優先度レベル
    private const int HIGH_PRIORITY = 0;      // 最高優先度（数値が小さいほど優先）
    private const int NORMAL_PRIORITY = 100;  // 通常優先度
    private const int LOW_PRIORITY = 200;     // 低優先度

    // 統計情報
    public long TotalEnqueued { get; private set; }
    public long TotalDequeued { get; private set; }
    public long HighPriorityCount { get; private set; }
    public long NormalPriorityCount { get; private set; }

    /// <summary>
    /// 現在キューに格納されているフレーム数
    /// </summary>
    public int Count
    {
        get
        {
            lock (_queueLock)
            {
                return _priorityQueue.Count;
            }
        }
    }

    public QoSFrameQueue()
    {
        _priorityQueue = new PriorityQueue<SecureFrame, int>();
        _queueSemaphore = new SemaphoreSlim(0);
        _cts = new CancellationTokenSource();

        Log.Information("QoSFrameQueue initialized");
    }

    /// <summary>
    /// フレームをキューに追加
    /// </summary>
    /// <param name="frame">送信するフレーム</param>
    public void Enqueue(SecureFrame frame)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(QoSFrameQueue));

        ArgumentNullException.ThrowIfNull(frame);

        var priority = DeterminePriority(frame);

        lock (_queueLock)
        {
            _priorityQueue.Enqueue(frame, priority);
            TotalEnqueued++;

            if (priority == HIGH_PRIORITY)
            {
                HighPriorityCount++;
                Log.Debug("High priority frame enqueued: SessionId={SessionId}, Seq={Seq}",
                    frame.SessionId, frame.SequenceNumber);
            }
            else
            {
                NormalPriorityCount++;
            }
        }

        _queueSemaphore.Release();
    }

    /// <summary>
    /// フレームをキューから取得（優先度順）
    /// </summary>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>取り出されたフレーム</returns>
    public async Task<SecureFrame?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(QoSFrameQueue));

        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        try
        {
            await _queueSemaphore.WaitAsync(combinedCts.Token);

            lock (_queueLock)
            {
                if (_priorityQueue.TryDequeue(out var frame, out var priority))
                {
                    TotalDequeued++;

                    if (priority == HIGH_PRIORITY)
                    {
                        Log.Debug("High priority frame dequeued: SessionId={SessionId}, Seq={Seq}",
                            frame.SessionId, frame.SequenceNumber);
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
        finally
        {
            combinedCts.Dispose();
        }
    }

    /// <summary>
    /// フレームの優先度を判定
    /// </summary>
    /// <param name="frame">フレーム</param>
    /// <returns>優先度値（小さいほど高優先度）</returns>
    private static int DeterminePriority(SecureFrame frame)
    {
        // HighPriorityフラグがセットされている場合
        if (frame.Flags.HasFlag(SecureFrame.FrameFlags.HighPriority))
        {
            return HIGH_PRIORITY;
        }

        // ACK要求フレームも優先度を上げる
        if (frame.Flags.HasFlag(SecureFrame.FrameFlags.RequireAck))
        {
            return HIGH_PRIORITY;
        }

        // ハートビートは高優先度
        if (frame.Protocol == SecureFrame.ProtocolType.Heartbeat)
        {
            return HIGH_PRIORITY;
        }

        // 制御メッセージも高優先度
        if (frame.Protocol == SecureFrame.ProtocolType.ControlMessage)
        {
            return HIGH_PRIORITY;
        }

        // 通常のデータフレーム
        return NORMAL_PRIORITY;
    }

    /// <summary>
    /// キューをクリア
    /// </summary>
    public void Clear()
    {
        lock (_queueLock)
        {
            _priorityQueue.Clear();
            Log.Information("QoS queue cleared");
        }
    }

    /// <summary>
    /// 統計情報を取得
    /// </summary>
    public Dictionary<string, long> GetStatistics()
    {
        lock (_queueLock)
        {
            return new Dictionary<string, long>
            {
                { "TotalEnqueued", TotalEnqueued },
                { "TotalDequeued", TotalDequeued },
                { "HighPriorityCount", HighPriorityCount },
                { "NormalPriorityCount", NormalPriorityCount },
                { "CurrentQueueSize", _priorityQueue.Count }
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _cts.Cancel();
        _cts.Dispose();
        _queueSemaphore.Dispose();
        _disposed = true;

        Log.Information("QoSFrameQueue disposed");
    }
}
