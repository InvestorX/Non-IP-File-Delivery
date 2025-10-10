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

    // 統計情報
    public long TotalEnqueued { get; private set; }
    public long TotalDequeued { get; private set; }
    public long HighPriorityCount { get; private set; }
    public long NormalPriorityCount { get; private set; }
    public long LowPriorityCount { get; private set; }

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

            Log.Debug("Frame enqueued with priority {Priority}: Session={SessionId}, Protocol={Protocol}",
                priority, frame.SessionId, frame.Protocol);
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

        await _semaphore.WaitAsync(cancellationToken);

        lock (_lock)
        {
            if (_priorityQueue.TryDequeue(out var frame, out var priority))
            {
                TotalDequeued++;
                Log.Debug("Frame dequeued with priority {Priority}: Session={SessionId}, Protocol={Protocol}",
                    priority, frame.SessionId, frame.Protocol);
                return frame;
            }
        }

        throw new InvalidOperationException("Queue is empty despite semaphore signal");
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
            Log.Information(
                "QoS Queue Statistics: Enqueued={Enqueued}, Dequeued={Dequeued}, CurrentSize={CurrentSize}, " +
                "HighPriority={HighPriority}, NormalPriority={NormalPriority}, LowPriority={LowPriority}",
                TotalEnqueued, TotalDequeued, CurrentQueueSize,
                HighPriorityCount, NormalPriorityCount, LowPriorityCount);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _semaphore.Dispose();

        Log.Information("QoSFrameQueue disposed");
    }
}
