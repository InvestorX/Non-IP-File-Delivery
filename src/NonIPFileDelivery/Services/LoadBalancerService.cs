using System;
using System.Collections.Generic;
using System.Linq;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// ロードバランサーサービス実装
/// </summary>
public class LoadBalancerService : ILoadBalancerService
{
    private readonly ILoggingService _logger;
    private readonly LoadBalancingAlgorithm _algorithm;
    private readonly Dictionary<string, NodeInfo> _nodes;
    private readonly LoadBalancerStats _stats;
    private int _currentIndex;
    private readonly object _lockObject = new object();
    private bool _disposed;

    public LoadBalancerService(ILoggingService logger, LoadBalancingAlgorithm algorithm, NodeInfo[] nodes)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _algorithm = algorithm;
        _nodes = new Dictionary<string, NodeInfo>();
        _stats = new LoadBalancerStats();
        _currentIndex = 0;

        if (nodes != null)
        {
            foreach (var node in nodes)
            {
                _nodes[node.NodeId] = node;
            }
            _stats.ActiveNodes = _nodes.Count(n => n.Value.IsHealthy);
        }

        _logger.Info($"LoadBalancerService initialized with {_algorithm} algorithm, {_nodes.Count} nodes");
    }

    public NodeInfo? SelectNode()
    {
        lock (_lockObject)
        {
            var healthyNodes = _nodes.Values.Where(n => n.IsHealthy && n.State == NodeState.Active).ToList();

            if (!healthyNodes.Any())
            {
                _logger.Warning("No healthy nodes available");
                _stats.TotalFailures++;
                return null;
            }

            NodeInfo? selectedNode = _algorithm switch
            {
                LoadBalancingAlgorithm.RoundRobin => SelectRoundRobin(healthyNodes),
                LoadBalancingAlgorithm.WeightedRoundRobin => SelectWeightedRoundRobin(healthyNodes),
                LoadBalancingAlgorithm.LeastConnections => SelectLeastConnections(healthyNodes),
                LoadBalancingAlgorithm.Random => SelectRandom(healthyNodes),
                _ => SelectRoundRobin(healthyNodes)
            };

            if (selectedNode != null)
            {
                _stats.TotalRequests++;
                _logger.Debug($"Selected node: {selectedNode.NodeId} using {_algorithm}");
            }

            return selectedNode;
        }
    }

    private NodeInfo SelectRoundRobin(List<NodeInfo> healthyNodes)
    {
        var node = healthyNodes[_currentIndex % healthyNodes.Count];
        _currentIndex = (_currentIndex + 1) % healthyNodes.Count;
        return node;
    }

    private NodeInfo SelectWeightedRoundRobin(List<NodeInfo> healthyNodes)
    {
        // 重み付きラウンドロビン: 重みに基づいてノードを選択
        var totalWeight = healthyNodes.Sum(n => n.Weight);
        var random = new Random();
        var randomWeight = random.Next(totalWeight);
        var cumulativeWeight = 0;

        foreach (var node in healthyNodes)
        {
            cumulativeWeight += node.Weight;
            if (randomWeight < cumulativeWeight)
            {
                return node;
            }
        }

        return healthyNodes.Last();
    }

    private NodeInfo SelectLeastConnections(List<NodeInfo> healthyNodes)
    {
        return healthyNodes.OrderBy(n => n.ActiveConnections).First();
    }

    private NodeInfo SelectRandom(List<NodeInfo> healthyNodes)
    {
        var random = new Random();
        return healthyNodes[random.Next(healthyNodes.Count)];
    }

    public void RecordConnection(string nodeId)
    {
        lock (_lockObject)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.ActiveConnections++;
                _logger.Debug($"Node {nodeId} connections: {node.ActiveConnections}");
            }
        }
    }

    public void RecordDisconnection(string nodeId)
    {
        lock (_lockObject)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.ActiveConnections = Math.Max(0, node.ActiveConnections - 1);
                _logger.Debug($"Node {nodeId} connections: {node.ActiveConnections}");
            }
        }
    }

    public void UpdateNodeHealth(string nodeId, bool isHealthy)
    {
        lock (_lockObject)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                var wasHealthy = node.IsHealthy;
                node.IsHealthy = isHealthy;
                node.LastHeartbeat = DateTime.UtcNow;

                if (wasHealthy != isHealthy)
                {
                    _logger.Info($"Node {nodeId} health changed: {wasHealthy} -> {isHealthy}");
                    _stats.ActiveNodes = _nodes.Count(n => n.Value.IsHealthy);
                }
            }
        }
    }

    public LoadBalancerStats GetStats()
    {
        lock (_lockObject)
        {
            _stats.ActiveNodes = _nodes.Count(n => n.Value.IsHealthy);
            return _stats;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _logger.Info($"LoadBalancerService disposed. Total requests: {_stats.TotalRequests}, Failures: {_stats.TotalFailures}");
    }
}
