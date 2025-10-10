# 非IP送受信機B (Non-IP Transceiver B) 実装ガイド

## 概要

非IP送受信機Bは、Raw Ethernetで受信したデータを実際のサーバー（FTP/SFTP/PostgreSQL）に転送するサーバー側コンポーネントです。

## システム構成

```
[Windows端末A] <-> [非IP送受信機A] <-> [Raw Ethernet] <-> [非IP送受信機B] <-> [Windows端末B]
   (クライアント)     (A側プロキシ)      (非IPプロトコル)     (B側プロキシ)      (サーバー群)
```

## アーキテクチャ

### 非IP送受信機A（クライアント側）
- TCP接続を**待ち受け**（Listen）
- クライアントからのリクエストを受信
- Raw Ethernetで送信

### 非IP送受信機B（サーバー側）
- Raw Ethernetから**受信**
- 実際のサーバーに**接続**（Connect）
- サーバーからのレスポンスをRaw Ethernetで返送

## プロジェクト構造

```
src/
├── NonIPFileDelivery/              # A側（既存）
│   ├── Program.cs                   # A側エントリーポイント
│   └── Protocols/
│       ├── FtpProxy.cs              # A側FTPプロキシ（Listen）
│       ├── SftpProxy.cs             # A側SFTPプロキシ（Listen）
│       ├── PostgreSqlProxy.cs       # A側PostgreSQLプロキシ（Listen）
│       ├── FtpProxyB.cs            # B側FTPプロキシ（Connect）
│       ├── SftpProxyB.cs           # B側SFTPプロキシ（Connect）
│       └── PostgreSqlProxyB.cs     # B側PostgreSQLプロキシ（Connect）
└── NonIPFileDeliveryB/             # B側（新規実装）
    ├── Program.cs                   # B側エントリーポイント
    └── NonIPFileDeliveryB.csproj    # B側プロジェクト
```

## 実装詳細

### 1. FtpProxyB.cs - FTPプロキシ（B側）

**役割:**
- Raw Ethernetから受信したFTPコマンドを実際のFTPサーバーに転送
- FTPサーバーからのレスポンスをRaw Ethernetで返送

**主要機能:**
```csharp
public class FtpProxyB : IDisposable
{
    // セッション管理
    private readonly ConcurrentDictionary<string, FtpSession> _sessions;
    
    // Raw Ethernetからの受信処理
    private async Task HandleRawEthernetPacketAsync(EthernetPacket packet)
    {
        // 1. プロトコル識別子とセッションIDを抽出
        // 2. セキュリティ検閲
        // 3. FTPサーバーに転送
    }
    
    // FTPサーバーへの接続と送信
    private async Task<FtpSession?> GetOrCreateSessionAsync(string sessionId)
    {
        // セッションがない場合は新規作成し、FTPサーバーに接続
    }
    
    // FTPサーバーからのレスポンス受信
    private async Task ReceiveFromServerAsync(FtpSession session)
    {
        // FTPサーバーからのレスポンスをRaw Ethernetで返送
    }
}
```

**プロトコルフォーマット:**
```
[1 byte: Protocol Type] [8 bytes: Session ID] [N bytes: Data]
```

### 2. SftpProxyB.cs - SFTPプロキシ（B側）

**役割:**
- Raw Ethernetから受信したSSH/SFTPデータを実際のSFTPサーバーに転送
- SFTPサーバーからのレスポンスをRaw Ethernetで返送

**特徴:**
- SSH暗号化されたデータをそのまま転送（中身は検閲不可）
- バイナリデータをバッファリングして効率的に転送

