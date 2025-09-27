# API リファレンス

## Web設定API

### 設定取得
```http
GET /api/config
```

**レスポンス例:**
```json
{
  "mode": "ActiveStandby",
  "logLevel": "Warning",
  "interface": "eth0",
  "frameSize": "9000",
  "encryption": "true",
  "enableVirusScan": "true",
  "scanTimeout": "5000",
  "quarantinePath": "C:\\NonIP\\Quarantine"
}
```

### 設定保存
```http
POST /api/config
Content-Type: application/json
```

**リクエスト例:**
```json
{
  "mode": "ActiveStandby",
  "logLevel": "Warning",
  "interface": "eth0",
  "frameSize": "9000",
  "encryption": "true",
  "enableVirusScan": "true",
  "scanTimeout": "5000",
  "quarantinePath": "C:\\NonIP\\Quarantine"
}
```

**レスポンス例:**
```json
{
  "success": true,
  "message": "設定が正常に保存されました"
}
```

### システムステータス取得
```http
GET /api/status
```

**レスポンス例:**
```json
{
  "status": "running",
  "version": "1.0.0",
  "uptime": "00:05:23",
  "throughput": "1.2 Gbps",
  "connections": 42,
  "memory_usage": "2.1 GB"
}
```

## コマンドラインAPI

### NonIPFileDelivery.exe
メインサービスアプリケーション

**使用方法:**
```bash
NonIPFileDelivery.exe [オプション]
```

**オプション:**
- `--debug`: デバッグモードで実行
- `--log-level <level>`: ログレベルを指定 (Debug, Info, Warning, Error)
- `--config <path>`: 設定ファイルのパスを指定
- `--help, -h`: ヘルプを表示

### NonIPConfigTool.exe
設定管理ツール

**使用方法:**
```bash
NonIPConfigTool.exe [オプション]
```

**オプション:**
- `--create-config`: 新しい設定ファイルを作成
- `--validate-config [path]`: 設定ファイルを検証
- `--help, -h`: ヘルプを表示

### NonIPPerformanceTest.exe
パフォーマンステストツール

**使用方法:**
```bash
NonIPPerformanceTest.exe --mode=<mode> [オプション]
```

**モード:**
- `throughput`: スループットテスト
- `latency`: レイテンシテスト

**オプション:**
- `--target-gbps <value>`: 目標スループット (Gbps)
- `--max-latency-ms <value>`: 最大許容レイテンシ (ms)
- `--duration-minutes <value>`: テスト継続時間 (分)
- `--help, -h`: ヘルプを表示

### NonIPLoadTest.exe
負荷テストツール

**使用方法:**
```bash
NonIPLoadTest.exe [オプション]
```

**オプション:**
- `--concurrent-connections <num>`: 同時接続数
- `--duration-minutes <num>`: テスト継続時間 (分)
- `--file-size-kb <num>`: ファイルサイズ (KB)
- `--help, -h`: ヘルプを表示

## エラーコード

| コード | 説明 |
|--------|------|
| 0 | 正常終了 |
| 1 | 一般的なエラー |
| 2 | 設定エラー |
| 3 | ネットワークエラー |
| 4 | セキュリティエラー |