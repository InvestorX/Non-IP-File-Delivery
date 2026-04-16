# NonIPFileDelivery (A側) と NonIPFileDeliveryB (B側) のアーキテクチャ比較

**作成日**: 2026年4月16日
**バージョン**: 1.0

---

## 📋 概要

Non-IP File Deliveryシステムは、2つの独立したアプリケーション（A側とB側）で構成されています。
このドキュメントでは、それぞれの役割、実装の違い、設定方法について詳しく解説します。

---

## 🏗️ システム全体構成

```
┌──────────────┐         ┌────────────────────┐         ┌────────────────────┐         ┌──────────────┐
│ Windows端末A │ <---①--->│ NonIPFileDelivery  │ <---②--->│NonIPFileDeliveryB │ <---③--->│ Windows端末B │
│ (クライアント)│  TCP/IP  │      (A側)         │Raw Ether-│      (B側)         │  TCP/IP  │  (サーバー)  │
│              │          │   送信側プロキシ    │   net   │  受信側プロキシ    │          │              │
└──────────────┘          └────────────────────┘          └────────────────────┘          └──────────────┘
     FTP/SFTP/                     暗号化                      復号化                       FTP/SFTP/
    PostgreSQL                   セキュリティ検閲            セキュリティ検閲              PostgreSQL
    クライアント                  フレーム生成                フレーム解析                   サーバー
```

### データフローの詳細

#### ① Windows端末A → NonIPFileDelivery (A側)
- **プロトコル**: TCP/IP（FTP port 21, SFTP port 22, PostgreSQL port 5432）
- **方向**: クライアント → プロキシ
- **処理**:
  1. クライアント接続受付
  2. プロトコル解析（FTP/SFTP/PostgreSQL）
  3. セキュリティ検閲（マルウェアスキャン、SQLインジェクション検出等）
  4. セッション管理（8文字セッションID生成）

#### ② NonIPFileDelivery (A側) → NonIPFileDeliveryB (B側)
- **プロトコル**: 独自フレーム形式（Raw Ethernet）
- **EtherType**: 0x88B5（カスタム）
- **方向**: 送信側 → 受信側
- **処理**:
  1. TCPストリームを独自フレームに変換
  2. AES-256-GCM暗号化（オプション）
  3. フラグメンテーション（大きなデータを分割）
  4. QoS優先度制御
  5. ACK/NAK再送制御

#### ③ NonIPFileDeliveryB (B側) → Windows端末B
- **プロトコル**: TCP/IP（元のプロトコル形式に復元）
- **方向**: プロキシ → サーバー
- **処理**:
  1. Raw Ethernetフレーム受信
  2. AES-256-GCM復号（オプション）
  3. フラグメント再構築
  4. セキュリティ検閲（機密データ検出等）
  5. TCP/IPストリームに再構築してサーバーへ転送

---

## 🔄 A側とB側の役割の違い

| 項目 | NonIPFileDelivery (A側) | NonIPFileDeliveryB (B側) |
|------|-------------------------|--------------------------|
| **主な役割** | TCP/IPをRaw Ethernetに変換 | Raw EthernetをTCP/IPに変換 |
| **ネットワーク接続** | Windows端末Aのネットワーク | Windows端末Bのネットワーク |
| **インターフェース** | `eth0` (クライアント側) | `eth1` (サーバー側) |
| **リスニング** | TCP/IPポート（21, 22, 5432）をリッスン | Raw Ethernetフレームを監視 |
| **送信先** | B側のMACアドレス（Raw Ethernet） | 実サーバーのIPアドレス（TCP/IP） |
| **セッション開始** | クライアント接続で開始 | Raw Ethernetフレーム受信で開始 |
| **設定ファイル** | `appsettings.json` | `appsettings.b.json` |
| **プログラムエントリ** | `Program.cs` | `Program.cs` (別プロジェクト) |

---

## 📦 プロジェクト構造の比較

