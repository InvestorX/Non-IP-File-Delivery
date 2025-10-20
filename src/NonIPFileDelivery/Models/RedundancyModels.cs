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
