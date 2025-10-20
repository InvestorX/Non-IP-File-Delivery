using System;

namespace NonIPFileDelivery.Models;

/// <summary>
/// ノードの状態
/// </summary>
public enum NodeState
{
    Unknown,
    Active,
    Standby,
    Failed,
    Initializing
}

/// <summary>
/// ノード情報
/// </summary>
public class NodeInfo
{
    public string NodeId { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public NodeState State { get; set; } = NodeState.Unknown;
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public int Priority { get; set; } = 100;
    public int Weight { get; set; } = 1;
    public bool IsHealthy { get; set; } = true;
    public int ActiveConnections { get; set; } = 0;
    
    /// <summary>
    /// ノードが回復した時刻（自動フェイルバック判定用）
    /// </summary>
    public DateTime? RecoveryTime { get; set; }
}

/// <summary>
/// ハートビート情報
/// </summary>
public class HeartbeatInfo
{
    public string NodeId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public NodeState State { get; set; } = NodeState.Unknown;
    public int ActiveConnections { get; set; } = 0;
    public long MemoryUsageMB { get; set; } = 0;
    public double CpuUsagePercent { get; set; } = 0;
}

/// <summary>
/// ノード間通信用ハートビートメッセージ
/// </summary>
public class HeartbeatMessage
{
    /// <summary>メッセージタイプ識別子</summary>
    public const string MessageType = "HEARTBEAT";
    
    /// <summary>プロトコルバージョン</summary>
    public int Version { get; set; } = 1;
    
    /// <summary>メッセージタイプ</summary>
    public string Type { get; set; } = MessageType;
    
    /// <summary>送信元ノードID</summary>
    public string NodeId { get; set; } = string.Empty;
    
    /// <summary>送信元MACアドレス</summary>
    public string SourceMac { get; set; } = string.Empty;
    
    /// <summary>タイムスタンプ（UTC）</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    /// <summary>ノード状態</summary>
    public NodeState State { get; set; } = NodeState.Unknown;
    
    /// <summary>ノード優先度</summary>
    public int Priority { get; set; } = 100;
    
    /// <summary>アクティブ接続数</summary>
    public int ActiveConnections { get; set; } = 0;
    
    /// <summary>メモリ使用量（MB）</summary>
    public long MemoryUsageMB { get; set; } = 0;
    
    /// <summary>CPU使用率（%）</summary>
    public double CpuUsagePercent { get; set; } = 0;
    
    /// <summary>
    /// ハートビートメッセージをバイト配列にシリアライズ
    /// </summary>
    public byte[] Serialize()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }
    
    /// <summary>
    /// バイト配列からハートビートメッセージをデシリアライズ
    /// </summary>
    public static HeartbeatMessage? Deserialize(byte[] data)
    {
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(data);
            return System.Text.Json.JsonSerializer.Deserialize<HeartbeatMessage>(json);
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// ハートビートメッセージかどうかを判定
    /// </summary>
    public static bool IsHeartbeatMessage(byte[] data)
    {
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(data);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("Type", out var typeProperty) 
                   && typeProperty.GetString() == MessageType;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// フェイルオーバーイベント
/// </summary>
public class FailoverEvent
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string FromNodeId { get; set; } = string.Empty;
    public string ToNodeId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool Success { get; set; } = false;
}
