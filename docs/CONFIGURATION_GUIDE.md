# Non-IP File Delivery 設定ガイド

**作成日**: 2026-04-14
**バージョン**: 1.0
**対象**: Phase 4完了版

---

## 概要

Non-IP File Deliveryシステムは、2つの設定ファイル形式をサポートしています:

1. **config.ini** - INI形式の設定ファイル（推奨）
2. **appsettings.json** - JSON形式の設定ファイル

両方のファイルが存在する場合、`config.ini`が優先されます。

---

## 設定ファイルの優先順位

```
1. 環境変数（最優先）
   └─ NONIP_CRYPTO_PASSWORD: 暗号化パスワード

2. config.ini（推奨）
   └─ INI形式の設定ファイル

3. appsettings.json
   └─ JSON形式の設定ファイル（WebConfig用）
```

---

## config.ini 設定項目

### [General]セクション

| 設定項目 | デフォルト値 | 説明 |
|---------|-------------|------|
| `Mode` | `ActiveStandby` | 動作モード<br>`ActiveStandby`: アクティブ/スタンバイ構成<br>`LoadBalancing`: 負荷分散構成 |
| `LogLevel` | `Warning` | ログレベル<br>`Debug`, `Info`, `Warning`, `Error` |

**例:**
```ini
[General]
Mode=ActiveStandby
LogLevel=Warning
```

---

### [Network]セクション

| 設定項目 | デフォルト値 | 説明 |
|---------|-------------|------|
| `Interface` | `eth0` | 使用するネットワークインターフェース名 |
| `FrameSize` | `9000` | フレームサイズ（バイト）<br>ジャンボフレーム対応: 最大9000バイト |
| `Encryption` | `true` | 暗号化を有効にするか |
| `EtherType` | `0x88B5` | カスタムEtherType<br>**注意**: 0x88B5は非IPプロトコル用 |
| `RemoteMacAddress` | なし | 対向機器のMACアドレス<br>**未設定の場合はシミュレーションモード** |
| `UseSecureTransceiver` | `false` | SecureEthernetTransceiverを使用するか<br>`false`: RawEthernetTransceiver（軽量）<br>`true`: SecureEthernetTransceiver（暗号化） |

**例:**
```ini
[Network]
Interface=eth0
FrameSize=9000
Encryption=true
EtherType=0x88B5
# 本番環境では必ず設定してください
RemoteMacAddress=00:11:22:33:44:55
UseSecureTransceiver=false
```

**重要:** `RemoteMacAddress`が未設定の場合、システムはシミュレーションモードで動作します。

---

### [Security]セクション

| 設定項目 | デフォルト値 | 説明 |
|---------|-------------|------|
| `EnableVirusScan` | `true` | ウイルススキャンを有効にするか |
| `ScanTimeout` | `5000` | スキャンタイムアウト（ミリ秒） |
| `QuarantinePath` | `C:\NonIP\Quarantine` | 隔離ファイルの保存先 |
| `PolicyFile` | `security_policy.ini` | セキュリティポリシーファイルのパス |
| `CryptoPassword` | (空文字列) | 暗号化パスワード<br>**`UseSecureTransceiver=true`の場合は必須。環境変数`NONIP_CRYPTO_PASSWORD`の使用を推奨** |

**優先順位:** `環境変数 NONIP_CRYPTO_PASSWORD` > `config.ini [Security] CryptoPassword` > `空文字列（未設定）`

INIローダー（`ConfigurationService.LoadFromIni`）は`Security:CryptoPassword`を読み込み、環境変数が設定されている場合はそちらが優先されます。`UseSecureTransceiver=true`の場合、パスワード未設定時は初期化エラーとなります。

**例:**
```ini
[Security]
EnableVirusScan=true
ScanTimeout=5000
QuarantinePath=C:\NonIP\Quarantine
PolicyFile=security_policy.ini
# 本番環境では環境変数 NONIP_CRYPTO_PASSWORD を使用してください
# このファイルにパスワードを直接記載しないことを推奨
CryptoPassword=<set via NONIP_CRYPTO_PASSWORD>
```

**セキュリティのベストプラクティス:**
```bash
# Windows PowerShell
$env:NONIP_CRYPTO_PASSWORD = "YourStrongPasswordHere"

# Linux/Mac
export NONIP_CRYPTO_PASSWORD="YourStrongPasswordHere"
```

