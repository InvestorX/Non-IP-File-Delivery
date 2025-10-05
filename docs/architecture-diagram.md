# 非IP送受信機システム アーキテクチャ図

## 全体システム構成

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                          Non-IP File Delivery System                         │
└─────────────────────────────────────────────────────────────────────────────┘

┌──────────────────┐         ┌──────────────────┐         ┌──────────────────┐
│  Windows端末A    │         │  非IP送受信機A   │         │  非IP送受信機B   │
│                  │         │  (クライアント側) │         │  (サーバー側)     │
│ ┌──────────────┐ │         │ ┌──────────────┐ │         │ ┌──────────────┐ │
│ │FTPクライアント│ │ TCP/IP  │ │ FtpProxy     │ │  Raw    │ │ FtpProxyB    │ │
│ │(FileZilla)   │◄├─────────┤►│ (Listen:21)  │◄├─Ethernet├►│ (Connect)    │◄├─┐
│ └──────────────┘ │         │ └──────────────┘ │         │ └──────────────┘ │ │
│                  │         │                  │         │                  │ │
│ ┌──────────────┐ │         │ ┌──────────────┐ │         │ ┌──────────────┐ │ │
│ │SFTPクライアント│ │ TCP/IP  │ │ SftpProxy    │ │  Raw    │ │ SftpProxyB   │ │ │
│ │(WinSCP)      │◄├─────────┤►│ (Listen:22)  │◄├─Ethernet├►│ (Connect)    │◄├─┤
│ └──────────────┘ │         │ └──────────────┘ │         │ └──────────────┘ │ │
│                  │         │                  │         │                  │ │
│ ┌──────────────┐ │         │ ┌──────────────┐ │         │ ┌──────────────┐ │ │
│ │PostgreSQL    │ │ TCP/IP  │ │PostgreSqlProxy│ │  Raw    │ │PostgreSqlProxyB││ │
│ │クライアント   │◄├─────────┤►│ (Listen:5432)│◄├─Ethernet├►│ (Connect)    │◄├─┤
│ │(pgAdmin/psql)│ │         │ └──────────────┘ │         │ └──────────────┘ │ │
│ └──────────────┘ │         │                  │         │                  │ │
│                  │         │ ┌──────────────┐ │         │ ┌──────────────┐ │ │
│ IP: 10.0.0.10    │         │ │RawEthernet   │ │         │ │RawEthernet   │ │ │
└──────────────────┘         │ │Transceiver   │◄├─────────┤►│Transceiver   │ │ │
                             │ └──────────────┘ │         │ └──────────────┘ │ │
                             │ ┌──────────────┐ │         │ ┌──────────────┐ │ │
                             │ │Security      │ │         │ │Security      │ │ │
                             │ │Inspector     │ │         │ │Inspector     │ │ │
                             │ └──────────────┘ │         │ └──────────────┘ │ │
                             │                  │         │                  │ │
                             │ IP: 10.0.0.100   │         │ IP: 192.168.1.200│ │
                             │ MAC: AA:AA:...   │         │ MAC: BB:BB:...   │ │
                             └──────────────────┘         └──────────────────┘ │
                                                                                │
                                                           ┌──────────────────┐ │
                                                           │  Windows端末B    │ │
                                                           │                  │ │
                                      TCP/IP               │ ┌──────────────┐ │ │
                                      ┌────────────────────┤►│FTPサーバー   │ │ │
                                      │                    │ │(vsftpd/IIS)  │◄┘ │
                                      │                    │ └──────────────┘ │
                                      │                    │                  │
                                      │                    │ ┌──────────────┐ │
                                      │                    ├►│SFTPサーバー  │◄──┘
                                      │                    │ │(OpenSSH)     │ │
                                      │                    │ └──────────────┘ │
                                      │                    │                  │
                                      │                    │ ┌──────────────┐ │
                                      │                    ├►│PostgreSQL    │◄──┘
                                      │                    │ │Server        │ │
                                      │                    │ └──────────────┘ │
                                      │                    │                  │
                                      │                    │ IP: 192.168.1.100│
                                      │                    └──────────────────┘
                                      └─────────────────────────────────────────
