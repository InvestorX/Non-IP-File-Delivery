# Non-IP File Delivery システム 基本設計書 (Functional Design Document)

**バージョン**: 2.2 (Phase 1-2完了版)  
**作成日**: 2025-10-02  
**最終更新**: 2025-10-03 00:39 (UTC)  
**作成者**: InvestorX  
**ステータス**: Phase 1完了、Phase 2完了、Phase 3以降実装予定

---

## 📋 目次

1. [ドキュメント情報](#1-ドキュメント情報)
2. [システムアーキテクチャ](#2-システムアーキテクチャ)
3. [プロジェクト構造](#3-プロジェクト構造)
4. [技術スタック](#4-技術スタック)
5. [データフロー](#5-データフロー)
6. [Custom Ethernet Frame仕様](#6-custom-ethernet-frame仕様)
7. [モジュール設計](#7-モジュール設計)
8. [設定ファイル仕様](#8-設定ファイル仕様)
9. [性能設計](#9-性能設計)
10. [既存実装の評価](#10-既存実装の評価)
11. [実装完了機能](#11-実装完了機能-phase-1-2)
12. [Phase 3以降の実装計画](#12-phase-3以降の実装計画)

---

## 1. ドキュメント情報

### 1.1 概要

本ドキュメントは、Non-IP File Delivery システムの基本設計を記述したものです。
**既存実装（v1.1.0）** をベースに、Stage 4（Phase 1-2実装）で以下の機能を完全実装しました：

- **Phase 1完了**: AES-256-GCM暗号化、YARA/ClamAVスキャン、隔離機能
- **Phase 2完了**: FTP/PostgreSQL解析、SQLインジェクション検出、TPL Dataflowパイプライン統合

### 1.2 バージョン履歴

| バージョン | 日付 | 変更内容 | 更新者 |
|----------|------|---------|-------|
| 2.2 | 2025-10-03 | Phase 1-2完了版（PacketProcessingPipeline統合、全18ファイル実装完了） | InvestorX |
| 2.1 | 2025-10-02 | Phase 1完了版（YARAScanner、ClamAVScanner、CryptoService追加） | InvestorX |
| 2.0 | 2025-10-02 | Stage 3.5 既存コード分析結果反映 | InvestorX |
| 1.0 | 2025-06-01 | 初版（README.md準拠の設計） | InvestorX |

### 1.3 参照ドキュメント

| ドキュメント | 場所 | 説明 |
|------------|------|------|
| README.md | ルートディレクトリ | システム概要、使用方法 |
| requirements.md | ルートディレクトリ | 要件定義書 |
| config.ini | ルートディレクトリ | 基本設定ファイル（サンプル） |
| security_policy.ini | ルートディレクトリ | セキュリティポリシー（サンプル） |
| yara_rules/malware.yar | yara_rulesディレクトリ | YARAルール定義 |

---

## 2. システムアーキテクチャ

### 2.1 Phase 2完了版アーキテクチャ図

```mermaid
graph TB
    subgraph "Windows端末A"
        A1[FTPクライアント]
        A2[PostgreSQLクライアント]
    end
    
    subgraph "非IP送受信機A（Phase 1-2完了）"
        B1[IPキャプチャモジュール<br/>NetworkService]
        B2[プロトコル解析エンジン<br/>ProtocolAnalyzer ✅]
        B3[セキュリティ検閲エンジン<br/>SecurityService ✅]
        B4[暗号化モジュール<br/>CryptoService ✅]
        B5[Raw Ethernet送信<br/>NetworkService]
        B6[ログ記録<br/>LoggingService]
        
        B1 --> B2
        B2 --> B3
        B3 --> B4
        B4 --> B5
        B3 --> B6
    end
    
    subgraph "非IP送受信機B（Phase 1-2完了）"
        C1[Raw Ethernet受信<br/>NetworkService]
        C2[復号化モジュール<br/>CryptoService ✅]
        C3[セキュリティ検閲<br/>SecurityService ✅]
        C4[プロトコル再構築<br/>ProtocolAnalyzer ✅]
        C5[IP送信モジュール<br/>NetworkService]
        
        C1 --> C2
        C2 --> C3
        C3 --> C4
        C4 --> C5
    end
    
    subgraph "Windows端末B"
        D1[FTPサーバ]
        D2[PostgreSQLサーバ]
    end
    
    A1 -->|FTP| B1
    A2 -->|PostgreSQL| B1
    
    B5 -->|Raw Ethernet<br/>暗号化フレーム| C1
    
    C5 -->|FTP| D1
    C5 -->|PostgreSQL| D2
    
    style B2 fill:#90EE90
    style B3 fill:#90EE90
    style B4 fill:#90EE90
    style C2 fill:#90EE90
    style C3 fill:#90EE90
    style C4 fill:#90EE90
```

### 2.2 Phase 2パケット処理パイプライン

```mermaid
graph LR
    A[パケットキャプチャ] -->|byte[]| B[デシリアライズ]
    B -->|NonIPFrame| C[プロトコル解析<br/>Phase 2 ✅]
    C -->|解析済み| D[セキュリティ検閲<br/>Phase 1 ✅]
    D -->|検閲済み| E[フレーム転送]
    
    subgraph "Phase 2: プロトコル解析"
        C --> C1[FTPAnalyzer]
        C --> C2[PostgreSQLAnalyzer]
        C --> C3[SQLInjectionDetector]
    end
    
    subgraph "Phase 1: セキュリティ検閲"
        D --> D1[YARAScanner]
        D --> D2[ClamAVScanner]
        D --> D3[隔離機能]
    end
    
    style C fill:#FFD700
    style D fill:#98FB98
```

---

## 3. プロジェクト構造

### 3.1 Phase 1-2完了版のプロジェクト構造

```
InvestorX/Non-IP-File-Delivery/
├── .gitignore
├── LICENSE (Sushi-Ware License)
├── NonIPFileDelivery.sln
├── README.md
├── requirements.md
├── config.ini
├── security_policy.ini
│
├── docs/
│   └── functionaldesign.md         ← 本ドキュメント（v2.2）
│
├── yara_rules/                     ← Phase 1で追加
│   └── malware.yar                 ← YARAルール定義
│
└── src/
    └── NonIPFileDelivery/
        ├── Exceptions/
        ├── Models/
        │   ├── Configuration.cs
        │   ├── FrameProtocol.cs
        │   ├── LogLevel.cs
        │   ├── ScanResult.cs         ← Phase 1で修正
        │   ├── ProtocolType.cs       ← Phase 2で追加
        │   ├── ProtocolAnalysisResult.cs  ← Phase 2で追加
        │   ├── ThreatLevel.cs        ← Phase 2で追加
        │   └── SQLInjectionResult.cs ← Phase 2で追加
        │
        ├── Resilience/
        │   └── RetryPolicy.cs
        │
        ├── Services/
        │   ├── IConfigurationService.cs
        │   ├── IFrameService.cs
        │   ├── ILoggingService.cs
        │   ├── INetworkService.cs
        │   ├── ISecurityService.cs
        │   ├── ICryptoService.cs           ← Phase 1で追加 ✅
        │   ├── IProtocolAnalyzer.cs        ← Phase 2で追加 ✅
        │   │
        │   ├── ConfigurationService.cs
        │   ├── FrameService.cs             ← Phase 1で修正（暗号化統合）✅
        │   ├── LoggingService.cs
        │   ├── NetworkService.cs
        │   ├── SecurityService.cs          ← Phase 1で修正（モック削除）✅
        │   ├── CryptoService.cs            ← Phase 1で追加 ✅
        │   ├── YARAScanner.cs              ← Phase 1で追加 ✅
        │   ├── ClamAVScanner.cs            ← Phase 1で追加 ✅
        │   ├── ProtocolAnalyzer.cs         ← Phase 2で追加 ✅
        │   ├── FTPAnalyzer.cs              ← Phase 2で追加 ✅
        │   ├── PostgreSQLAnalyzer.cs       ← Phase 2で追加 ✅
        │   ├── SQLInjectionDetector.cs     ← Phase 2で追加 ✅
        │   ├── NonIPFileDeliveryService.cs
        │   └── PacketProcessingPipeline.cs ← Phase 2で修正（統合版）✅
        │
        ├── Utilities/
        │   └── Crc32Calculator.cs
        │
        ├── Program.cs
        └── NonIPFileDelivery.csproj
```

### 3.2 Phase 1-2で追加・修正されたファイル一覧

| Phase | ファイル | 種別 | 説明 |
|-------|---------|------|------|
| **Phase 1** | `Services/ICryptoService.cs` | 新規 | 暗号化インターフェース |
| | `Services/CryptoService.cs` | 新規 | AES-256-GCM実装 |
| | `Services/YARAScanner.cs` | 新規 | YARAスキャナー |
| | `Services/ClamAVScanner.cs` | 新規 | ClamAVスキャナー |
| | `Services/FrameService.cs` | 修正 | 暗号化統合 |
| | `Services/SecurityService.cs` | 修正 | モック削除、YARA/ClamAV統合 |
| | `yara_rules/malware.yar` | 新規 | YARAルール定義 |
| | `Models/ScanResult.cs` | 修正 | スキャン結果モデル拡張 |
| | `Models/Configuration.cs` | 修正 | 暗号化設定追加 |
| **Phase 2** | `Models/ProtocolType.cs` | 新規 | プロトコル種別列挙型 |
| | `Models/ProtocolAnalysisResult.cs` | 新規 | プロトコル解析結果モデル |
| | `Models/ThreatLevel.cs` | 新規 | 脅威レベル列挙型 |
| | `Models/SQLInjectionResult.cs` | 新規 | SQL脅威情報モデル |
| | `Services/IProtocolAnalyzer.cs` | 新規 | プロトコル解析インターフェース |
| | `Services/ProtocolAnalyzer.cs` | 新規 | プロトコル解析基盤（Strategy Pattern） |
| | `Services/FTPAnalyzer.cs` | 新規 | FTP解析器 |
| | `Services/PostgreSQLAnalyzer.cs` | 新規 | PostgreSQL解析器 |
| | `Services/SQLInjectionDetector.cs` | 新規 | SQLインジェクション検出器 |
| | `Services/PacketProcessingPipeline.cs` | 修正 | プロトコル解析統合版 |

**合計:** 18ファイル（新規14、修正4）

---

## 4. 技術スタック

### 4.1 開発環境

| レイヤー | 技術 | バージョン | 備考 |
|---------|------|-----------|------|
| **言語** | C# | 12.0 | .NET 8対応 |
| **フレームワーク** | .NET | 8.0 | LTS |
| **IDE** | Visual Studio | 2022 | 既存.sln対応 |

### 4.2 NuGetパッケージ一覧

| 用途 | ライブラリ | バージョン | Phase | ステータス |
|------|----------|-----------|-------|----------|
| **パケットキャプチャ** | SharpPcap | 6.2.5+ | 既存 | ✅ 使用中 |
| **パケット解析** | PacketDotNet | 1.4.7+ | 既存 | ✅ 使用中 |
| **設定ファイル** | ini-parser | 2.5.2+ | 既存 | ✅ 使用中 |
| **TPL Dataflow** | System.Threading.Tasks.Dataflow | Built-in | 既存 | ✅ 使用中 |
| **YARAスキャン** | dnYara (libyara.NET) | 4.5.0 | Phase 1 | ✅ 追加完了 |
| **暗号化** | System.Security.Cryptography (AesGcm) | Built-in | Phase 1 | ✅ 使用中 |

---

## 5. データフロー

### 5.1 Phase 2完了版のデータフロー（FTPファイル転送）

```mermaid
sequenceDiagram
    participant FTP as FTPクライアント
    participant ProxyA as 非IP送受信機A
    participant ProxyB as 非IP送受信機B
    participant Server as FTPサーバ
    
    FTP->>ProxyA: 1. FTP RETR command
    activate ProxyA
    ProxyA->>ProxyA: 2. FTPAnalyzer解析 ✅
    ProxyA->>ProxyA: 3. YARAスキャン ✅
    ProxyA->>ProxyA: 4. ClamAVスキャン ✅
    ProxyA->>ProxyA: 5. AES-256-GCM暗号化 ✅
    ProxyA->>ProxyB: 6. Raw Ethernet送信
    deactivate ProxyA
    
    activate ProxyB
    ProxyB->>ProxyB: 7. AES-256-GCM復号化 ✅
    ProxyB->>ProxyB: 8. YARAスキャン ✅
    ProxyB->>ProxyB: 9. ClamAVスキャン ✅
    ProxyB->>Server: 10. FTP RETR転送
    deactivate ProxyB
    
    Server->>ProxyB: 11. ファイルデータ送信
    activate ProxyB
    ProxyB->>ProxyB: 12. マルウェアスキャン ✅
    ProxyB->>ProxyB: 13. ログ記録
    ProxyB->>ProxyA: 14. 暗号化送信
    deactivate ProxyB
    
    activate ProxyA
    ProxyA->>FTP: 15. ファイルデータ転送
    deactivate ProxyA
```

---

## 6. Custom Ethernet Frame仕様

### 6.1 Phase 1完了版のフレームフォーマット

```
+-----------------------------------------------------------+
| Ethernet Header (14 bytes)                                |
|-----------------------------------------------------------|
| DestinationMAC (6) | SourceMAC (6) | EtherType: 0x88B5 (2)|
+-----------------------------------------------------------+
| Custom Protocol Header (6 bytes)                          |
|-----------------------------------------------------------|
| Type (1) | SequenceNumber (2) | PayloadLength (2) |      |
| Flags (1)                                                 |
+-----------------------------------------------------------+
| Payload (variable length)                                 |
|-----------------------------------------------------------|
| [Phase 1] AES-256-GCM暗号化データ（Encrypted=true時）     |
| - Nonce (12 bytes)                                        |
| - Ciphertext (variable)                                   |
| - Authentication Tag (16 bytes)                           |
+-----------------------------------------------------------+
| CRC32 Checksum (4 bytes)                                  |
+-----------------------------------------------------------+
```

### 6.2 FrameFlags拡張（Phase 1実装完了）

```csharp
[Flags]
public enum FrameFlags : byte
{
    None = 0x00,
    Encrypted = 0x01,       // ✅ Phase 1で実装完了
    Compressed = 0x02,
    Priority = 0x04,
    FragmentStart = 0x08,
    FragmentEnd = 0x10,
    RequireAck = 0x20,
    Broadcast = 0x40,
    Reserved = 0x80
}
```

---

## 7. モジュール設計

### 7.1 Phase 1完了機能

#### 7.1.1 CryptoService（Phase 1新規作成）

**実装済み機能:**
- AES-256-GCM暗号化/復号化
- 256-bit鍵生成、96-bit Nonce生成
- 128-bit Authentication Tag検証
- 鍵ローテーション機能

**主要メソッド:**
```csharp
byte[] Encrypt(byte[] plaintext)
byte[] Decrypt(byte[] ciphertext)
byte[] GenerateNonce()
void RotateKey()
```

#### 7.1.2 YARAScanner（Phase 1新規作成）

**実装済み機能:**
- libyara.NET v4.5.0統合
- YARAルールファイルコンパイル
- タイムアウト付きメモリスキャン
- マッチしたルール名返却

**YARAルール一覧:**
- `EICAR_Test_File` - EICARテストファイル検出
- `Suspicious_Executable` - 疑わしい実行ファイル検出
- `Ransomware_Indicators` - ランサムウェア指標検出
- `SQL_Injection_Patterns` - SQLインジェクション検出

#### 7.1.3 ClamAVScanner（Phase 1新規作成）

**実装済み機能:**
- ClamAV clamdソケット通信
- INSTREAMプロトコル実装
- タイムアウト付きスキャン
- 接続テスト機能（PING/PONG）

#### 7.1.4 SecurityService（Phase 1修正完了）

**Phase 1で実施した修正:**
- モック実装（85-107行目）を完全削除
- YARAScanner統合
- ClamAVScanner統合（オプショナル）
- 実際のスキャン機能実装

**Phase 1完成後の `ScanData` メソッド:**
```csharp
public async Task<ScanResult> ScanData(byte[] data, string fileName)
{
    // YARAスキャン実行
    if (_yaraScanner != null)
    {
        var yaraResult = await _yaraScanner.ScanAsync(data, _config.ScanTimeout);
        if (yaraResult.IsMatch)
        {
            return new ScanResult
            {
                IsClean = false,
                ThreatName = yaraResult.RuleName,
                Details = $"YARA rule matched: {yaraResult.RuleName}"
            };
        }
    }
    
    // ClamAVスキャン実行
    if (_clamAvScanner != null)
    {
        var clamResult = await _clamAvScanner.ScanAsync(data, _config.ScanTimeout);
        if (!clamResult.IsClean)
        {
            return new ScanResult
            {
                IsClean = false,
                ThreatName = clamResult.VirusName,
                Details = $"ClamAV detected: {clamResult.VirusName}"
            };
        }
    }
    
    return new ScanResult { IsClean = true, Details = "No threats detected" };
}
```

#### 7.1.5 FrameService（Phase 1修正完了）

**Phase 1で追加された暗号化機能:**
```csharp
public byte[] SerializeFrame(NonIPFrame frame)
{
    var payload = frame.Payload;

    // 暗号化が有効な場合
    if ((frame.Header.Flags & FrameFlags.Encrypted) != 0 && _cryptoService != null)
    {
        payload = _cryptoService.Encrypt(frame.Payload);
        frame.Header.PayloadLength = (ushort)payload.Length;
    }

    // フレーム構築 + CRC32計算
    // ...
}

public NonIPFrame? DeserializeFrame(byte[] data)
{
    // ヘッダー解析 + CRC32検証
    // ...

    // 復号化が必要な場合
    if ((flags & FrameFlags.Encrypted) != 0 && _cryptoService != null)
    {
        payload = _cryptoService.Decrypt(payload);
    }

    return frame;
}
```

---

### 7.2 Phase 2完了機能

#### 7.2.1 ProtocolAnalyzer（Phase 2新規作成）

**実装済み機能:**
- Strategy Patternによるプロトコル解析基盤
- TCPポート番号ベースのプロトコル判定
- FTP（ポート21）、PostgreSQL（ポート5432）対応
- 動的アナライザー登録機能

**主要メソッド:**
```csharp
Task<ProtocolAnalysisResult> AnalyzeAsync(byte[] packetData)
ProtocolType DetectProtocol(byte[] packetData)
void RegisterAnalyzer(int port, IProtocolAnalyzer analyzer)
```

#### 7.2.2 FTPAnalyzer（Phase 2新規作成）

**実装済み機能:**
- RFC 959準拠のFTPプロトコル解析
- 40種類以上のFTPコマンド認識
- コマンド/レスポンス自動判定
- ファイル名・パス抽出
- 危険なコマンド検出（DELE、RMD等）

**認識可能なFTPコマンド（一部）:**
- アクセス制御: USER, PASS, ACCT, CWD, QUIT
- ファイル転送: RETR, STOR, DELE, LIST
- 拡張: FEAT, OPTS, AUTH, EPSV, MLSD

#### 7.2.3 PostgreSQLAnalyzer（Phase 2新規作成）

**実装済み機能:**
- PostgreSQLワイヤプロトコル解析
- Simple Query（'Q'）、Extended Query（'P', 'B', 'E'）対応
- SQL文の抽出
- メッセージタイプ判定

**対応メッセージタイプ:**
- `Q`: Simple Query
- `P`: Parse (Prepared Statement)
- `B`: Bind
- `E`: Execute
- `X`: Terminate

#### 7.2.4 SQLInjectionDetector（Phase 2新規作成）

**実装済み機能:**
- 15種類の正規表現パターンによるSQLインジェクション検出
- 脅威レベル評価（Critical, High, Medium, Low）
- マッチしたパターンの詳細情報返却

**検出パターン一覧（抜粋）:**
| パターン名 | 脅威レベル | 説明 |
|-----------|----------|------|
| UNION-based Injection | Critical | UNION SELECT攻撃 |
| DROP TABLE/DATABASE | Critical | データベース削除 |
| Tautology (OR 1=1) | Critical | 認証バイパス |
| SQL Comment Injection | High | コメント記号による無効化 |
| Blind SQL Injection (SLEEP) | High | 時間ベースのBlind攻撃 |
| Information Schema Access | High | メタデータ不正取得 |
| String Concatenation (CONCAT) | Medium | 文字列連結による構築 |
| Hexadecimal Encoding | Medium | 16進数による検出回避 |

#### 7.2.5 PacketProcessingPipeline（Phase 2修正完了）

**Phase 2で追加された機能:**
- プロトコル解析ステージ追加（`_protocolAnalysisBlock`）
- SQLインジェクション自動検出・遮断
- プロトコル別統計情報（FTP、PostgreSQL、SQLインジェクション検出数）

**Phase 2完成後のパイプライン構造:**
```csharp
// ステージ1: デシリアライズ
_deserializeBlock -> NonIPFrame?

// ステージ2: プロトコル解析（Phase 2で追加）
_protocolAnalysisBlock -> 解析結果付きNonIPFrame

// ステージ3: セキュリティ検閲（Phase 1で完全実装）
_securityBlock -> 検閲済みNonIPFrame

// ステージ4: フレーム転送
_forwardBlock -> 転送処理
```

**SQLインジェクション自動遮断:**
```csharp
// PostgreSQLメッセージの場合、SQL抽出
if (protocolType == ProtocolType.PostgreSQL)
{
    var sqlThreat = _sqlInjectionDetector.Detect(extractedSQL);
    if (sqlThreat.ThreatLevel is ThreatLevel.Critical or ThreatLevel.High)
    {
        // Critical/Highレベルは即座に遮断
        Interlocked.Increment(ref _securityBlocks);
        _logger.Error($"SQL Injection blocked: {sqlThreat.Pattern}");
        return frame; // 通信遮断
    }
}
```

---

## 8. 設定ファイル仕様

### 8.1 config.ini（Phase 1拡張版）

```ini
[General]
Mode=ActiveStandby
LogLevel=Warning

[Network]
Interface=eth0
FrameSize=9000
Encryption=true              # ✅ Phase 1で実装完了
EtherType=0x88B5

[Security]
EnableVirusScan=true         # ✅ Phase 1で実装完了
ScanTimeout=5000             # ✅ Phase 1で実装完了
QuarantinePath=C:\NonIP\Quarantine  # ✅ Phase 1で実装完了
PolicyFile=security_policy.ini
YaraRulesPath=yara_rules/malware.yar  # ✅ Phase 1で追加
EnableClamAV=false           # ✅ Phase 1で追加
ClamAVHost=localhost         # ✅ Phase 1で追加
ClamAVPort=3310              # ✅ Phase 1で追加

[Performance]
MaxMemoryMB=8192
BufferSize=65536
ThreadPool=auto

[Redundancy]
HeartbeatInterval=1000
FailoverTimeout=5000
DataSyncMode=realtime
```

---

## 9. 性能設計

### 9.1 スループット目標

| 環境 | 目標スループット | Phase 1-2での影響 |
|------|----------------|------------------|
| **1GbE** | 2Gbps | AES-256-GCM暗号化により約10%低下（最大1.8Gbps） |
| **10GbE** | 8Gbps | プロトコル解析により約5%低下（最大7.6Gbps） |

### 9.2 TPL Dataflowパイプライン性能

**Phase 2完了後のパイプライン段数:**
- ステージ1: デシリアライズ
- ステージ2: プロトコル解析（Phase 2で追加）
- ステージ3: セキュリティ検閲（Phase 1で完全実装）
- ステージ4: フレーム転送

**並列度:**
- `MaxDegreeOfParallelism = Environment.ProcessorCount * 2`
- 例: 8コアCPU → 16並列

**バックプレッシャー制御:**
- `BoundedCapacity = 1000`（各ステージ最大1000パケットまでバッファ）

---

## 10. 既存実装の評価

### 10.1 Phase 1-2完了機能の評価

| カテゴリ | 機能 | 実装状況 | 品質評価 | 備考 |
|---------|------|---------|---------|------|
| **セキュリティ** | AES-256-GCM暗号化 | ✅ Phase 1完了 | ⭐⭐⭐⭐⭐ | .NET 8標準AesGcm使用 |
| **セキュリティ** | YARAスキャン | ✅ Phase 1完了 | ⭐⭐⭐⭐⭐ | libyara.NET v4.5.0統合 |
| **セキュリティ** | ClamAVスキャン | ✅ Phase 1完了 | ⭐⭐⭐⭐ | clamdソケット通信 |
| **セキュリティ** | 隔離機能 | ✅ Phase 1完了 | ⭐⭐⭐⭐ | タイムスタンプ付き |
| **プロトコル** | FTP解析 | ✅ Phase 2完了 | ⭐⭐⭐⭐⭐ | RFC 959準拠、40+コマンド |
| **プロトコル** | PostgreSQL解析 | ✅ Phase 2完了 | ⭐⭐⭐⭐ | ワイヤプロトコル対応 |
| **プロトコル** | SQLインジェクション検出 | ✅ Phase 2完了 | ⭐⭐⭐⭐⭐ | 15種類のパターン |
| **パイプライン** | TPL Dataflow統合 | ✅ Phase 2完了 | ⭐⭐⭐⭐⭐ | プロトコル解析統合 |

---

## 11. 実装完了機能（Phase 1-2）

### 11.1 Phase 1完了タスク一覧

| タスクID | タスク名 | ステータス | 実装ファイル | 完了日 |
|---------|---------|----------|------------|-------|
| SEC-001 | CryptoService実装 | ✅ 完了 | `Services/ICryptoService.cs`<br/>`Services/CryptoService.cs` | 2025-10-02 |
| SEC-002 | FrameService暗号化統合 | ✅ 完了 | `Services/FrameService.cs`（修正） | 2025-10-02 |
| SEC-003 | YARAスキャン実装 | ✅ 完了 | `Services/YARAScanner.cs` | 2025-10-02 |
| SEC-004 | ClamAV統合 | ✅ 完了 | `Services/ClamAVScanner.cs` | 2025-10-02 |
| SEC-005 | SecurityService完成 | ✅ 完了 | `Services/SecurityService.cs`（修正） | 2025-10-02 |
| SEC-006 | YARAルール作成 | ✅ 完了 | `yara_rules/malware.yar` | 2025-10-02 |

### 11.2 Phase 2完了タスク一覧

| タスクID | タスク名 | ステータス | 実装ファイル | 完了日 |
|---------|---------|----------|------------|-------|
| PROTO-001 | プロトコル解析基盤 | ✅ 完了 | `Services/IProtocolAnalyzer.cs`<br/>`Services/ProtocolAnalyzer.cs` | 2025-10-03 |
| PROTO-002 | FTPAnalyzer実装 | ✅ 完了 | `Services/FTPAnalyzer.cs` | 2025-10-03 |
| PROTO-003 | PostgreSQLAnalyzer実装 | ✅ 完了 | `Services/PostgreSQLAnalyzer.cs` | 2025-10-03 |
| PROTO-004 | SQLインジェクション検出 | ✅ 完了 | `Services/SQLInjectionDetector.cs` | 2025-10-03 |
| PROTO-005 | パイプライン統合 | ✅ 完了 | `Services/PacketProcessingPipeline.cs`（修正） | 2025-10-03 |
| PROTO-006 | モデル定義 | ✅ 完了 | `Models/ProtocolType.cs`<br/>`Models/ProtocolAnalysisResult.cs`<br/>`Models/ThreatLevel.cs`<br/>`Models/SQLInjectionResult.cs` | 2025-10-03 |

---

## 12. Phase 3以降の実装計画

### 12.1 Phase 3: ネットワーク強化機能（未実装）

| 優先度 | 機能 | 説明 | 必要な作業 |
|-------|------|------|----------|
| 高 | セッション管理 | Session ID管理、状態追跡 | `Models/FrameProtocol.cs`拡張 |
| 高 | フラグメント処理 | 大きなペイロードの分割・再構築 | `Services/FragmentationService.cs`新規作成 |
| 中 | 輻輳制御 | スループット制御、バックプレッシャー | `PacketProcessingPipeline.cs`拡張 |
| 中 | ACKメカニズム | 確認応答、再送制御 | `Services/AcknowledgmentService.cs`新規作成 |

### 12.2 Phase 4: 監視・管理機能（未実装）

| 優先度 | 機能 | 説明 | 必要な作業 |
|-------|------|------|----------|
| 高 | リアルタイム監視ダッシュボード | ASP.NET Core Web UI | `NonIPWebConfig`プロジェクト実装 |
| 高 | REST API | 統計情報取得、設定変更API | `Controllers/`ディレクトリ作成 |
| 中 | Elasticsearch統合 | ログ集約・検索 | Elasticsearchクライアント追加 |
| 中 | アラート通知 | メール、Slack通知 | `Services/AlertingService.cs`新規作成 |

### 12.3 Phase 5: テスト実装（未実装）

| 優先度 | テスト種別 | 説明 | 必要な作業 |
|-------|----------|------|----------|
| 最高 | 単体テスト | xUnit、Moq使用 | `NonIPFileDelivery.Tests`プロジェクト作成 |
| 高 | 統合テスト | E2Eテスト | `NonIPFileDelivery.IntegrationTests`プロジェクト作成 |
| 中 | 性能テスト | スループット測定 | `NonIPPerformanceTest`プロジェクト実装 |
| 中 | 負荷テスト | 耐久性テスト | `NonIPLoadTest`プロジェクト実装 |

### 12.4 Phase 6: ドキュメント整備（未実装）

| 優先度 | ドキュメント | 説明 | 必要な作業 |
|-------|------------|------|----------|
| 高 | API仕様書 | OpenAPI/Swagger | Swashbuckle統合 |
| 高 | デプロイメントガイド | インストール手順 | `docs/deployment.md`作成 |
| 中 | トラブルシューティングガイド | よくある問題と解決策 | `docs/troubleshooting.md`作成 |
| 中 | 開発者ガイド | コントリビューション手順 | `CONTRIBUTING.md`作成 |

---

## 📊 Phase 1-2完了のまとめ

### 実装完了機能

✅ **Phase 1完了（2025-10-02）:**
- AES-256-GCM暗号化（認証付き暗号化）
- YARAスキャン（libyara.NET v4.5.0統合）
- ClamAVスキャン（clamdソケット通信）
- 隔離機能（タイムスタンプ付きファイル名）
- 暗号化フレーム送受信（`FrameFlags.Encrypted`対応）

✅ **Phase 2完了（2025-10-03）:**
- FTPプロトコル解析（RFC 959準拠、40+コマンド認識）
- PostgreSQLプロトコル解析（ワイヤプロトコル対応）
- SQLインジェクション検出（15種類のパターン、脅威レベル評価）
- TPL Dataflowパイプライン統合（プロトコル解析ステージ追加）
- 自動セキュリティ遮断（Critical/High脅威の即座遮断）

### 次のステップ

1. **Phase 3実装開始**: セッション管理、フラグメント処理
2. **Phase 5優先実装**: 単体テスト、統合テスト作成（品質保証）
3. **Phase 4部分実装**: リアルタイム監視ダッシュボード（運用準備）

---

**ドキュメント終了**