---

### [Performance]セクション

| 設定項目 | デフォルト値 | 説明 |
|---------|-------------|------|
| `MaxMemoryMB` | `8192` | 最大メモリ使用量（MB） |
| `BufferSize` | `65536` | バッファサイズ（バイト） |
| `ThreadPool` | `auto` | スレッドプール設定<br>`auto`: 自動調整<br>数値: 最大スレッド数 |

**例:**
```ini
[Performance]
MaxMemoryMB=8192
BufferSize=65536
ThreadPool=auto
```

---

### [Redundancy]セクション

| 設定項目 | デフォルト値 | 説明 |
|---------|-------------|------|
| `HeartbeatInterval` | `1000` | ハートビート送信間隔（ミリ秒） |
| `FailoverTimeout` | `5000` | フェイルオーバータイムアウト（ミリ秒） |
| `DataSyncMode` | `realtime` | データ同期モード<br>`realtime`: リアルタイム同期<br>`batch`: バッチ同期 |

**例:**
```ini
[Redundancy]
HeartbeatInterval=1000
FailoverTimeout=5000
DataSyncMode=realtime
```

---

## appsettings.json 設定項目

### network セクション

```json
{
  "network": {
    "interfaceName": "eth0",
    "remoteMacAddress": "00:11:22:33:44:55",
    "etherType": "0x88B5",
    "useSecureTransceiver": false
  }
}
```

**注意:** `config.ini`との命名規則の統一:
- ~~`customEtherType`~~ → `etherType` に変更しました（Phase 4で修正）

---

### security セクション

```json
{
  "security": {
    "yaraRulesPath": "rules/*.yar",
    "enableDeepInspection": true,
    "scanTimeout": 5000,
    "cryptoPassword": "NonIPFileDeliverySecurePassword2025"
  }
}
```

---

### protocols セクション

```json
{
  "protocols": {
    "ftp": {
      "enabled": true,
      "listenPort": 21,
      "targetHost": "192.168.1.100",
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
  }
}
```

---

### qos セクション

```json
{
  "qos": {
    "enabled": true,
    "maxBandwidthMbps": 2000,
    "highPriorityWeight": 70,
    "normalPriorityWeight": 20,
    "lowPriorityWeight": 10,
    "maxQueueSize": 10000,
    "burstSizeBytes": 0
  }
}
```

---

## 設定ファイルの読み込み順序

```csharp
// 1. 環境変数をチェック
var cryptoPassword = Environment.GetEnvironmentVariable("NONIP_CRYPTO_PASSWORD");

// 2. config.ini から読み込み
if (cryptoPassword == null && File.Exists("config.ini"))
{
    var config = ConfigurationService.LoadFromIni("config.ini");
    cryptoPassword = config.Security.CryptoPassword;
}

// 3. appsettings.json から読み込み（フォールバック）
if (cryptoPassword == null && File.Exists("appsettings.json"))
{
    var config = ConfigurationService.LoadFromJson("appsettings.json");
    cryptoPassword = config.security.cryptoPassword;
}

// 4. デフォルト値を使用（開発環境のみ）
if (cryptoPassword == null)
{
    cryptoPassword = "NonIPFileDeliverySecurePassword2025"; // デフォルト
}
```

---

## 設定例

### 開発環境

```ini
[General]
Mode=ActiveStandby
LogLevel=Debug

[Network]
Interface=eth0
FrameSize=1500
Encryption=true
EtherType=0x88B5
# シミュレーションモード（RemoteMacAddress未設定）
UseSecureTransceiver=false

[Security]
EnableVirusScan=true
ScanTimeout=5000
QuarantinePath=C:\NonIP\Quarantine
PolicyFile=security_policy.ini
CryptoPassword=DevelopmentPassword
```

---

### 本番環境（A側 - 送信側）