### NonIPFileDelivery (A側)

```
src/NonIPFileDelivery/
├── Program.cs                          ← エントリーポイント
├── Protocols/
│   ├── FtpProxy.cs                     ← FTPプロキシ（522行）
│   ├── SftpProxy.cs                    ← SFTPプロキシ（674行）
│   └── PostgreSqlProxy.cs              ← PostgreSQLプロキシ（531行）
├── Core/
│   ├── RawEthernetTransceiver.cs       ← Raw Ethernet送受信
│   ├── SecureEthernetTransceiver.cs    ← 暗号化通信
│   └── IRawEthernetTransceiver.cs      ← インターフェース
├── Security/
│   ├── SecurityInspector.cs            ← セキュリティ検閲
│   └── CryptoEngine.cs                 ← 暗号化エンジン
├── Services/
│   ├── NetworkService.cs               ← ネットワーク管理
│   ├── QoSService.cs                   ← QoS制御
│   ├── SessionManager.cs               ← セッション管理
│   └── FragmentationService.cs         ← フラグメント処理
└── appsettings.json                    ← 設定ファイル
```

### NonIPFileDeliveryB (B側)

```
src/NonIPFileDeliveryB/
├── Program.cs                          ← エントリーポイント
├── Protocols/
│   ├── FtpProxyB.cs                    ← FTPプロキシB（246行）
│   ├── SftpProxyB.cs                   ← SFTPプロキシB（213行）
│   └── PostgreSqlProxyB.cs             ← PostgreSQLプロキシB（261行）
├── (共通コンポーネントはA側から参照)
│   ├── Core/                           ← A側のCoreを参照
│   ├── Security/                       ← A側のSecurityを参照
│   └── Models/                         ← A側のModelsを参照
└── appsettings.b.json                  ← 設定ファイル（B側専用）
```

**注意**: B側は、A側の多くのコンポーネント（Core, Security, Models等）を再利用しています。
B側固有の実装は主にProtocolsディレクトリ内のプロキシクラスのみです。

---

## ⚙️ 設定ファイルの違い

### appsettings.json (A側)

```json
{
  "network": {
    "interfaceName": "eth0",                    // A側ネットワークインターフェース
    "remoteMacAddress": "00:11:22:33:44:55",    // B側のMACアドレス
    "etherType": "0x88B5",                      // カスタムEtherType
    "useSecureTransceiver": false               // 暗号化の有効/無効
  },
  "security": {
    "yaraRulesPath": "rules/*.yar",
    "enableDeepInspection": true,
    "scanTimeout": 5000,
    "cryptoPassword": "__SET_VIA_ENV_OR_SECRET_MANAGER__"
  },
  "protocols": {
    "ftp": {
      "enabled": true,
      "listenPort": 21,                         // A側でリッスンするポート
      "targetHost": "192.168.1.100",            // B側に転送（仮想的な値）
      "targetPort": 21
    },
    "sftp": {
      "enabled": true,
      "listenPort": 22,
      "targetHost": "192.168.1.100",
      "targetPort": 22
    },
    "postgresql": {
      "enabled": true,
      "listenPort": 5432,
      "targetHost": "192.168.1.100",
      "targetPort": 5432
    }
  },
  "logging": {
    "minimumLevel": "Debug",
    "retentionDays": 30
  }
}
```

### appsettings.b.json (B側)

```json
{
  "Network": {
    "InterfaceName": "eth1",                    // B側ネットワークインターフェース
    "RemoteMacAddress": "AA:BB:CC:DD:EE:FF",    // A側のMACアドレス
    "CustomEtherType": "0x88B5"
  },
  "Security": {
    "YaraRulesPath": "rules/*.yar",
    "EnableDeepInspection": true,
    "ScanTimeout": 5000
  },
  "Protocols": {
    "Ftp": {
      "Enabled": true,
      "TargetHost": "192.168.2.100",            // 実際のFTPサーバー
      "TargetPort": 21
    },
    "Sftp": {
      "Enabled": true,
      "TargetHost": "192.168.2.101",            // 実際のSFTPサーバー
      "TargetPort": 22
    },
    "Postgresql": {
      "Enabled": true,
      "TargetHost": "192.168.2.102",            // 実際のPostgreSQLサーバー
      "TargetPort": 5432
    }
  },
  "Logging": {
    "MinimumLevel": "Debug",
    "RetentionDays": 30
  }
}
```