```

## データフロー - FTP GET コマンド例

```
┌────────────┐    ┌─────────┐    ┌──────────────┐    ┌─────────┐    ┌────────────┐
│Windows端末A│    │送受信機A │    │ Raw Ethernet │    │送受信機B │    │Windows端末B│
└─────┬──────┘    └────┬────┘    └──────┬───────┘    └────┬────┘    └─────┬──────┘
      │                │                 │                 │                │
      │ FTP GET file   │                 │                 │                │
      ├───────────────►│                 │                 │                │
      │                │ [0x01][SessID]  │                 │                │
      │                │    [GET file]   │                 │                │
      │                ├────────────────►│                 │                │
      │                │                 │ 暗号化フレーム  │                │
      │                │                 ├────────────────►│                │
      │                │                 │                 │ GET file       │
      │                │                 │                 ├───────────────►│
      │                │                 │                 │                │
      │                │                 │                 │ 200 OK         │
      │                │                 │                 │ + FileData     │
      │                │                 │                 │◄───────────────┤
      │                │                 │                 │                │
      │                │                 │ [0x01][SessID]  │                │
      │                │                 │    [200 OK]     │                │
      │                │                 │◄────────────────┤                │
      │                │                 │                 │                │
      │                │ [0x02][SessID]  │                 │                │
      │                │    [FileData]   │                 │                │
      │                │◄────────────────┤                 │                │
      │                │                 │                 │                │
      │ 200 OK         │                 │                 │                │
      │ + FileData     │                 │                 │                │
      │◄───────────────┤                 │                 │                │
      │                │                 │                 │                │
      ▼                ▼                 ▼                 ▼                ▼

