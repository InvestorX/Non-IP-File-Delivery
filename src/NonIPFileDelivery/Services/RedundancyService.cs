using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// 冗長化サービス実装（Active-Standby構成）
/// </summary>
public class RedundancyService : IRedundancyService
{
    private readonly ILoggingService _logger;
    private readonly RedundancyConfig _config;
    private readonly Dictionary<string, NodeInfo> _nodes;
    private readonly List<FailoverEvent> _failoverHistory;
    private NodeState _currentState;
    private Timer? _heartbeatTimer;
    private bool _disposed;
    private readonly object _lockObject = new object();

    public RedundancyService(ILoggingService logger, RedundancyConfig config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _nodes = new Dictionary<string, NodeInfo>();
        _failoverHistory = new List<FailoverEvent>();
        _currentState = NodeState.Initializing;

        InitializeNodes();
    }

    private void InitializeNodes()
    {
        lock (_lockObject)
        {
            // Primary nodeの初期化
            if (!string.IsNullOrEmpty(_config.PrimaryNode))
            {
                var primaryNode = new NodeInfo
                {
                    NodeId = "primary",
                    Address = _config.PrimaryNode,
                    State = NodeState.Active,
                    Priority = 100,
                    IsHealthy = true
                };
                _nodes["primary"] = primaryNode;
                _currentState = NodeState.Active;
                _logger.Info($"Initialized primary node: {_config.PrimaryNode}");
            }

            // Standby nodeの初期化
            if (!string.IsNullOrEmpty(_config.StandbyNode))
            {
                var standbyNode = new NodeInfo
                {
                    NodeId = "standby",
                    Address = _config.StandbyNode,
                    State = NodeState.Standby,
                    Priority = 50,
                    IsHealthy = true
                };
                _nodes["standby"] = standbyNode;
                _logger.Info($"Initialized standby node: {_config.StandbyNode}");
            }

            // Load Balancing modesの初期化
            if (_config.Nodes != null && _config.Nodes.Length > 0)
            {
                for (int i = 0; i < _config.Nodes.Length; i++)
                {
                    var node = new NodeInfo
                    {
                        NodeId = $"node{i + 1}",
                        Address = _config.Nodes[i],
                        State = NodeState.Active,
                        Priority = 100,
                        Weight = 1,
                        IsHealthy = true
                    };
                    _nodes[node.NodeId] = node;
                    _logger.Info($"Initialized load balancing node: {node.NodeId} - {node.Address}");
                }
            }
        }
    }

    public async Task StartAsync()
    {
        _logger.Info("Starting RedundancyService...");

        // ハートビートタイマーを開始
        _heartbeatTimer = new Timer(
            HeartbeatCallback,
            null,
            TimeSpan.FromMilliseconds(_config.HeartbeatInterval),
            TimeSpan.FromMilliseconds(_config.HeartbeatInterval)
        );

        _logger.Info("RedundancyService started");
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _logger.Info("Stopping RedundancyService...");

        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        _logger.Info("RedundancyService stopped");
        await Task.CompletedTask;
    }

    private void HeartbeatCallback(object? state)
    {
        try
        {
            lock (_lockObject)
            {
                var now = DateTime.UtcNow;

                foreach (var node in _nodes.Values)
                {
                    // タイムアウトチェック
                    var timeSinceLastHeartbeat = (now - node.LastHeartbeat).TotalMilliseconds;
                    if (timeSinceLastHeartbeat > _config.FailoverTimeout)
                    {
                        if (node.IsHealthy)
                        {
                            _logger.Warning($"Node {node.NodeId} heartbeat timeout ({timeSinceLastHeartbeat}ms)");
                            node.IsHealthy = false;
                            node.State = NodeState.Failed;

                            // Activeノードが失敗した場合、フェイルオーバーを実行
                            if (node.State == NodeState.Active || _currentState == NodeState.Active)
                            {
                                _ = Task.Run(() => PerformFailoverAsync($"Heartbeat timeout for {node.NodeId}"));
                            }
                        }
                    }

                    // 自ノードのハートビート更新
                    node.LastHeartbeat = now;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error in heartbeat callback: {ex.Message}");
        }
    }

    public NodeState GetCurrentState()
    {
        lock (_lockObject)
        {
            return _currentState;
        }
    }

    public Task<bool> PerformFailoverAsync(string reason)
    {
        lock (_lockObject)
        {
            try
            {
                _logger.Warning($"Performing failover: {reason}");

                // Activeノードを見つける
                var activeNode = _nodes.Values.FirstOrDefault(n => n.State == NodeState.Active);
                if (activeNode == null)
                {
                    _logger.Error("No active node found for failover");
                    return Task.FromResult(false);
                }

                // Standbyノードを見つける
                var standbyNode = _nodes.Values
                    .Where(n => n.State == NodeState.Standby && n.IsHealthy)
                    .OrderByDescending(n => n.Priority)
                    .FirstOrDefault();

                if (standbyNode == null)
                {
                    _logger.Error("No healthy standby node available for failover");
                    return Task.FromResult(false);
                }

                // フェイルオーバー実行
                activeNode.State = NodeState.Failed;
                standbyNode.State = NodeState.Active;
                _currentState = NodeState.Active;

                var failoverEvent = new FailoverEvent
                {
                    Timestamp = DateTime.UtcNow,
                    FromNodeId = activeNode.NodeId,
                    ToNodeId = standbyNode.NodeId,
                    Reason = reason,
                    Success = true
                };
                _failoverHistory.Add(failoverEvent);

                _logger.Info($"Failover successful: {activeNode.NodeId} -> {standbyNode.NodeId}");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failover failed: {ex.Message}");
                return Task.FromResult(false);
            }
        }
    }

    public NodeInfo? GetNodeInfo(string nodeId)
    {
        lock (_lockObject)
        {
            return _nodes.TryGetValue(nodeId, out var node) ? node : null;
        }
    }

    public NodeInfo[] GetAllNodes()
    {
        lock (_lockObject)
        {
            return _nodes.Values.ToArray();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _heartbeatTimer?.Dispose();
        _disposed = true;
        _logger.Info("RedundancyService disposed");
    }
}