### 設定の主な違い

| 項目 | A側 | B側 | 説明 |
|------|-----|-----|------|
| **interfaceName** | `eth0` | `eth1` | 使用するネットワークインターフェース |
| **remoteMacAddress** | B側のMAC | A側のMAC | 送信先のMACアドレス |
| **listenPort** | ✅ 設定 | ❌ 不要 | TCP/IPリスニングポート（A側のみ） |
| **targetHost** | 仮想値 | 実サーバーIP | 転送先（B側は実際のサーバーIPを設定） |

---

## 🔐 暗号化キーの管理

A側とB側は**同じ暗号化キー**を使用する必要があります。

### 暗号化キーの優先順位

1. **環境変数**: `NONIP_CRYPTO_KEY`（Base64エンコード済み）
2. **ファイル**: `crypto.key`（Base64エンコード済み）
3. **エラー**: キーが見つからない場合はエラー終了

### 暗号化キーの生成方法

```bash
# 32バイト（256ビット）のランダムキーを生成
openssl rand -base64 32 > crypto.key

# または環境変数に設定
export NONIP_CRYPTO_KEY=$(openssl rand -base64 32)
```

### A側とB側での設定例

#### A側（Linux/Windows）
```bash
# Linuxの場合
export NONIP_CRYPTO_KEY="your-base64-encoded-key-here"
./NonIPFileDelivery

# Windowsの場合（PowerShell）
$env:NONIP_CRYPTO_KEY="your-base64-encoded-key-here"
.\NonIPFileDelivery.exe
```

#### B側（Linux/Windows）
```bash
# 同じキーを設定
export NONIP_CRYPTO_KEY="your-base64-encoded-key-here"
./NonIPFileDeliveryB

# またはcrypto.keyファイルを配置
echo "your-base64-encoded-key-here" > crypto.key
./NonIPFileDeliveryB
```

---

## 🚀 起動順序と依存関係

### 推奨起動順序

1. **B側を先に起動** ← Raw Ethernet受信を開始
2. **A側を起動** ← TCP/IPリスニングを開始
3. **クライアントから接続** ← 通信開始

### なぜB側を先に起動するのか？

- A側がクライアント接続を受け付けると、すぐにB側へRaw Ethernetフレームを送信します
- B側が起動していないと、送信されたフレームが失われる可能性があります
- B側を先に起動することで、A側からの最初のフレームを確実に受信できます

### 起動確認

#### A側のログ出力例
```
[10:30:15 INF] ========================================
[10:30:15 INF] Non-IP File Delivery System Starting...
[10:30:15 INF] Version: 1.0.0 - PostgreSQL/SFTP Support
[10:30:15 INF] Log Level: Debug
[10:30:15 INF] ========================================
[10:30:15 INF] Crypto key loaded from environment variable
[10:30:15 INF] FTP Proxy enabled
[10:30:15 INF] SFTP Proxy enabled
[10:30:15 INF] PostgreSQL Proxy enabled
[10:30:15 INF] All services started successfully
[10:30:15 INF] Active Protocols: FTP=True, SFTP=True, PostgreSQL=True
[10:30:15 INF] Press Ctrl+C to shutdown...
```