```ini
[General]
Mode=ActiveStandby
LogLevel=Warning

[Network]
Interface=eth1
FrameSize=9000
Encryption=true
EtherType=0x88B5
RemoteMacAddress=00:1A:2B:3C:4D:5E  # B側のMACアドレス
UseSecureTransceiver=true  # 暗号化トランシーバー使用

[Security]
EnableVirusScan=true
ScanTimeout=5000
QuarantinePath=C:\NonIP\Quarantine
PolicyFile=security_policy.ini
# 本番環境では環境変数 NONIP_CRYPTO_PASSWORD を使用
# CryptoPasswordはコメントアウト

[Performance]
MaxMemoryMB=16384
BufferSize=131072
ThreadPool=auto

[Redundancy]
HeartbeatInterval=500
FailoverTimeout=3000
DataSyncMode=realtime
```

**環境変数設定（本番環境）:**
```powershell
# Windows Server
[System.Environment]::SetEnvironmentVariable("NONIP_CRYPTO_PASSWORD", "SuperSecureProductionPassword2025", "Machine")
```

---

### 本番環境（B側 - 受信側）

```ini
[General]
Mode=ActiveStandby
LogLevel=Warning

[Network]
Interface=eth1
FrameSize=9000
Encryption=true
EtherType=0x88B5
RemoteMacAddress=00:0A:1B:2C:3D:4E  # A側のMACアドレス
UseSecureTransceiver=true  # 暗号化トランシーバー使用

[Security]
EnableVirusScan=true
ScanTimeout=5000
QuarantinePath=C:\NonIP\Quarantine
PolicyFile=security_policy.ini
# 本番環境では環境変数 NONIP_CRYPTO_PASSWORD を使用
# CryptoPasswordはコメントアウト

[Performance]
MaxMemoryMB=16384
BufferSize=131072
ThreadPool=auto

[Redundancy]
HeartbeatInterval=500
FailoverTimeout=3000
DataSyncMode=realtime
```

---

## トラブルシューティング

### Q1: "Interface not found" エラーが発生する

**原因**: 指定したネットワークインターフェース名が存在しない

**解決策**:
```bash
# Windows
ipconfig /all

# Linux
ip link show
ifconfig -a
```

正しいインターフェース名を`Interface`設定に指定してください。

---

### Q2: シミュレーションモードから抜け出せない

**原因**: `RemoteMacAddress`が未設定

**解決策**:
```ini
[Network]
RemoteMacAddress=00:11:22:33:44:55  # 対向機器の実際のMACアドレスを設定
```

---

### Q3: 暗号化パスワードが反映されない

**原因**: 環境変数、config.ini、appsettings.jsonの優先順位

**解決策**:
1. 環境変数`NONIP_CRYPTO_PASSWORD`をチェック
2. config.iniの`CryptoPassword`をチェック
3. appsettings.jsonの`cryptoPassword`をチェック

優先順位は `環境変数 > config.ini > appsettings.json` です。

---

### Q4: フレームサイズが大きすぎるエラー

**原因**: ネットワークカードがジャンボフレームに対応していない

**解決策**:
```ini
[Network]
FrameSize=1500  # 標準的なMTUサイズに変更
```

---

## 参考情報

### 設定変更の反映

設定ファイルを変更した後、サービスの再起動が必要です:

```bash
# Windowsサービスの再起動
net stop NonIPFileDelivery
net start NonIPFileDelivery

# または
Restart-Service NonIPFileDelivery
```

### ログファイルの確認

設定が正しく読み込まれたかログファイルで確認できます:

```
ログファイル: C:\NonIP\Logs\nonip-YYYYMMDD.log
```

設定読み込み時のログ例:
```
[2026-04-14 10:30:15] [INFO] Configuration loaded from config.ini
[2026-04-14 10:30:15] [INFO] Network interface: eth1
[2026-04-14 10:30:15] [INFO] Remote MAC address: 00:11:22:33:44:55
[2026-04-14 10:30:15] [INFO] Encryption: Enabled
[2026-04-14 10:30:15] [INFO] Secure Transceiver: Enabled
```

---

## まとめ

- **開発環境**: `config.ini`でシンプルな設定、`RemoteMacAddress`未設定でシミュレーションモード
- **本番環境**: 環境変数`NONIP_CRYPTO_PASSWORD`を使用、`RemoteMacAddress`必須
- **設定優先順位**: 環境変数 > config.ini > appsettings.json
- **命名規則**: Phase 4で統一済み（`etherType`, `cryptoPassword`等）

---

**作成者**: GitHub Copilot
**最終更新**: 2026-04-14
**バージョン**: 1.0
**ステータス**: Phase 4完了
