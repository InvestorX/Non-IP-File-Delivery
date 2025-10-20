using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// 冗長化サービス実装(Active-Standby構成)
/// </summary>
public class RedundancyService : IRedundancyService
{
    private readonly ILoggingService _logger;
    private readonly RedundancyConfig _config;
    private readonly INetworkService? _networkService;
    private readonly Dictionary<string, NodeInfo> _nodes;
    private readonly List<FailoverEvent> _failoverHistory;
    private NodeState _currentState;
    private Timer? _heartbeatTimer;
    private bool _disposed;
    private readonly object _lockObject = new object();
    private string _localNodeId = string.Empty;
    private string _localMacAddress = string.Empty;

    public RedundancyService(ILoggingService logger, RedundancyConfig config, INetworkService? networkService = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _networkService = networkService;
        _nodes = new Dictionary<string, NodeInfo>();
        _failoverHistory = new List<FailoverEvent>();
        _currentState = NodeState.Initializing;

        InitializeNodes();
        
        // ネットワークサービスが利用可能な場合、フレーム受信イベントを購読
        if (_networkService != null)
        {
            _networkService.FrameReceived += OnFrameReceived;
            _logger.Info("RedundancyService: NetworkService integration enabled");
        }
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
                _localNodeId = "primary";
                _localMacAddress = _config.PrimaryNode;
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
                
                // Standbyノードの場合、ローカルノードIDを設定
                if (string.IsNullOrEmpty(_localNodeId))
                {
                    _localNodeId = "standby";
                    _localMacAddress = _config.StandbyNode;
                }
                
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
            // ハートビートメッセージを送信
            Task.Run(async () =>
            {
                try
                {
                    await SendHeartbeatAsync();
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error in SendHeartbeatAsync: {ex.Message}");
                }
            });

            lock (_lockObject)
            {
                var now = DateTime.UtcNow;
                var activeNode = _nodes.Values.FirstOrDefault(n => n.State == NodeState.Active);
                var failedNodes = new List<NodeInfo>();

                // 全ノードのハートビートチェック
                foreach (var node in _nodes.Values)
                {
                    var timeSinceLastHeartbeat = (now - node.LastHeartbeat).TotalMilliseconds;
                    
                    if (timeSinceLastHeartbeat > _config.FailoverTimeout)
                    {
                        // タイムアウト検出
                        if (node.IsHealthy)
                        {
                            _logger.Warning($"Node {node.NodeId} heartbeat timeout ({timeSinceLastHeartbeat:F0}ms > {_config.FailoverTimeout}ms)");
                            node.IsHealthy = false;
                            
                            // Activeノードの場合は即座にFailedに
                            if (node.State == NodeState.Active)
                            {
                                node.State = NodeState.Failed;
                                failedNodes.Add(node);
                                _logger.Error($"Active node {node.NodeId} has failed - triggering failover");
                            }
                        }
                    }
                    else
                    {
                        // ハートビートが正常
                        if (!node.IsHealthy && node.State == NodeState.Failed)
                        {
                            // ノード回復検出
                            _logger.Info($"Node {node.NodeId} has recovered (heartbeat received)");
                            node.IsHealthy = true;
                            node.RecoveryTime = now;
                            
                            // プライマリノードが回復した場合、自動フェイルバックを検討
                            if (_config.EnableAutoFailback && node.NodeId == "primary" && activeNode?.NodeId != "primary")
                            {
                                CheckAndPerformFailback(node, now);
                            }
                        }
                    }
                }

                // Activeノードが失敗した場合、フェイルオーバー実行
                if (activeNode != null && failedNodes.Any(n => n.NodeId == activeNode.NodeId))
                {
                    _ = Task.Run(() => PerformFailoverAsync($"Heartbeat timeout for active node {activeNode.NodeId}"));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error in heartbeat callback: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 自動フェイルバックの判定と実行
    /// </summary>
    private void CheckAndPerformFailback(NodeInfo recoveredNode, DateTime now)
    {
        try
        {
            if (recoveredNode.RecoveryTime == null)
            {
                return;
            }

            var timeSinceRecovery = (now - recoveredNode.RecoveryTime.Value).TotalMilliseconds;
            
            // 回復後の安定期間をチェック
            if (timeSinceRecovery >= _config.FailbackDelay)
            {
                _logger.Info($"Primary node {recoveredNode.NodeId} has been stable for {timeSinceRecovery:F0}ms - performing automatic failback");
                
                // フェイルバック実行
                var currentActive = _nodes.Values.FirstOrDefault(n => n.State == NodeState.Active);
                if (currentActive != null && currentActive.NodeId != recoveredNode.NodeId)
                {
                    // 現在のActiveをStandbyに
                    currentActive.State = NodeState.Standby;
                    
                    // 回復したプライマリをActiveに
                    recoveredNode.State = NodeState.Active;
                    _currentState = NodeState.Active;
                    recoveredNode.RecoveryTime = null; // リセット
                    
                    var failbackEvent = new FailoverEvent
                    {
                        Timestamp = now,
                        FromNodeId = currentActive.NodeId,
                        ToNodeId = recoveredNode.NodeId,
                        Reason = "Automatic failback to recovered primary node",
                        Success = true
                    };
                    _failoverHistory.Add(failbackEvent);
                    
                    _logger.Info($"Automatic failback completed: {currentActive.NodeId} → {recoveredNode.NodeId}");
                }
            }
            else
            {
                var remainingTime = _config.FailbackDelay - timeSinceRecovery;
                _logger.Debug($"Primary node {recoveredNode.NodeId} recovering - {remainingTime:F0}ms until automatic failback");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error in automatic failback: {ex.Message}", ex);
        }
    }

    public NodeState GetCurrentState()
    {
        lock (_lockObject)
        {
            return _currentState;
        }
    }

    public Task RecordHeartbeatAsync(string nodeId, NodeState state, Dictionary<string, object>? metadata = null)
    {
        try
        {
            lock (_lockObject)
            {
                if (!_nodes.TryGetValue(nodeId, out var node))
                {
                    _logger.Warning($"Received heartbeat from unknown node: {nodeId}");
                    
                    // 新しいノードとして登録
                    node = new NodeInfo
                    {
                        NodeId = nodeId,
                        State = state,
                        Priority = 100,
                        Weight = 1,
                        IsHealthy = true,
                        LastHeartbeat = DateTime.UtcNow
                    };
                    
                    // メタデータがあれば適用
                    if (metadata != null)
                    {
                        foreach (var kvp in metadata)
                        {
                            if (kvp.Key == "Priority" && kvp.Value is int priority)
                            {
                                node.Priority = priority;
                            }
                            else if (kvp.Key == "Weight" && kvp.Value is int weight)
                            {
                                node.Weight = weight;
                            }
                            else if (kvp.Key == "ActiveConnections" && kvp.Value is int activeConnections)
                            {
                                node.ActiveConnections = activeConnections;
                            }
                        }
                    }
                    
                    _nodes[nodeId] = node;
                    _logger.Info($"Registered new node from heartbeat: {nodeId}, Priority={node.Priority}, Weight={node.Weight}");
                }
                else
                {
                    // 既存ノードのハートビート更新
                    var wasUnhealthy = !node.IsHealthy;
                    
                    node.LastHeartbeat = DateTime.UtcNow;
                    node.State = state;
                    node.IsHealthy = true;
                    
                    // メタデータ更新
                    if (metadata != null)
                    {
                        foreach (var kvp in metadata)
                        {
                            if (kvp.Key == "Priority" && kvp.Value is int priority)
                            {
                                node.Priority = priority;
                            }
                            else if (kvp.Key == "Weight" && kvp.Value is int weight)
                            {
                                node.Weight = weight;
                            }
                            else if (kvp.Key == "ActiveConnections" && kvp.Value is int activeConnections)
                            {
                                node.ActiveConnections = activeConnections;
                            }
                        }
                    }
                    
                    if (wasUnhealthy)
                    {
                        _logger.Info($"Node {nodeId} recovered from unhealthy state");
                        node.RecoveryTime = DateTime.UtcNow;
                        
                        // プライマリノードが回復した場合、自動フェイルバックを検討
                        if (_config.EnableAutoFailback && node.NodeId == "primary" && _config.PrimaryNode != null)
                        {
                            var currentActive = _nodes.Values.FirstOrDefault(n => n.State == NodeState.Active);
                            if (currentActive != null && currentActive.NodeId != "primary")
                            {
                                _logger.Info($"Primary node {nodeId} recovered - automatic failback will be considered after {_config.FailbackDelay}ms stability period");
                            }
                        }
                    }
                    
                    _logger.Debug($"Heartbeat recorded for node {nodeId}: State={state}, Healthy={node.IsHealthy}");
                }
            }
            
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error recording heartbeat for node {nodeId}: {ex.Message}", ex);
            throw;
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
                activeNode.IsHealthy = false;
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
        
        // ネットワークサービスのイベント購読を解除
        if (_networkService != null)
        {
            _networkService.FrameReceived -= OnFrameReceived;
        }
        
        _disposed = true;
        _logger.Info("RedundancyService disposed");
    }

    #region ノード間通信機能

    /// <summary>
    /// ハートビートメッセージを送信
    /// </summary>
    private async Task SendHeartbeatAsync()
    {
        if (_networkService == null || !_networkService.IsInterfaceReady)
        {
            _logger.Debug("NetworkService not available for heartbeat transmission");
            return;
        }

        try
        {
            NodeInfo? localNode;
            lock (_lockObject)
            {
                localNode = _nodes.Values.FirstOrDefault(n => n.NodeId == _localNodeId);
            }

            if (localNode == null)
            {
                _logger.Warning($"Local node '{_localNodeId}' not found in node list");
                return;
            }

            // ハートビートメッセージを作成
            var heartbeat = new HeartbeatMessage
            {
                NodeId = _localNodeId,
                SourceMac = _localMacAddress,
                Timestamp = DateTime.UtcNow,
                State = localNode.State,
                Priority = localNode.Priority,
                ActiveConnections = localNode.ActiveConnections,
                MemoryUsageMB = GC.GetTotalMemory(false) / (1024 * 1024),
                CpuUsagePercent = 0 // TODO: 実装時にCPU使用率を取得
            };

            // シリアライズして送信
            var data = heartbeat.Serialize();
            
            // ブロードキャストまたは他ノードへ送信
            var otherNodes = _nodes.Values.Where(n => n.NodeId != _localNodeId).ToList();
            foreach (var node in otherNodes)
            {
                var success = await _networkService.SendFrame(data, node.Address);
                if (success)
                {
                    _logger.Debug($"Heartbeat sent to {node.NodeId} ({node.Address})");
                }
                else
                {
                    _logger.Warning($"Failed to send heartbeat to {node.NodeId} ({node.Address})");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error sending heartbeat: {ex.Message}");
        }
    }

    /// <summary>
    /// フレーム受信イベントハンドラ（ハートビート受信処理）
    /// </summary>
    private void OnFrameReceived(object? sender, FrameReceivedEventArgs e)
    {
        // ハートビートメッセージかどうかを確認
        if (!HeartbeatMessage.IsHeartbeatMessage(e.Data))
        {
            return;
        }

        // ハートビートメッセージをデシリアライズ
        var heartbeat = HeartbeatMessage.Deserialize(e.Data);
        if (heartbeat == null)
        {
            _logger.Warning("Failed to deserialize heartbeat message");
            return;
        }

        // 自分自身からのメッセージは無視
        if (heartbeat.NodeId == _localNodeId)
        {
            return;
        }

        _logger.Debug($"Received heartbeat from {heartbeat.NodeId} (State: {heartbeat.State})");

        // ハートビート情報をメタデータに変換
        var metadata = new Dictionary<string, object>
        {
            { "Priority", heartbeat.Priority },
            { "ActiveConnections", heartbeat.ActiveConnections },
            { "MemoryUsageMB", heartbeat.MemoryUsageMB },
            { "CpuUsagePercent", heartbeat.CpuUsagePercent }
        };

        // 非同期で記録処理を実行
        Task.Run(async () =>
        {
            try
            {
                await RecordHeartbeatAsync(heartbeat.NodeId, heartbeat.State, metadata);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error recording heartbeat from {heartbeat.NodeId}: {ex.Message}");
            }
        });
    }

    #endregion
}