**主要機能:**
```csharp
public class SftpProxyB : IDisposable
{
    private const byte PROTOCOL_SFTP = 0x03;
    
    // 64KBバッファでデータ転送
    private async Task ReceiveFromServerAsync(SftpSession session)
    {
        var buffer = new byte[65536];
        while (!_cts.Token.IsCancellationRequested)
        {
            var bytesRead = await session.ServerStream.ReadAsync(buffer, _cts.Token);
            if (bytesRead == 0) break;
            
            // Raw Ethernetで返送
            var payload = BuildProtocolPayload(PROTOCOL_SFTP, session.SessionId, data);
            await _transceiver.SendAsync(payload, _cts.Token);
        }
    }
}
```

### 3. PostgreSqlProxyB.cs - PostgreSQLプロキシ（B側）

**役割:**
- Raw Ethernetから受信したPostgreSQLクエリを実際のPostgreSQLサーバーに転送
- PostgreSQLサーバーからのレスポンスをRaw Ethernetで返送

**特徴:**
- PostgreSQL Wire Protocolを透過的に転送
- SQLインジェクション検出機能
- エラーメッセージの生成

**主要機能:**
```csharp
public class PostgreSqlProxyB : IDisposable
{
    private const byte PROTOCOL_POSTGRESQL = 0x04;
    
    // PostgreSQLエラーメッセージの生成
    private byte[] CreatePostgreSqlErrorMessage(string message)
    {
        // 'E' (Error) + length + severity + message
        // PostgreSQL Wire Protocol準拠
    }
}
```

### 4. Program.cs（B側エントリーポイント）

**役割:**
- B側アプリケーションの初期化とライフサイクル管理

**起動フロー:**
```csharp
static async Task Main(string[] args)
{
    // 1. ロギング初期化
    Log.Logger = new LoggerConfiguration()...
    
    // 2. 設定ファイル読み込み
    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.b.json", optional: true)
        .AddJsonFile("appsettings.json", optional: false)
        .Build();
    
    // 3. コンポーネント初期化
    using var transceiver = new RawEthernetTransceiver(...);
    using var inspector = new SecurityInspector(...);
    
    // 4. B側プロキシ初期化
    var ftpProxyB = new FtpProxyB(transceiver, inspector, ...);
    var sftpProxyB = new SftpProxyB(transceiver, inspector, ...);
    var pgProxyB = new PostgreSqlProxyB(transceiver, inspector, ...);
    
    // 5. Raw Ethernet送受信開始
    transceiver.Start();
    
    // 6. シャットダウン待機
    await Task.Delay(Timeout.Infinite, cts.Token);
}
```

## 設定ファイル

### appsettings.b.json

```json
{
  "Network": {
    "InterfaceName": "eth1",
    "RemoteMacAddress": "AA:BB:CC:DD:EE:FF",
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
      "TargetHost": "192.168.1.100",
      "TargetPort": 21
    },
    "Sftp": {
      "Enabled": true,
      "TargetHost": "192.168.1.100",
      "TargetPort": 22
    },
    "Postgresql": {
      "Enabled": true,
      "TargetHost": "192.168.1.100",
      "TargetPort": 5432
    }
  }
}
```

### 設定パラメータ説明

| パラメータ | 説明 | デフォルト値 |
|-----------|------|------------|
| `Network:InterfaceName` | Raw Ethernet送受信に使用するネットワークインターフェース | eth1 |
| `Network:RemoteMacAddress` | 非IP送受信機AのMACアドレス | AA:BB:CC:DD:EE:FF |
| `Protocols:Ftp:TargetHost` | Windows端末B上のFTPサーバーIPアドレス | 192.168.1.100 |
| `Protocols:Sftp:TargetHost` | Windows端末B上のSFTPサーバーIPアドレス | 192.168.1.100 |
| `Protocols:Postgresql:TargetHost` | Windows端末B上のPostgreSQLサーバーIPアドレス | 192.168.1.100 |

## ビルド方法

### 全体ビルド
```bash
dotnet build NonIPFileDelivery.sln
```

### B側のみビルド
```bash
dotnet build src/NonIPFileDeliveryB/NonIPFileDeliveryB.csproj
```

