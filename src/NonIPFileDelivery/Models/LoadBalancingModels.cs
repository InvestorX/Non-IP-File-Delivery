using System;

namespace NonIPFileDelivery.Models;

/// <summary>
/// ロードバランシングアルゴリズム
/// </summary>
public enum LoadBalancingAlgorithm
{
    RoundRobin,
    WeightedRoundRobin,
    LeastConnections,
    Random
}

/// <summary>
/// ロードバランサー統計情報
/// </summary>
public class LoadBalancerStats
{
    public long TotalRequests { get; set; } = 0;
    public long TotalFailures { get; set; } = 0;
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public int ActiveNodes { get; set; } = 0;
}