時間軸 →
```

## コンポーネント詳細

### 非IP送受信機A (NonIPFileDelivery.exe)

```
┌────────────────────────────────────────────────────────┐
│            NonIPFileDelivery (A側)                      │
├────────────────────────────────────────────────────────┤
│                                                        │
│  Program.cs (Main Entry Point)                        │
│  └─ Configuration Loading                             │
│  └─ Component Initialization                          │
│                                                        │
│  ┌──────────────────────────────────────────────────┐ │
│  │ Protocols Layer                                   │ │
│  │                                                   │ │
│  │  FtpProxy (Listen TCP:21)                        │ │
│  │  ├─ HandleClientAsync()                          │ │
│  │  ├─ Security Inspection                          │ │
│  │  └─ SendAsync(Raw Ethernet)                      │ │
│  │                                                   │ │
│  │  SftpProxy (Listen TCP:22)                       │ │
│  │  └─ SSH Protocol Handling                        │ │
│  │                                                   │ │
│  │  PostgreSqlProxy (Listen TCP:5432)               │ │
│  │  └─ PostgreSQL Wire Protocol                     │ │
│  └──────────────────────────────────────────────────┘ │
│                                                        │
│  ┌──────────────────────────────────────────────────┐ │
│  │ Core Layer                                        │ │
│  │                                                   │ │
│  │  RawEthernetTransceiver                          │ │
│  │  ├─ Start() - Begin capturing                    │ │
│  │  ├─ SendAsync() - Send frames                    │ │
│  │  └─ ReceiveStream() - Receive frames             │ │
│  └──────────────────────────────────────────────────┘ │
│                                                        │
│  ┌──────────────────────────────────────────────────┐ │
│  │ Security Layer                                    │ │
│  │                                                   │ │
│  │  SecurityInspector                               │ │
│  │  ├─ ValidateFtpCommand()                         │ │
│  │  ├─ ScanData()                                   │ │
│  │  └─ YARA Integration                             │ │
│  └──────────────────────────────────────────────────┘ │
│                                                        │
└────────────────────────────────────────────────────────┘
```

### 非IP送受信機B (NonIPFileDeliveryB.exe) ⭐NEW!

```
┌────────────────────────────────────────────────────────┐
│            NonIPFileDeliveryB (B側)                     │
├────────────────────────────────────────────────────────┤
│                                                        │
│  Program.cs (Main Entry Point)                        │
│  └─ Configuration Loading (appsettings.b.json)        │
│  └─ Component Initialization                          │
│                                                        │
│  ┌──────────────────────────────────────────────────┐ │
│  │ Protocols Layer (B-side)                         │ │
│  │                                                   │ │
│  │  FtpProxyB (Connect to FTP Server)               │ │
│  │  ├─ HandleRawEthernetPacketAsync()               │ │
│  │  ├─ GetOrCreateSessionAsync()                    │ │
│  │  └─ ReceiveFromServerAsync()                     │ │
│  │                                                   │ │
│  │  SftpProxyB (Connect to SFTP Server)             │ │
│  │  └─ Session Management                           │ │
│  │                                                   │ │
│  │  PostgreSqlProxyB (Connect to PostgreSQL)        │ │
│  │  └─ CreatePostgreSqlErrorMessage()               │ │
│  └──────────────────────────────────────────────────┘ │
│                                                        │
│  ┌──────────────────────────────────────────────────┐ │
│  │ Core Layer (Shared from NonIPFileDelivery)       │ │
│  │                                                   │ │
│  │  RawEthernetTransceiver                          │ │
│  │  SecurityInspector                               │ │
│  └──────────────────────────────────────────────────┘ │
│                                                        │
│  ┌──────────────────────────────────────────────────┐ │
│  │ Session Management                                │ │
│  │                                                   │ │
│  │  ConcurrentDictionary<SessionId, Session>        │ │
│  │  ├─ TcpClient to backend server                  │ │
│  │  └─ NetworkStream for communication              │ │
│  └──────────────────────────────────────────────────┘ │
│                                                        │
└────────────────────────────────────────────────────────┘
```

## Raw Ethernetフレームフォーマット

```
┌──────────────────────────────────────────────────────────────────┐
│                      Ethernet Frame                              │
├──────────────────────────────────────────────────────────────────┤
│  Destination MAC (6 bytes) │  Source MAC (6 bytes)               │
├────────────────────────────┼─────────────────────────────────────┤
│  EtherType: 0x88B5 (2 bytes)                                     │
├──────────────────────────────────────────────────────────────────┤
│                      Payload (Custom Protocol)                   │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │ Protocol Type (1 byte)                                     │  │
│  │   0x01 = FTP Control                                       │  │
│  │   0x02 = FTP Data                                          │  │
│  │   0x03 = SFTP                                              │  │
│  │   0x04 = PostgreSQL                                        │  │
│  ├────────────────────────────────────────────────────────────┤  │
│  │ Session ID (8 bytes, ASCII)                                │  │
│  │   Example: "a1b2c3d4"                                      │  │
│  ├────────────────────────────────────────────────────────────┤  │
│  │ Data (Variable length)                                     │  │
│  │   - FTP: ASCII commands or responses                       │  │
│  │   - SFTP: SSH encrypted binary data                        │  │
│  │   - PostgreSQL: Wire protocol messages                     │  │
│  └────────────────────────────────────────────────────────────┘  │
├──────────────────────────────────────────────────────────────────┤
│  FCS (4 bytes) - Frame Check Sequence                            │
└──────────────────────────────────────────────────────────────────┘

Total Frame Size: 64 - 9000 bytes (Jumbo frames supported)
```

## セッション管理フロー

```
┌─────────────────────────────────────────────────────────────────┐
│                 Session Lifecycle (B-side)                       │
└─────────────────────────────────────────────────────────────────┘

1. 新規セッション作成
   ┌──────────────────────────────────────────────┐
   │ Raw Ethernet packet received                 │
   │ SessionID: "abc12345"                        │
   └──────────────────┬───────────────────────────┘
                      │
                      ▼
   ┌──────────────────────────────────────────────┐
   │ GetOrCreateSessionAsync(sessionId)           │
   │ - Check _sessions dictionary                 │
   │ - If not exists, create new TcpClient        │
   │ - Connect to backend server                  │
   │ - Store in dictionary                        │
   └──────────────────┬───────────────────────────┘
                      │
                      ▼
   ┌──────────────────────────────────────────────┐
   │ Start ReceiveFromServerAsync()               │
   │ - Background task                            │
   │ - Read from server NetworkStream             │
   │ - Send responses back via Raw Ethernet       │
   └──────────────────────────────────────────────┘

