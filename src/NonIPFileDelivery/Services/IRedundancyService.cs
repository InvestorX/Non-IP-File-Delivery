using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// 冗長化サービスインターフェース
/// </summary>
public interface IRedundancyService : IDisposable
{
    /// <summary>
    /// サービス開始
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// サービス停止
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// 現在のノード状態を取得
    /// </summary>
    NodeState GetCurrentState();

    /// <summary>
    /// フェイルオーバーを実行
    /// </summary>
    Task<bool> PerformFailoverAsync(string reason);

    /// <summary>
    /// 外部ノードからのハートビートを記録
    /// </summary>
    /// <param name="nodeId">ノードID</param>
    /// <param name="state">ノード状態</param>
    /// <param name="metadata">追加のメタデータ</param>
    Task RecordHeartbeatAsync(string nodeId, NodeState state, Dictionary<string, object>? metadata = null);

    /// <summary>
    /// ノード情報を取得
    /// </summary>
    NodeInfo? GetNodeInfo(string nodeId);

    /// <summary>
    /// すべてのノード情報を取得
    /// </summary>
    NodeInfo[] GetAllNodes();
}
