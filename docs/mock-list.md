# モック一覧 (Mock List)

このドキュメントは、Non-IP File Deliveryプロジェクトのソースコード内でモック化されているコンポーネントの完全なリストを提供します。

## 概要

本プロジェクトでは、ユニットテストにおいて**Moq**フレームワークを使用して依存関係をモック化しています。モックは外部依存を分離し、テストの信頼性と速度を向上させるために使用されます。

## モック化されているインターフェース

### 1. ILoggingService

**モック箇所:**
- `tests/NonIPFileDelivery.Tests/YARAScannerTests.cs`
- `tests/NonIPFileDelivery.Tests/RedundancyServiceTests.cs`
- `tests/NonIPFileDelivery.Tests/LoadBalancerServiceTests.cs`

**用途:**
ロギング機能をモック化し、テスト対象のクラスがログ出力に依存せずにテスト可能にします。

**使用されるテストクラス:**

#### YARAScannerTests
```csharp
private readonly Mock<ILoggingService> _mockLogger;

public YARAScannerTests()
{
    _mockLogger = new Mock<ILoggingService>();
}
```

**テスト対象:** `YARAScanner`クラス
- YARA ルールベースのマルウェアスキャナー
- ファイルやデータのセキュリティスキャン機能

**テストケース:**
- Constructor_WithValidRulesFile_ShouldSucceed
- Constructor_WithInvalidRulesFile_ShouldThrow
- ScanAsync_WithCleanData_ShouldReturnNoMatch
- ScanAsync_WithEICARTestString_ShouldDetectThreat
- ScanAsync_WithRansomwareIndicators_ShouldDetectThreat
- ScanAsync_WithSQLInjection_ShouldDetectThreat
- ScanAsync_WithEmptyData_ShouldReturnNoMatch
- ReloadRules_ShouldSucceed

#### RedundancyServiceTests
```csharp
private readonly Mock<ILoggingService> _mockLogger;

public RedundancyServiceTests()
{
    _mockLogger = new Mock<ILoggingService>();
}
```

**テスト対象:** `RedundancyService`クラス
- ノードの冗長化とフェイルオーバー機能
- ハートビート監視
- アクティブ/スタンバイ構成の管理

**テストケース:**
- StartAsync_ShouldInitializeHeartbeat
- GetAllNodes_WithPrimaryAndStandby_ShouldReturnTwoNodes
- GetAllNodes_WithLoadBalancingNodes_ShouldReturnAllNodes
- GetNodeInfo_WithValidNodeId_ShouldReturnNode
- GetNodeInfo_WithInvalidNodeId_ShouldReturnNull
- PerformFailoverAsync_WithHealthyStandby_ShouldSucceed
- StopAsync_ShouldStopHeartbeat

#### LoadBalancerServiceTests
```csharp
private readonly Mock<ILoggingService> _mockLogger;

public LoadBalancerServiceTests()
{
    _mockLogger = new Mock<ILoggingService>();
}
```

**テスト対象:** `LoadBalancerService`クラス
- ロードバランシング機能
- ラウンドロビン、重み付けラウンドロビン、最小接続数アルゴリズム
- ノードヘルスチェック

**テストケース:**
- SelectNode_WithRoundRobin_ShouldDistributeEvenly
- SelectNode_WithWeightedRoundRobin_ShouldSelectBasedOnWeight
- SelectNode_WithLeastConnections_ShouldSelectLeastBusy
- SelectNode_WithNoHealthyNodes_ShouldReturnNull
- RecordConnection_ShouldIncreaseActiveConnections
- RecordDisconnection_ShouldDecreaseActiveConnections
- UpdateNodeHealth_ShouldAffectStats
- GetStats_ShouldReturnCorrectStatistics

## モックを使用しないテストクラス

以下のテストクラスは、モックを使用せず、実際のクラスインスタンスを直接テストしています：

### SecurityInspectorTests
**テスト対象:** `SecurityInspector`クラス
- セキュリティスキャンとパターン検出
- FTPコマンドの検証
- パストラバーサル攻撃の検出

### SecureEthernetFrameTests
**テスト対象:** `SecureEthernetFrame`クラス
- 暗号化フレームの作成とシリアライゼーション
- フレームの暗号化/復号化

### CryptoEngineTests
**テスト対象:** `CryptoEngine`クラス
- AES-256-GCM暗号化/復号化
- パスワードベースの鍵導出

## モックの使用パターン

### 基本的な使用法

```csharp
// モックの作成
private readonly Mock<ILoggingService> _mockLogger;

// コンストラクタでの初期化
public TestClass()
{
    _mockLogger = new Mock<ILoggingService>();
}

// テスト対象クラスへの注入
var service = new TargetService(_mockLogger.Object);
```

### モックの利点

1. **依存性の分離**: テスト対象のクラスを外部依存から分離
2. **テストの高速化**: 実際のI/O操作やネットワーク通信を回避
3. **テストの信頼性**: 外部要因による失敗を防止
4. **振る舞いの検証**: メソッドの呼び出しやパラメータを検証可能

## 統計情報

### プロジェクト全体
- **総テストクラス数**: 6
- **モックを使用するテストクラス数**: 3
- **モック化されたインターフェース数**: 1 (`ILoggingService`)
- **総テスト数**: 20以上

### テストフレームワーク
- **テストフレームワーク**: xUnit
- **アサーションライブラリ**: FluentAssertions
- **モッキングフレームワーク**: Moq

## 参考情報

### 関連ドキュメント
- [テストREADME](../tests/README.md) - テストスイートの詳細情報
- [アーキテクチャ図](./architecture-diagram.md) - システムアーキテクチャ
- [技術仕様](./technical-specification.md) - 技術詳細

### ILoggingServiceインターフェース

ILoggingServiceは構造化ロギングを提供するインターフェースで、以下のメソッドを含みます：

- `void Info(string message)`
- `void Warning(string message)`
- `void Error(string message)`
- `void Debug(string message)`
- その他のロギングメソッド

実装クラス: `NonIPFileDelivery.Services.LoggingService` (Serilogベース)

## まとめ

本プロジェクトでは、ロギング機能のみをモック化することで、テストの複雑さを最小限に抑えながら、効果的なユニットテストを実現しています。他の依存関係（暗号化、セキュリティスキャン等）は、実装が軽量かつ副作用が少ないため、実際のインスタンスを使用してテストされています。

---

**最終更新日**: 2025-10-09  
**ドキュメントバージョン**: 1.0