2. セッション再利用
   ┌──────────────────────────────────────────────┐
   │ Raw Ethernet packet received                 │
   │ SessionID: "abc12345" (existing)             │
   └──────────────────┬───────────────────────────┘
                      │
                      ▼
   ┌──────────────────────────────────────────────┐
   │ GetOrCreateSessionAsync(sessionId)           │
   │ - Found in _sessions dictionary              │
   │ - Return existing session                    │
   └──────────────────┬───────────────────────────┘
                      │
                      ▼
   ┌──────────────────────────────────────────────┐
   │ Use existing TcpClient                       │
   │ - Send data to server                        │
   └──────────────────────────────────────────────┘

3. セッション終了
   ┌──────────────────────────────────────────────┐
   │ Server closes connection                     │
   │ OR ReceiveFromServerAsync() reads 0 bytes    │
   └──────────────────┬───────────────────────────┘
                      │
                      ▼
   ┌──────────────────────────────────────────────┐
   │ Cleanup                                      │
   │ - Remove from _sessions dictionary           │
   │ - Dispose TcpClient                          │
   │ - Dispose NetworkStream                      │
   └──────────────────────────────────────────────┘
```

## セキュリティ検閲フロー

```
A側 (送信機A)              Raw Ethernet              B側 (送受信機B)
─────────────              ─────────────              ─────────────
    │                                                      │
    │ 1. FTP Command                                       │
    │    "DELE malware.exe"                                │
    │                                                      │
    ▼                                                      │
┌─────────────┐                                           │
│ Security    │                                           │
│ Inspector   │                                           │
│ - YARA scan │                                           │
│ - Validate  │                                           │
└─────┬───────┘                                           │
      │                                                   │
      │ 2. If BLOCKED                                     │
      ├──► Return "550 Rejected" to client               │
      │                                                   │
      │ 3. If OK                                          │
      │                                                   │
      ▼                                                   │
   Send via                                               │
   Raw Ethernet ─────────────────────────────────────────►│
                                                          │
                                                          ▼
                                                    ┌─────────────┐
                                                    │ Security    │
                                                    │ Inspector   │
                                                    │ - YARA scan │
                                                    │ - Validate  │
                                                    └─────┬───────┘
                                                          │
                                                          │ If OK
                                                          ▼
                                                    Forward to
                                                    FTP Server
```

## パフォーマンス目標

| 項目 | 目標値 | 実測値（要検証） |
|------|--------|-----------------|
| スループット | 2Gbps以上 | TBD |
| レイテンシ | 10ms以下 | TBD |
| 同時セッション数 | 100以上 | Unlimited (ConcurrentDictionary) |
| CPU使用率 | 80%以下 | TBD |
| メモリ使用量 | 4GB以下 | TBD |

## 今後の拡張ポイント

1. **セッションタイムアウト**
   - 現在: 無期限（サーバーが閉じるまで）
   - 改善: アイドルタイムアウト機能追加

2. **接続プーリング**
   - 現在: セッションごとに新規接続
   - 改善: 事前接続プール作成

3. **メトリクス収集**
   - 現在: ログのみ
   - 改善: Prometheus/Grafana統合

4. **高可用性**
   - 現在: 単一インスタンス
   - 改善: アクティブ-スタンバイ構成

## まとめ

このアーキテクチャにより、以下が実現されます：

✅ **完全な透過性:** クライアントとサーバーは通常のTCP/IP通信と同様に動作
✅ **セキュリティ:** 両側での検閲により、マルウェア侵入を防止
✅ **高性能:** Raw Ethernetによる低レイテンシ通信
✅ **拡張性:** プロトコル追加が容易な設計
✅ **運用性:** 詳細なログとモニタリング機能
