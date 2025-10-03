using System;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// ロードバランサーサービスインターフェース
/// </summary>
public interface ILoadBalancerService : IDisposable
{
    /// <summary>
    /// 次の利用可能なノードを選択
    /// </summary>
    NodeInfo? SelectNode();

    /// <summary>
    /// ノードへの接続を記録
    /// </summary>
    void RecordConnection(string nodeId);

    /// <summary>
    /// ノードからの切断を記録
    /// </summary>
    void RecordDisconnection(string nodeId);

    /// <summary>
    /// ノードの健全性を更新
    /// </summary>
    void UpdateNodeHealth(string nodeId, bool isHealthy);

    /// <summary>
    /// 統計情報を取得
    /// </summary>
    LoadBalancerStats GetStats();
}