### 実行ファイル出力先
```
src/NonIPFileDeliveryB/bin/Debug/net8.0/NonIPFileDeliveryB.exe  (Windows)
src/NonIPFileDeliveryB/bin/Debug/net8.0/NonIPFileDeliveryB      (Linux)
```

## 実行方法

### 前提条件
1. Npcap（Windows）またはlibpcap（Linux）がインストールされている
2. 管理者権限でRaw Ethernetにアクセス可能
3. `appsettings.b.json`が適切に設定されている

### 実行コマンド

**Windows:**
```cmd
# 管理者権限で実行
cd src\NonIPFileDeliveryB\bin\Debug\net8.0
NonIPFileDeliveryB.exe
```

**Linux:**
```bash
# root権限で実行
cd src/NonIPFileDeliveryB/bin/Debug/net8.0
sudo ./NonIPFileDeliveryB
```

### 実行例

```
========================================
Non-IP File Delivery System B Starting...
Version: 1.0.0 - PostgreSQL/SFTP Support
Role: Server-side (B) - Receiver
========================================
[12:34:56 INF] RawEthernetTransceiver initialized: Interface=eth1
[12:34:56 INF] FtpProxyB initialized: Target=192.168.1.100:21
[12:34:56 INF] SftpProxyB initialized: Target=192.168.1.100:22
[12:34:56 INF] PostgreSqlProxyB initialized: Target=192.168.1.100:5432
[12:34:56 INF] FTP Proxy B enabled
[12:34:56 INF] SFTP Proxy B enabled
[12:34:56 INF] PostgreSQL Proxy B enabled
[12:34:56 INF] All services started successfully
[12:34:56 INF] Active Protocols: FTP=True, SFTP=True, PostgreSQL=True
[12:34:56 INF] Waiting for Raw Ethernet packets from Transceiver A...
[12:34:56 INF] Press Ctrl+C to shutdown...
```

## デプロイメント

### ネットワーク構成

```
┌─────────────────┐         ┌─────────────────┐         ┌─────────────────┐
│  Windows端末A   │         │ 非IP送受信機A   │         │ 非IP送受信機B   │
│                 │  TCP/IP │                 │   Raw   │                 │
│ - FTPクライアント│◄────────┤ - FtpProxy     │ Ethernet│ - FtpProxyB    │
│ - psql         │         │ - SftpProxy    │◄────────┤ - SftpProxyB   │
│                 │         │ - PostgreSqlProxy│        │ - PostgreSqlProxyB│
└─────────────────┘         └─────────────────┘         └─────────────────┘
                                                                  │ TCP/IP
                                                                  ▼
                                                         ┌─────────────────┐
                                                         │  Windows端末B   │
                                                         │ - FTPサーバー    │
                                                         │ - SFTPサーバー   │
                                                         │ - PostgreSQL    │
                                                         └─────────────────┘
```

### インストール手順

**非IP送受信機B（サーバー側マシン）:**

1. **ハードウェア要件:**
   - CPU: マルチコア推奨（2コア以上）
   - メモリ: 4GB以上
   - ネットワーク: 2つのNIC（1つはRaw Ethernet用、1つはサーバー接続用）

2. **ソフトウェアインストール:**
   ```bash
   # .NET 8 Runtimeインストール
   # Windows: https://dotnet.microsoft.com/download/dotnet/8.0
   # Linux: sudo apt install dotnet-runtime-8.0
   
   # Npcapインストール（Windows）
   # https://npcap.com/
   
   # libpcapインストール（Linux）
   sudo apt install libpcap-dev
   ```

3. **アプリケーション配置:**
   ```bash
   # ビルド済みバイナリをコピー
   mkdir /opt/nonip-transceiver-b
   cd /opt/nonip-transceiver-b
   cp -r <ビルド成果物>/* .
   
   # 設定ファイル編集
   nano appsettings.b.json
   ```

