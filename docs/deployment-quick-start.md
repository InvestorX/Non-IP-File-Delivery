# 非IP送受信機 デプロイメントクイックスタート

## システム概要

```
┌──────────────┐    TCP/IP     ┌──────────────┐   Raw Ethernet  ┌──────────────┐    TCP/IP     ┌──────────────┐
│ Windows端末A │◄────────────►│ 送受信機A    │◄──────────────►│ 送受信機B    │◄────────────►│ Windows端末B │
│              │               │(クライアント側)│                │(サーバー側)  │               │              │
│ - FTPクライアント│               │              │                │              │               │ - FTPサーバ   │
│ - psql       │               │              │                │              │               │ - PostgreSQL │
└──────────────┘               └──────────────┘                └──────────────┘               └──────────────┘
                                NonIPFileDelivery              NonIPFileDeliveryB
```

## 必要なもの

### 共通
- .NET 8 Runtime
- Npcap (Windows) または libpcap (Linux)
- 管理者権限

### ハードウェア
- **送受信機A用マシン:** Windows端末Aと同じネットワークに接続
- **送受信機B用マシン:** Windows端末Bと同じネットワークに接続
- A-B間はクロスケーブルまたはスイッチ経由で接続

## デプロイ手順

### ステップ1: ビルド

```bash
cd /path/to/Non-IP-File-Delivery
dotnet build NonIPFileDelivery.sln --configuration Release
```

**成果物:**
- `src/NonIPFileDelivery/bin/Release/net8.0/` - 送受信機A用
- `src/NonIPFileDeliveryB/bin/Release/net8.0/` - 送受信機B用

### ステップ2: 送受信機A（クライアント側）のセットアップ

**配置先マシン:** Windows端末Aの近くに設置

1. **ファイルコピー:**
   ```bash
   mkdir /opt/nonip-transceiver-a
   cp -r src/NonIPFileDelivery/bin/Release/net8.0/* /opt/nonip-transceiver-a/
   ```

2. **設定ファイル編集 (appsettings.json):**
   ```json
   {
     "Network": {
       "InterfaceName": "eth0",           // A-B間の通信用NIC
       "RemoteMacAddress": "BB:BB:BB:BB:BB:BB",  // 送受信機BのMAC
       "CustomEtherType": "0x88B5"
     },
     "Protocols": {
       "Ftp": {
         "Enabled": true,
         "ListenPort": 21,                // Windows端末Aからの接続を待ち受け
         "TargetHost": "192.168.1.100",   // 無視される（B側設定）
         "TargetPort": 21
       },
       "Postgresql": {
         "Enabled": true,
         "ListenPort": 5432,
         "TargetHost": "192.168.1.100",
         "TargetPort": 5432
       }
     }
   }
   ```

3. **ネットワーク設定:**
   - Windows端末Aからのルーティング設定
   - 送受信機AのIPアドレス: 例 `192.168.100.10`

4. **起動:**
   ```bash
   cd /opt/nonip-transceiver-a
   sudo ./NonIPFileDelivery  # または NonIPFileDelivery.exe
   ```

### ステップ3: 送受信機B（サーバー側）のセットアップ

**配置先マシン:** Windows端末Bの近くに設置

1. **ファイルコピー:**
   ```bash
   mkdir /opt/nonip-transceiver-b
   cp -r src/NonIPFileDeliveryB/bin/Release/net8.0/* /opt/nonip-transceiver-b/
   ```

2. **設定ファイル作成 (appsettings.b.json):**
   ```json
   {
     "Network": {
       "InterfaceName": "eth0",           // A-B間の通信用NIC
       "RemoteMacAddress": "AA:AA:AA:AA:AA:AA",  // 送受信機AのMAC
       "CustomEtherType": "0x88B5"
     },
     "Protocols": {
       "Ftp": {
         "Enabled": true,
         "TargetHost": "192.168.1.100",   // Windows端末BのFTPサーバーIP
         "TargetPort": 21
       },
       "Postgresql": {
         "Enabled": true,
         "TargetHost": "192.168.1.100",   // Windows端末BのPostgreSQLサーバーIP
         "TargetPort": 5432
       }
     }
   }
   ```

3. **ネットワーク設定:**
   - Windows端末Bへのルーティング設定
   - 送受信機BのIPアドレス: 例 `192.168.1.200`

4. **起動:**
   ```bash
   cd /opt/nonip-transceiver-b
   sudo ./NonIPFileDeliveryB  # または NonIPFileDeliveryB.exe
   ```

## 設定例: FTP接続

### シナリオ
- **Windows端末A (クライアント):** 10.0.0.10
- **送受信機A:** 10.0.0.100 (Windows端末A側), Raw Ethernet用NIC
- **送受信機B:** Raw Ethernet用NIC, 192.168.1.200 (Windows端末B側)
- **Windows端末B (FTPサーバー):** 192.168.1.100:21

