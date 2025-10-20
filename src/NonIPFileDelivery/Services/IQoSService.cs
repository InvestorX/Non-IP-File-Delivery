using System;
using System.Threading;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// QoS (Quality of Service) サービスインターフェース
/// フレーム送信の優先度制御と帯域幅管理を提供
/// </summary>
public interface IQoSService
{
    /// <summary>
    /// QoS機能が有効かどうか
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 現在の最大帯域幅（Mbps）
    /// </summary>
    int MaxBandwidthMbps { get; }

    /// <summary>
    /// QoS機能を有効化
    /// </summary>
    void Enable();

    /// <summary>
    /// QoS機能を無効化
    /// </summary>
    void Disable();

    /// <summary>
    /// 帯域幅制限を設定
    /// </summary>
    /// <param name="maxBandwidthMbps">最大帯域幅（Mbps）</param>
    void SetBandwidthLimit(int maxBandwidthMbps);

    /// <summary>
    /// 優先度別の重み付けを設定（Weighted Fair Queuing用）
    /// </summary>
    /// <param name="highPriorityWeight">高優先度の重み (0-100)</param>
    /// <param name="normalPriorityWeight">通常優先度の重み (0-100)</param>
    /// <param name="lowPriorityWeight">低優先度の重み (0-100)</param>
    void ConfigureWeights(int highPriorityWeight, int normalPriorityWeight, int lowPriorityWeight);

    /// <summary>
    /// フレームをQoSキューに追加
    /// </summary>
    /// <param name="frame">送信するフレーム</param>
    /// <param name="priority">優先度（nullの場合は自動判定）</param>
    /// <returns>キューに追加できた場合true</returns>
    Task<bool> EnqueueFrameAsync(NonIPFrame frame, FrameFlags? priority = null);

    /// <summary>
    /// QoSキューから次のフレームを取得（優先度順）
    /// </summary>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>取得したフレーム（キューが空の場合null）</returns>
    Task<NonIPFrame?> DequeueFrameAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定サイズのデータ送信が帯域幅制限内か確認し、トークンを消費
    /// </summary>
    /// <param name="sizeInBytes">送信データサイズ（バイト）</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>送信可能な場合true（トークン消費済み）</returns>
    Task<bool> TryConsumeAsync(long sizeInBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// QoS統計情報を取得
    /// </summary>
    /// <returns>統計情報の辞書</returns>
    QoSStatistics GetStatistics();

    /// <summary>
    /// QoSキューをクリア
    /// </summary>
    void ClearQueue();

    /// <summary>
    /// 統計情報をログ出力
    /// </summary>
    void LogStatistics();
}

/// <summary>
/// QoS統計情報
/// </summary>
public class QoSStatistics
{
    public bool IsEnabled { get; set; }
    public int MaxBandwidthMbps { get; set; }
    public long TotalEnqueued { get; set; }
    public long TotalDequeued { get; set; }
    public long HighPriorityCount { get; set; }
    public long NormalPriorityCount { get; set; }
    public long LowPriorityCount { get; set; }
    public int CurrentQueueSize { get; set; }
    public long TotalBytesTransmitted { get; set; }
    public long TotalTokensConsumed { get; set; }
    public double CurrentTransmitRateMbps { get; set; }
    public int HighPriorityWeight { get; set; }
    public int NormalPriorityWeight { get; set; }
    public int LowPriorityWeight { get; set; }
    public DateTime LastResetTime { get; set; }
}
