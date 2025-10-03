using System;

namespace NonIPFileDelivery.Models;

/// <summary>
/// フラグメント情報
/// Phase 3: 大きなペイロードの分割・再構築
/// </summary>
public class FragmentInfo
{
    /// <summary>
    /// フラグメントグループID（同一ペイロードのフラグメントを識別）
    /// </summary>
    public Guid FragmentGroupId { get; set; }
    
    /// <summary>
    /// フラグメントインデックス（0から開始）
    /// </summary>
    public uint FragmentIndex { get; set; }
    
    /// <summary>
    /// 総フラグメント数
    /// </summary>
    public uint TotalFragments { get; set; }
    
    /// <summary>
    /// このフラグメントのサイズ（バイト）
    /// </summary>
    public uint FragmentSize { get; set; }
    
    /// <summary>
    /// 元のペイロード全体のサイズ（バイト）
    /// </summary>
    public long OriginalPayloadSize { get; set; }
    
    /// <summary>
    /// 元のペイロードのハッシュ値（SHA256）
    /// </summary>
    public string? OriginalPayloadHash { get; set; }
    
    /// <summary>
    /// フラグメント作成時刻（UTC）
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// フラグメントグループ（再構築用）
/// </summary>
public class FragmentGroup
{
    /// <summary>
    /// フラグメントグループID
    /// </summary>
    public Guid FragmentGroupId { get; set; }
    
    /// <summary>
    /// 総フラグメント数
    /// </summary>
    public uint TotalFragments { get; set; }
    
    /// <summary>
    /// 元のペイロードサイズ
    /// </summary>
    public long OriginalPayloadSize { get; set; }
    
    /// <summary>
    /// 元のペイロードハッシュ
    /// </summary>
    public string? OriginalPayloadHash { get; set; }
    
    /// <summary>
    /// 受信済みフラグメント
    /// Key: FragmentIndex, Value: FragmentData
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<uint, byte[]> ReceivedFragments { get; set; } = new();
    
    /// <summary>
    /// 最初のフラグメント受信時刻
    /// </summary>
    public DateTime FirstFragmentReceivedAt { get; set; }
    
    /// <summary>
    /// 最後のフラグメント受信時刻
    /// </summary>
    public DateTime LastFragmentReceivedAt { get; set; }
    
    /// <summary>
    /// タイムアウト時間（秒）
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60; // デフォルト60秒
    
    /// <summary>
    /// 全てのフラグメントを受信済みか判定
    /// </summary>
    public bool IsComplete()
    {
        return ReceivedFragments.Count == TotalFragments;
    }
    
    /// <summary>
    /// タイムアウトしているか判定
    /// </summary>
    public bool IsTimedOut()
    {
        return (DateTime.UtcNow - LastFragmentReceivedAt).TotalSeconds > TimeoutSeconds;
    }
    
    /// <summary>
    /// 受信進捗率（0.0 - 1.0）
    /// </summary>
    public double GetProgress()
    {
        return TotalFragments > 0 ? (double)ReceivedFragments.Count / TotalFragments : 0.0;
    }
}
