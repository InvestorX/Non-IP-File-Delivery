# 設定ガイド

## 概要

Non-IP File Delivery システムの設定方法について詳しく説明します。

## 基本設定

### config.ini

基本的なシステム設定を行うファイルです。

```ini
[General]
Mode=ActiveStandby  # ActiveStandby | LoadBalancing
LogLevel=Warning    # Debug | Info | Warning | Error

[Network]
Interface=eth0      # ネットワークインターフェース名
FrameSize=9000      # フレームサイズ（ジャンボフレーム）
Encryption=true     # 暗号化の有効/無効
EtherType=0x88B5    # カスタムEtherType

[Security]
EnableVirusScan=true                    # ウイルススキャンの有効/無効
ScanTimeout=5000                        # スキャンタイムアウト（ミリ秒）
QuarantinePath=C:\NonIP\Quarantine      # 隔離フォルダパス
PolicyFile=security_policy.ini          # セキュリティポリシーファイル

[Performance]
MaxMemoryMB=8192    # 最大メモリ使用量（MB）
BufferSize=65536    # バッファサイズ
ThreadPool=auto     # スレッドプール設定

[Redundancy]
HeartbeatInterval=1000  # ハートビート間隔（ミリ秒）
FailoverTimeout=5000    # フェイルオーバータイムアウト（ミリ秒）
DataSyncMode=realtime   # データ同期モード
```

### security_policy.ini

セキュリティポリシーを定義するファイルです。

```ini
[FileExtensions]
Allowed=.txt,.pdf,.docx,.xlsx           # 許可するファイル拡張子
Blocked=.exe,.bat,.cmd,.vbs,.scr        # ブロックするファイル拡張子

[FileSize]
MaxSizeMB=3072      # 最大ファイルサイズ（MB）
MinSizeKB=1         # 最小ファイルサイズ（KB）

[ContentType]
AllowedTypes=text/*,application/pdf,application/msword     # 許可するMIMEタイプ
BlockedPatterns=malware,virus,trojan                       # ブロックするパターン
```

## 詳細設定

### ネットワーク設定

#### インターフェース名の確認
Windows環境では以下のコマンドでネットワークインターフェース名を確認できます：

```cmd
ipconfig /all
```

#### ジャンボフレーム設定
9000バイトのジャンボフレームを使用する場合、ネットワークカードとスイッチの両方でジャンボフレームサポートが必要です。

### セキュリティ設定

#### ウイルススキャン
- ClamAVエンジンを使用
- スキャンタイムアウトは5秒以内を推奨
- 隔離フォルダは書き込み権限が必要

#### 暗号化
- AES-256-GCMアルゴリズムを使用
- パフォーマンスに影響があるため、必要に応じて無効化可能

### パフォーマンス設定

#### メモリ使用量
- システムメモリの50%以下を推奨
- 大容量ファイル転送時は多めに設定

#### バッファサイズ
- 64KBが標準
- 高速なストレージ環境では増加を検討

### 冗長化設定

#### アクティブ-スタンバイ構成
```ini
[Redundancy]
Mode=ActiveStandby
PrimaryNode=192.168.1.10
StandbyNode=192.168.1.11
VirtualIP=192.168.1.100
```

#### ロードバランシング構成
```ini
[Redundancy]
Mode=LoadBalancing
Node1=192.168.1.10
Node2=192.168.1.11
Node3=192.168.1.12
Algorithm=RoundRobin  # RoundRobin | WeightedRoundRobin
```

## 設定ツールの使用

### コンソール設定ツール
```bash
# 設定ファイル作成
NonIPConfigTool.exe --create-config

# 設定ファイル検証
NonIPConfigTool.exe --validate-config config.ini
```

### Web設定ツール
```bash
# Web設定ツール起動
NonIPWebConfig.exe

# ブラウザで http://localhost:8080 にアクセス
```

## トラブルシューティング

### 設定ファイルエラー
- INIファイルの形式が正しいか確認
- セクション名とキー名の大文字小文字に注意
- 特殊文字（日本語パス等）はエスケープが必要

### ネットワーク設定エラー
- インターフェース名が正しいか確認
- 管理者権限で実行されているか確認
- ファイアウォール設定を確認

### セキュリティ設定エラー
- ClamAVがインストールされているか確認
- 隔離フォルダの書き込み権限を確認
- ポリシーファイルの形式を確認

---

### ClamAV拡張コマンド設定・運用例（Phase 3）

#### 複数ファイル並列スキャン（MULTISCAN）
- ファイルパス配列を指定して高速スキャン
- 例: `/var/tmp/upload1.txt`, `/var/tmp/upload2.txt` など

#### ディレクトリ連続スキャン（CONTSCAN）
- ディレクトリパスを指定して全ファイルを再帰的にスキャン
- 例: `/var/tmp/uploads/` など

#### clamd統計情報取得（STATS）
- サービス稼働状況やスキャン統計を取得
- 運用監視やヘルスチェックに活用

#### ウイルス定義DB再読み込み（RELOAD）
- 新しい定義ファイル反映時に実行
- サービス再起動不要で即時反映

#### 設定例（appsettings.json/ini）
```ini
[ClamAV]
Host=localhost
Port=3310
ScanTimeout=5000
MultiScanTimeout=30000
ContScanTimeout=60000
EnableStats=true
```

#### 注意事項
- MULTISCAN/CONTSCANはclamdのファイルパス参照権限が必要
- STATS/RELOADは管理者権限での運用推奨
- スキャン結果・統計はアプリ側で集計・監視可能

---