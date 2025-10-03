# YARA統合、冗長化、負荷分散機能ガイド

このドキュメントでは、Non-IP File Delivery Systemに実装された3つの主要機能の使用方法について説明します。

## 目次
1. [YARA統合](#yara統合)
2. [冗長化機能](#冗長化機能)
3. [負荷分散機能](#負荷分散機能)

---

## YARA統合

### 概要
YARAは、マルウェアやセキュリティ脅威を検出するためのパターンマッチングツールです。本システムでは、dnYara 2.1.0を使用して完全に統合されています。

### 前提条件
- ネイティブYARAライブラリ（libyara）のインストールが必要です
  - Windows: https://github.com/VirusTotal/yara/releases からダウンロード
  - Linux: `sudo apt-get install libyara-dev` または `yum install yara-devel`

### 使用方法

#### 1. YARAルールの準備
YARAルールファイル（`.yar`）を`yara_rules`ディレクトリに配置します。

```yara
rule EICAR_Test_File
{
    meta:
        description = "EICAR test file"
        severity = "test"
    
    strings:
        $eicar = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"
    
    condition:
        $eicar
}
```

#### 2. YARAScannerの初期化

```csharp
using NonIPFileDelivery.Services;

// ロギングサービスとルールパスを指定
var logger = new LoggingService();
var rulesPath = "yara_rules/malware.yar";

// YARAScannerを作成
using var scanner = new YARAScanner(logger, rulesPath);
```

#### 3. データのスキャン

```csharp
// スキャン対象データ
var data = File.ReadAllBytes("suspicious_file.exe");

// スキャン実行（タイムアウト：5000ms）
var result = await scanner.ScanAsync(data, timeoutMs: 5000);

// 結果の確認
if (result.IsMatch)
{
    Console.WriteLine($"脅威検出: {result.RuleName}");
    Console.WriteLine($"マッチ数: {result.MatchedStrings}");
    Console.WriteLine($"詳細: {result.Details}");
}
else
{
    Console.WriteLine("脅威は検出されませんでした");
}
```

#### 4. ルールのリロード

```csharp
// YARAルールを更新した後にリロード
scanner.ReloadRules();
```

### 組み込みルール
システムには以下のルールが含まれています：
- **EICAR_Test_File**: EICARテストファイル検出
- **Suspicious_Executable**: 疑わしい実行可能ファイル検出
- **Ransomware_Indicators**: ランサムウェアの兆候検出
- **SQL_Injection_Patterns**: SQLインジェクション検出
- **Webshell_PHP**: PHPウェブシェル検出

---

## 冗長化機能

### 概要
冗長化機能は、システムの高可用性を実現するためのActive-Standby構成をサポートします。

### アーキテクチャ
```
┌─────────────┐     Heartbeat     ┌─────────────┐
│   Primary   │ ←───────────────→ │   Standby   │
│   (Active)  │                   │  (Standby)  │
└─────────────┘                   └─────────────┘
       │                                  │
       │ Failover on failure             │
       └──────────────────────────────────┘
```

### 設定例

#### config.ini
```ini
[General]
Mode=ActiveStandby

[Redundancy]
HeartbeatInterval=1000      # ハートビート間隔（ミリ秒）
FailoverTimeout=5000        # フェイルオーバータイムアウト（ミリ秒）
DataSyncMode=realtime       # データ同期モード
PrimaryNode=192.168.1.10    # プライマリノードのアドレス
StandbyNode=192.168.1.11    # スタンバイノードのアドレス
VirtualIP=192.168.1.100     # 仮想IPアドレス
```

### 使用方法

#### 1. RedundancyServiceの初期化

```csharp
using NonIPFileDelivery.Services;
using NonIPFileDelivery.Models;

// 設定の作成
var config = new RedundancyConfig
{
    HeartbeatInterval = 1000,
    FailoverTimeout = 5000,
    PrimaryNode = "192.168.1.10",
    StandbyNode = "192.168.1.11"
};

// RedundancyServiceの作成
using var service = new RedundancyService(logger, config);
```

#### 2. サービスの開始

```csharp
// サービス開始（ハートビート監視開始）
await service.StartAsync();

// 現在の状態確認
var state = service.GetCurrentState();
Console.WriteLine($"Current state: {state}");
```

#### 3. ノード情報の取得

```csharp
// すべてのノード情報を取得
var nodes = service.GetAllNodes();
foreach (var node in nodes)
{
    Console.WriteLine($"Node: {node.NodeId}");
    Console.WriteLine($"  Address: {node.Address}");
    Console.WriteLine($"  State: {node.State}");
    Console.WriteLine($"  Healthy: {node.IsHealthy}");
}

// 特定のノード情報を取得
var primaryNode = service.GetNodeInfo("primary");
```

#### 4. 手動フェイルオーバー

```csharp
// 手動でフェイルオーバーを実行
var success = await service.PerformFailoverAsync("Manual failover test");
if (success)
{
    Console.WriteLine("Failover completed successfully");
}
```

#### 5. サービスの停止

```csharp
// サービス停止
await service.StopAsync();
```

### フェイルオーバーシナリオ
- **自動フェイルオーバー**: プライマリノードのハートビートタイムアウト時
- **手動フェイルオーバー**: 計画メンテナンス時
- **フェイルバック**: プライマリノード復旧後

---

## 負荷分散機能

### 概要
負荷分散機能は、複数のノード間でトラフィックを分散し、システムのスケーラビリティを向上させます。

### サポートされているアルゴリズム

1. **Round Robin（ラウンドロビン）**
   - 各ノードに順番にリクエストを割り当て
   - 最もシンプルで公平な分散

2. **Weighted Round Robin（重み付きラウンドロビン）**
   - ノードの重みに基づいて分散
   - 性能の異なるノードに対応

3. **Least Connections（最小接続数）**
   - 現在の接続数が最も少ないノードを選択
   - 長時間接続に適している

4. **Random（ランダム）**
   - ランダムにノードを選択
   - 単純で予測不可能

### 設定例

#### config.ini
```ini
[General]
Mode=LoadBalancing

[Redundancy]
Nodes=192.168.1.10,192.168.1.11,192.168.1.12
Algorithm=RoundRobin     # RoundRobin | WeightedRoundRobin | LeastConnections | Random
```

### 使用方法

#### 1. LoadBalancerServiceの初期化

```csharp
using NonIPFileDelivery.Services;
using NonIPFileDelivery.Models;

// ノードの定義
var nodes = new[]
{
    new NodeInfo
    {
        NodeId = "node1",
        Address = "192.168.1.10",
        State = NodeState.Active,
        IsHealthy = true,
        Weight = 1
    },
    new NodeInfo
    {
        NodeId = "node2",
        Address = "192.168.1.11",
        State = NodeState.Active,
        IsHealthy = true,
        Weight = 2  // より高性能なノードには高い重みを設定
    },
    new NodeInfo
    {
        NodeId = "node3",
        Address = "192.168.1.12",
        State = NodeState.Active,
        IsHealthy = true,
        Weight = 1
    }
};

// LoadBalancerServiceの作成
using var loadBalancer = new LoadBalancerService(
    logger,
    LoadBalancingAlgorithm.WeightedRoundRobin,
    nodes
);
```

#### 2. ノードの選択

```csharp
// 次の利用可能なノードを選択
var selectedNode = loadBalancer.SelectNode();
if (selectedNode != null)
{
    Console.WriteLine($"Selected node: {selectedNode.NodeId}");
    Console.WriteLine($"  Address: {selectedNode.Address}");
    
    // このノードを使用して処理を実行
    // ...
}
```

#### 3. 接続の追跡

```csharp
// 接続開始時
loadBalancer.RecordConnection(selectedNode.NodeId);

try
{
    // 処理を実行
    // ...
}
finally
{
    // 接続終了時
    loadBalancer.RecordDisconnection(selectedNode.NodeId);
}
```

#### 4. ヘルスチェック

```csharp
// ノードの健全性を更新
loadBalancer.UpdateNodeHealth("node1", isHealthy: true);
loadBalancer.UpdateNodeHealth("node2", isHealthy: false);  // node2に問題が発生

// 統計情報を取得
var stats = loadBalancer.GetStats();
Console.WriteLine($"Total requests: {stats.TotalRequests}");
Console.WriteLine($"Total failures: {stats.TotalFailures}");
Console.WriteLine($"Active nodes: {stats.ActiveNodes}");
```

### パフォーマンスチューニング

#### 重み付きラウンドロビンの調整
```csharp
// 高性能ノードには高い重みを設定
node1.Weight = 1;   // 標準性能
node2.Weight = 3;   // 3倍の性能
node3.Weight = 2;   // 2倍の性能
```

#### ヘルスチェック間隔
```csharp
// 定期的にノードの健全性をチェック
var timer = new Timer(async _ =>
{
    foreach (var node in nodes)
    {
        var isHealthy = await CheckNodeHealth(node);
        loadBalancer.UpdateNodeHealth(node.NodeId, isHealthy);
    }
}, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
```

---

## 統合使用例

### YARAスキャン付き負荷分散システム

```csharp
// 初期化
var yaraScanner = new YARAScanner(logger, "yara_rules/malware.yar");
var loadBalancer = new LoadBalancerService(logger, LoadBalancingAlgorithm.LeastConnections, nodes);

// リクエスト処理
async Task<bool> ProcessRequest(byte[] data)
{
    // 1. YARAスキャン
    var scanResult = await yaraScanner.ScanAsync(data);
    if (scanResult.IsMatch)
    {
        logger.Warning($"Threat detected: {scanResult.RuleName}");
        return false;  // 脅威検出、処理中止
    }
    
    // 2. ノード選択
    var node = loadBalancer.SelectNode();
    if (node == null)
    {
        logger.Error("No available node");
        return false;
    }
    
    // 3. 接続記録と処理
    loadBalancer.RecordConnection(node.NodeId);
    try
    {
        // データを選択されたノードに転送
        await SendToNode(node, data);
        return true;
    }
    finally
    {
        loadBalancer.RecordDisconnection(node.NodeId);
    }
}
```

### Active-Standby with YARA

```csharp
// 冗長化サービスの初期化
var redundancyService = new RedundancyService(logger, config);
await redundancyService.StartAsync();

// YARAスキャンを実行する前に、現在のノードがActiveか確認
if (redundancyService.GetCurrentState() == NodeState.Active)
{
    var scanResult = await yaraScanner.ScanAsync(data);
    // 処理を続行
}
else
{
    // Standbyモードでは処理しない
    logger.Info("This node is in standby mode");
}
```

---

## トラブルシューティング

### YARA関連

**問題**: `DllNotFoundException: Unable to load DLL 'yara'`
- **解決策**: ネイティブYARAライブラリをインストールし、システムPATHに追加してください

**問題**: `CompilationException` during rule loading
- **解決策**: YARAルールの構文を確認してください。`yara -c rulefile.yar`でルールを検証できます

### 冗長化関連

**問題**: フェイルオーバーが発生しない
- **解決策**: 
  - HeartbeatIntervalとFailoverTimeoutの設定を確認
  - ネットワーク接続を確認
  - ログで「heartbeat timeout」メッセージを確認

**問題**: 頻繁にフェイルオーバーが発生する
- **解決策**: FailoverTimeoutを増やす（デフォルト5000ms）

### 負荷分散関連

**問題**: 特定のノードにトラフィックが集中する
- **解決策**: 
  - アルゴリズムを確認（LeastConnectionsまたはRoundRobinを推奨）
  - ノードの重みを調整（WeightedRoundRobinの場合）

**問題**: すべてのリクエストが失敗する
- **解決策**:
  - `GetStats()`で統計情報を確認
  - すべてのノードが健全か確認
  - ノードの接続性を確認

---

## まとめ

これらの機能を組み合わせることで、以下のような高度なシステムを構築できます：

- **セキュアで高可用性**: YARAスキャンとActive-Standby冗長化
- **スケーラブルで安全**: 負荷分散とYARAスキャン
- **完全な高可用性システム**: 冗長化と負荷分散の組み合わせ

詳細な実装例とAPIリファレンスについては、プロジェクトのソースコードとテストケースを参照してください。