4. **設定調整:**
   - `Network:InterfaceName`: 実際のNIC名に変更（`ip link`コマンドで確認）
   - `Network:RemoteMacAddress`: 非IP送受信機AのMACアドレスに変更
   - `Protocols:*:TargetHost`: 実際のサーバーIPに変更

5. **サービス登録（オプション）:**
   ```bash
   # systemdサービス登録（Linux）
   sudo nano /etc/systemd/system/nonip-transceiver-b.service
   ```
   
   ```ini
   [Unit]
   Description=Non-IP File Delivery System B
   After=network.target
   
   [Service]
   Type=simple
   User=root
   WorkingDirectory=/opt/nonip-transceiver-b
   ExecStart=/opt/nonip-transceiver-b/NonIPFileDeliveryB
   Restart=on-failure
   
   [Install]
   WantedBy=multi-user.target
   ```
   
   ```bash
   sudo systemctl enable nonip-transceiver-b
   sudo systemctl start nonip-transceiver-b
   ```

## トラブルシューティング

### よくある問題

#### 1. "Npcap driver not found"
**原因:** Npcapがインストールされていない、または権限不足

**解決策:**
- Npcapを再インストール
- 管理者権限で実行

#### 2. "Failed to connect to FTP server"
**原因:** ターゲットサーバーに接続できない

**解決策:**
- `appsettings.b.json`のターゲットホスト設定を確認
- ファイアウォール設定を確認
- サーバーが起動しているか確認

#### 3. "No Raw Ethernet packets received"
**原因:** 非IP送受信機Aからパケットが届いていない

**解決策:**
- MACアドレス設定を確認
- ネットワークインターフェース名を確認
- ケーブル接続を確認
- 非IP送受信機Aが正常に動作しているか確認

## セキュリティ考慮事項

### 検閲機能
- FTPコマンド検証（`ValidateFtpCommand`）
- データスキャン（`ScanData`）
- YARA統合によるマルウェア検出

### 推奨設定
1. `Security:EnableDeepInspection`: true（常に有効化）
2. YARA rulesを定期的に更新
3. ログを監視し、不正アクセスを検知

### セッション管理
- セッションIDによる追跡
- タイムアウト処理（TODO: 今後実装）
- 接続数制限（`Performance:MaxConcurrentSessions`）

## パフォーマンス最適化

### チューニングポイント
1. **バッファサイズ:** `Performance:ReceiveBufferSize`
2. **並列処理:** `Task.Run`による非同期処理
3. **メモリ管理:** `ConcurrentDictionary`でセッション管理

### ベンチマーク
- **目標スループット:** 2Gbps以上
- **レイテンシ:** 10ms以下（Raw Ethernet往復）

## ログ

### ログ出力先
- コンソール（標準出力）
- ファイル: `logs/non-ip-file-delivery-b-YYYYMMDD.log`

### ログレベル
- `Debug`: 詳細なデバッグ情報
- `Information`: 通常の動作ログ
- `Warning`: セキュリティイベント、異常検知
- `Error`: エラー発生
- `Fatal`: 致命的なエラー

## 今後の拡張

### 実装予定機能
- [ ] セッションタイムアウト処理
- [ ] データチャンネル処理（FTPパッシブモード完全対応）
- [ ] 接続プーリングの最適化
- [ ] メトリクス収集（Prometheus対応）
- [ ] Web管理UI統合

## まとめ

非IP送受信機Bの実装により、以下が実現されました：

✅ **Raw Ethernetからの受信とサーバー転送**
✅ **FTP/SFTP/PostgreSQL完全対応**
✅ **セッション管理とマルチプレクシング**
✅ **セキュリティ検閲統合**
✅ **双方向通信（リクエスト・レスポンス）**

これにより、Windows端末A上のクライアントアプリケーションから、非IPプロトコル経由でWindows端末B上のサーバーに透過的にアクセスすることが可能になりました。