#### B側のログ出力例
```
[10:30:10 INF] === Non-IP File Delivery B (Receiver) Starting ===
[10:30:10 INF] Log Level: Debug
[10:30:10 INF] Configuration: Interface=eth1
[10:30:10 INF] Crypto key loaded from environment variable
[10:30:10 INF] SecureEthernetTransceiver initialized in receiver mode
[10:30:10 INF] SecurityInspector initialized
[10:30:10 INF] Protocol proxies initialized
[10:30:10 INF] All protocol proxies started
[10:30:10 INF] === Non-IP File Delivery B is running ===
[10:30:10 INF] Press Ctrl+C to stop
```

---

## 🐛 トラブルシューティング

### Q1: A側が「Network interface not found」エラーを出す

**原因**: `interfaceName`の設定が間違っている

**解決方法**:
```bash
# Linuxの場合
ip link show              # 利用可能なインターフェースを確認
# または
ifconfig                  # インターフェース一覧を表示

# Windowsの場合
ipconfig /all             # インターフェース一覧を表示
```

設定ファイルの`interfaceName`を正しい値に修正してください。

### Q2: B側が「Crypto key not found」エラーを出す

**原因**: 暗号化キーが設定されていない

**解決方法**:
```bash
# 環境変数を設定
export NONIP_CRYPTO_KEY=$(openssl rand -base64 32)

# またはcrypto.keyファイルを作成
openssl rand -base64 32 > crypto.key
```

### Q3: A側とB側の通信が確立しない

**原因**: MACアドレスの設定ミス、またはネットワーク分離

**確認ポイント**:
1. A側の`remoteMacAddress`がB側のeth1のMACアドレスと一致しているか
2. B側の`RemoteMacAddress`がA側のeth0のMACアドレスと一致しているか
3. A側とB側が同じ物理ネットワーク上にあるか（L2接続）
4. ファイアウォールがRaw Ethernetをブロックしていないか

**MACアドレスの確認**:
```bash
# Linuxの場合
ip link show eth0         # eth0のMACアドレスを確認
ip link show eth1         # eth1のMACアドレスを確認

# Windowsの場合
ipconfig /all             # MACアドレス（物理アドレス）を確認
```

### Q4: セキュリティスキャンでファイル転送が失敗する

**原因**: YARAルールが厳しすぎる、またはYARAライブラリが見つからない

**解決方法**:
1. YARAルールファイルの確認: `rules/*.yar`が存在するか
2. YARAライブラリのインストール確認
3. `enableDeepInspection`を`false`に設定して動作確認
4. ログで「Blocked malicious」というメッセージを確認

---

## 📊 パフォーマンスとスケーラビリティ

### A側のリソース消費

| 項目 | 推奨値 | 説明 |
|------|--------|------|
| CPU | 2コア以上 | プロトコル解析とセキュリティスキャン |
| メモリ | 4GB以上 | セッション管理とフレームバッファ |
| ネットワーク | 1Gbps以上 | Raw Ethernet送信帯域 |

### B側のリソース消費

| 項目 | 推奨値 | 説明 |
|------|--------|------|
| CPU | 2コア以上 | フレーム再構築とセキュリティスキャン |
| メモリ | 4GB以上 | フラグメント再構築バッファ |
| ネットワーク | 1Gbps以上 | Raw Ethernet受信とTCP/IP送信 |

### スケーラビリティ

- **同時接続数**: 最大100セッション（設定可能）
- **スループット**: 最大2Gbps（ハードウェア依存）
- **レイテンシ**: <10ms（目標値）

---

## 📚 関連ドキュメント

1. **PROTOCOL_IMPLEMENTATION_STATUS.md** - プロトコル実装状況詳細
2. **README.md** - プロジェクト概要と使用方法
3. **IMPLEMENTATION_STATUS_CHART.md** - 実装状況ビジュアルチャート

---

## 🔄 更新履歴

| 日付 | バージョン | 変更内容 |
|------|-----------|---------|
| 2026-04-16 | 1.0 | 初版作成（A側とB側のアーキテクチャ比較） |

---

**ドキュメント作成者**: Claude (GitHub Copilot)
**最終更新日**: 2026年4月16日
