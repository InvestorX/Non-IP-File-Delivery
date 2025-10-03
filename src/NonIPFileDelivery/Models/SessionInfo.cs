using System;
using System.Collections.Concurrent;

namespace NonIPFileDelivery.Models;

/// <summary>
/// セッション情報
/// Phase 3: セッション管理機能
/// </summary>
public class SessionInfo
{
    /// <summary>
    /// セッションID（GUID）
    /// </summary>
    public Guid SessionId { get; set; }
    
    /// <summary>
    /// 接続ID（高速検索用）
    /// </summary>
    public ulong ConnectionId { get; set; }
    
    /// <summary>
    /// 送信元MACアドレス
    /// </summary>
    public byte[] SourceMAC { get; set; } = new byte[6];
    
    /// <summary>
    /// 宛先MACアドレス
    /// </summary>
    public byte[] DestinationMAC { get; set; } = new byte[6];
    
    /// <summary>
    /// セッション状態
    /// </summary>
    public SessionState State { get; set; }
    
    /// <summary>
    /// セッション開始時刻（UTC）
    /// </summary>
    public DateTime StartTime { get; set; }
    
    /// <summary>
    /// 最終アクティブ時刻（UTC）
    /// </summary>
    public DateTime LastActiveTime { get; set; }
    
    /// <summary>
    /// セッションタイムアウト時間（秒）
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300; // デフォルト5分
    
    /// <summary>
    /// 送信パケット数
    /// </summary>
    public long PacketsSent { get; set; }
    
    /// <summary>
    /// 受信パケット数
    /// </summary>
    public long PacketsReceived { get; set; }
    
    /// <summary>
    /// 送信バイト数
    /// </summary>
    public long BytesSent { get; set; }
    
    /// <summary>
    /// 受信バイト数
    /// </summary>
    public long BytesReceived { get; set; }
    
    /// <summary>
    /// フラグメント管理用辞書
    /// Key: FragmentGroupId, Value: FragmentGroup
    /// </summary>
    public ConcurrentDictionary<Guid, FragmentGroup> FragmentGroups { get; set; } = new();
    
    /// <summary>
    /// セッション固有のメタデータ
    /// </summary>
    public ConcurrentDictionary<string, object> Metadata { get; set; } = new();
    
    /// <summary>
    /// セッションがタイムアウトしているか判定
    /// </summary>
    public bool IsTimedOut()
    {
        return (DateTime.UtcNow - LastActiveTime).TotalSeconds > TimeoutSeconds;
    }
    
    /// <summary>
    /// セッションが有効か判定
    /// </summary>
    public bool IsActive()
    {
        return State == SessionState.Active && !IsTimedOut();
    }
}

/// <summary>
/// セッション状態
/// </summary>
public enum SessionState
{
    /// <summary>
    /// 確立中
    /// </summary>
    Establishing = 0,
    
    /// <summary>
    /// アクティブ
    /// </summary>
    Active = 1,
    
    /// <summary>
    /// 終了処理中
    /// </summary>
    Closing = 2,
    
    /// <summary>
    /// クローズ済み
    /// </summary>
    Closed = 3,
    
    /// <summary>
    /// タイムアウト
    /// </summary>
    TimedOut = 4,
    
    /// <summary>
    /// エラー
    /// </summary>
    Error = 5
}