### 設定

**送受信機A (appsettings.json):**
```json
{
  "Network": {
    "InterfaceName": "eth1",
    "RemoteMacAddress": "<送受信機BのMAC>",
    "CustomEtherType": "0x88B5"
  },
  "Protocols": {
    "Ftp": {
      "Enabled": true,
      "ListenPort": 21,
      "TargetHost": "192.168.1.100",
      "TargetPort": 21
    }
  }
}
```

**送受信機B (appsettings.b.json):**
```json
{
  "Network": {
    "InterfaceName": "eth0",
    "RemoteMacAddress": "<送受信機AのMAC>",
    "CustomEtherType": "0x88B5"
  },
  "Protocols": {
    "Ftp": {
      "Enabled": true,
      "TargetHost": "192.168.1.100",
      "TargetPort": 21
    }
  }
}
```

### クライアント接続方法

Windows端末Aから以下のように接続:

```bash
# FTP接続
ftp 10.0.0.100
# または
ftp://10.0.0.100

# PostgreSQL接続
psql -h 10.0.0.100 -U username -d database
```

## トラブルシューティング

### 1. "Device not found"
**原因:** ネットワークインターフェース名が間違っている

**確認方法:**
```bash
# Linux
ip link show

# Windows
ipconfig /all
```

### 2. パケットが届かない
**確認項目:**
- [ ] MACアドレスが正しいか確認
- [ ] ケーブル接続を確認
- [ ] ファイアウォールを無効化または許可設定
- [ ] 両方の送受信機が起動しているか確認

**デバッグ:**
```bash
# Wiresharkでキャプチャ
tcpdump -i eth0 ether proto 0x88B5
```

### 3. サーバーに接続できない
**確認項目 (送受信機B):**
- [ ] `TargetHost`が正しいか確認
- [ ] ターゲットサーバーが起動しているか確認
- [ ] ルーティングが正しいか確認

```bash
# 送受信機BからWindows端末Bへのpingテスト
ping 192.168.1.100
```

## ログ確認

### ログファイル

**送受信機A:**
```
logs/non-ip-file-delivery-YYYYMMDD.log
```

**送受信機B:**
```
logs/non-ip-file-delivery-b-YYYYMMDD.log
```

### ログレベル

設定ファイルで変更可能:
```json
{
  "Logging": {
    "MinimumLevel": "Debug"  // Debug, Information, Warning, Error
  }
}
```

## 性能チェック

### スループット確認

```bash
# FTPで大きなファイルを転送
ftp 10.0.0.100
ftp> put largefile.bin

# 転送速度を確認
# 目標: 2Gbps (250MB/s) 以上
```

### ログで確認

```bash
# 送受信機Aのログ
grep "Forwarded FTP" logs/non-ip-file-delivery-*.log

# 送受信機Bのログ
grep "Received FTP" logs/non-ip-file-delivery-b-*.log
```

## サービス登録（オプション）

### systemd (Linux)

**送受信機A:**
```bash
sudo nano /etc/systemd/system/nonip-transceiver-a.service
```

```ini
[Unit]
Description=Non-IP File Delivery Transceiver A
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=/opt/nonip-transceiver-a
ExecStart=/opt/nonip-transceiver-a/NonIPFileDelivery
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable nonip-transceiver-a
sudo systemctl start nonip-transceiver-a
sudo systemctl status nonip-transceiver-a
```

**送受信機B:**
```bash
sudo nano /etc/systemd/system/nonip-transceiver-b.service
```

```ini
[Unit]
Description=Non-IP File Delivery Transceiver B
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
sudo systemctl status nonip-transceiver-b
```

## セキュリティ

### 推奨設定

1. **YARA Rulesを最新化:**
   ```bash
   cd rules/
   # YARAルールを更新
   ```

2. **ログ監視:**
   ```bash
   tail -f logs/non-ip-file-delivery-b-*.log | grep "Blocked"
   ```

3. **ファイアウォール設定:**
   - 送受信機A: Windows端末Aからのみ接続許可
   - 送受信機B: Windows端末Bへのみ接続許可

## まとめ

✅ **完了チェックリスト:**
- [ ] 両方の送受信機がビルドされている
- [ ] 設定ファイルが正しく編集されている
- [ ] MACアドレスが相互に正しく設定されている
- [ ] 両方の送受信機が起動している
- [ ] Windows端末Aからクライアント接続が成功する
- [ ] ログにエラーが出ていない

🎉 **これで非IPファイル転送システムが稼働します！**

詳細なドキュメントは以下を参照:
- [transceiver-b-guide.md](transceiver-b-guide.md) - B側実装詳細
- [functionaldesign.md](functionaldesign.md) - システム設計全体
