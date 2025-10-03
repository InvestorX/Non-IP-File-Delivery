# ビルドエラー修正ガイド

**✅ ステータス**: このガイドに記載されている問題は**2025-01-10に修正完了**しました。

このドキュメントは、Non-IP File Delivery プロジェクトにあった18件のビルドエラーを修正するための詳細な手順を提供します。現在、プロジェクトはビルド可能です（0エラー、16警告）。

このガイドは、以下の目的で保持されています：
- 過去の問題の記録
- 同様の問題が発生した場合の参考資料
- プロジェクトの開発履歴の文書化

---

## 🔧 修正手順の概要

1. [外部パッケージの追加](#1-外部パッケージの追加)
2. [Crc32Calculator の修正](#2-crc32calculator-の修正)
3. [ConfigurationService の実装完了](#3-configurationservice-の実装完了)
4. [FrameService の実装完了](#4-frameservice-の実装完了)
5. [SecurityService の実装完了](#5-securityservice-の実装完了)
6. [RetryPolicy の修正](#6-retrypolicy-の修正)

---

## 1. 外部パッケージの追加

### 問題
```
Error: The type or namespace name 'libyaraNET' could not be found
```

### 修正方法

`src/NonIPFileDelivery/NonIPFileDelivery.csproj` に以下のパッケージ参照を追加:

```xml
<ItemGroup>
  <!-- 既存のパッケージ参照の後に追加 -->
  
  <!-- YARA マルウェアスキャン -->
  <PackageReference Include="libyara.NET" Version="4.5.0" />
  
  <!-- または、YARAが利用できない場合の代替案 -->
  <!-- <PackageReference Include="dnYara" Version="3.2.0" /> -->
</ItemGroup>
```

**注意**: 
- `libyara.NET` が NuGet で利用できない場合は、YARAScanner の実装を一時的にコメントアウトするか、代替の実装を検討してください
- Windows環境では、YARAのネイティブDLLが必要な場合があります

---

## 2. Crc32Calculator の修正

### 問題
```
Error: Array elements cannot be of type 'ReadOnlySpan<byte>'
```

### 修正方法

`src/NonIPFileDelivery/Utilities/Crc32Calculator.cs` の `CalculateComposite` メソッドを以下のように修正:

#### 修正前:
```csharp
public static uint CalculateComposite(params ReadOnlySpan<byte>[] dataParts)
{
    ArgumentNullException.ThrowIfNull(dataParts);
    if (dataParts.Length == 0) return 0;

    var crc32 = new Crc32();
    foreach (var part in dataParts)
    {
        if (!part.IsEmpty)
        {
            crc32.Append(part);
        }
    }
    var hash = crc32.GetCurrentHash();
    return BinaryPrimitives.ReadUInt32BigEndian(hash);
}
```

#### 修正後:
```csharp
/// <summary>
/// 複数のデータ片を連結してCRC32を計算
/// </summary>
public static uint CalculateComposite(IEnumerable<byte[]> dataParts)
{
    ArgumentNullException.ThrowIfNull(dataParts);
    
    var crc32 = new Crc32();
    var hasData = false;
    
    foreach (var part in dataParts)
    {
        if (part != null && part.Length > 0)
        {
            crc32.Append(part);
            hasData = true;
        }
    }
    
    if (!hasData) return 0;
    
    var hash = crc32.GetCurrentHash();
    return BinaryPrimitives.ReadUInt32BigEndian(hash);
}

/// <summary>
/// 複数のデータ片を連結してCRC32を計算（params 配列版）
/// </summary>
public static uint CalculateComposite(params byte[][] dataParts)
{
    return CalculateComposite((IEnumerable<byte[]>)dataParts);
}
```

---

## 3. ConfigurationService の実装完了

### 問題
```
Error: 'ConfigurationService' does not implement interface member 
       'IConfigurationService.SaveConfiguration(Configuration, string)'
Error: 'ConfigurationService' does not implement interface member 
       'IConfigurationService.ValidateConfiguration(Configuration)'
```

### 修正方法

`src/NonIPFileDelivery/Services/ConfigurationService.cs` に以下のメソッドを追加:

```csharp
/// <summary>
/// 設定をファイルに保存
/// </summary>
public void SaveConfiguration(Configuration config, string configPath)
{
    ArgumentNullException.ThrowIfNull(config);
    ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
    
    var ext = Path.GetExtension(configPath).ToLowerInvariant();
    
    if (ext == ".json")
    {
        var json = System.Text.Json.JsonSerializer.Serialize(config, 
            new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        File.WriteAllText(configPath, json, Encoding.UTF8);
    }
    else if (ext == ".ini")
    {
        // INI形式での保存実装
        var ini = new StringBuilder();
        
        ini.AppendLine("[General]");
        ini.AppendLine($"Mode={config.General?.Mode ?? "ActiveStandby"}");
        ini.AppendLine($"LogLevel={config.General?.LogLevel ?? "Warning"}");
        ini.AppendLine();
        
        ini.AppendLine("[Network]");
        ini.AppendLine($"Interface={config.Network?.Interface ?? "eth0"}");
        ini.AppendLine($"FrameSize={config.Network?.FrameSize ?? 9000}");
        ini.AppendLine($"Encryption={config.Network?.Encryption ?? true}");
        ini.AppendLine($"EtherType={config.Network?.EtherType ?? "0x88B5"}");
        ini.AppendLine();
        
        ini.AppendLine("[Security]");
        ini.AppendLine($"EnableVirusScan={config.Security?.EnableVirusScan ?? true}");
        ini.AppendLine($"ScanTimeout={config.Security?.ScanTimeout ?? 5000}");
        ini.AppendLine($"QuarantinePath={config.Security?.QuarantinePath ?? "C:\\NonIP\\Quarantine"}");
        ini.AppendLine($"PolicyFile={config.Security?.PolicyFile ?? "security_policy.ini"}");
        ini.AppendLine();
        
        ini.AppendLine("[Performance]");
        ini.AppendLine($"MaxMemoryMB={config.Performance?.MaxMemoryMB ?? 8192}");
        ini.AppendLine($"BufferSize={config.Performance?.BufferSize ?? 65536}");
        ini.AppendLine($"ThreadPool={config.Performance?.ThreadPool ?? "auto"}");
        ini.AppendLine();
        
        ini.AppendLine("[Redundancy]");
        ini.AppendLine($"HeartbeatInterval={config.Redundancy?.HeartbeatInterval ?? 1000}");
        ini.AppendLine($"FailoverTimeout={config.Redundancy?.FailoverTimeout ?? 5000}");
        ini.AppendLine($"DataSyncMode={config.Redundancy?.DataSyncMode ?? "realtime"}");
        
        File.WriteAllText(configPath, ini.ToString(), Encoding.UTF8);
    }
    else
    {
        throw new NotSupportedException($"Unsupported configuration file format: {ext}");
    }
}

/// <summary>
/// 設定の妥当性を検証
/// </summary>
public bool ValidateConfiguration(Configuration config)
{
    if (config == null) return false;
    
    // General 検証
    if (config.General != null)
    {
        var validModes = new[] { "ActiveStandby", "LoadBalancing", "Standalone" };
        if (!string.IsNullOrWhiteSpace(config.General.Mode) && 
            !validModes.Contains(config.General.Mode))
        {
            return false;
        }
    }
    
    // Network 検証
    if (config.Network != null)
    {
        if (config.Network.FrameSize <= 0 || config.Network.FrameSize > 9000)
            return false;
        
        if (string.IsNullOrWhiteSpace(config.Network.Interface))
            return false;
    }
    
    // Security 検証
    if (config.Security != null)
    {
        if (config.Security.ScanTimeout < 0)
            return false;
        
        if (config.Security.EnableVirusScan && 
            string.IsNullOrWhiteSpace(config.Security.QuarantinePath))
        {
            return false;
        }
    }
    
    // Performance 検証
    if (config.Performance != null)
    {
        if (config.Performance.MaxMemoryMB <= 0 || config.Performance.MaxMemoryMB > 65536)
            return false;
        
        if (config.Performance.BufferSize <= 0)
            return false;
    }
    
    // Redundancy 検証
    if (config.Redundancy != null)
    {
        if (config.Redundancy.HeartbeatInterval < 100)
            return false;
        
        if (config.Redundancy.FailoverTimeout < config.Redundancy.HeartbeatInterval)
            return false;
    }
    
    return true;
}
```

---

## 4. FrameService の実装完了

### 問題
```
Error: 'FrameService' does not implement interface member 'IFrameService.CreateHeartbeatFrame(byte[])'
```

### 修正方法

`src/NonIPFileDelivery/Services/FrameService.cs` に以下のメソッドを追加:

```csharp
using System;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Utilities;

namespace NonIPFileDelivery.Services
{
    public class FrameService : IFrameService
    {
        private readonly ILoggingService _logger;
        private readonly ICryptoService _cryptoService;
        private int _sequenceNumber;

        public FrameService(ILoggingService logger, ICryptoService cryptoService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cryptoService = cryptoService ?? throw new ArgumentNullException(nameof(cryptoService));
            _sequenceNumber = 0;
        }

        // SerializeFrame と DeserializeFrame は既存のまま維持

        /// <summary>
        /// ハートビートフレームを作成
        /// </summary>
        public NonIPFrame CreateHeartbeatFrame(byte[] sourceMac)
        {
            if (sourceMac == null || sourceMac.Length != 6)
                throw new ArgumentException("Source MAC address must be 6 bytes", nameof(sourceMac));

            return new NonIPFrame
            {
                Header = new FrameHeader
                {
                    SourceMAC = sourceMac,
                    DestinationMAC = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, // Broadcast
                    Type = FrameType.Heartbeat,
                    SequenceNumber = (ushort)System.Threading.Interlocked.Increment(ref _sequenceNumber),
                    PayloadLength = 0,
                    Flags = FrameFlags.None,
                    Timestamp = DateTime.UtcNow
                },
                Payload = Array.Empty<byte>()
            };
        }

        /// <summary>
        /// データフレームを作成
        /// </summary>
        public NonIPFrame CreateDataFrame(byte[] sourceMac, byte[] destinationMac, byte[] data, FrameFlags flags = FrameFlags.None)
        {
            if (sourceMac == null || sourceMac.Length != 6)
                throw new ArgumentException("Source MAC address must be 6 bytes", nameof(sourceMac));
            if (destinationMac == null || destinationMac.Length != 6)
                throw new ArgumentException("Destination MAC address must be 6 bytes", nameof(destinationMac));
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            return new NonIPFrame
            {
                Header = new FrameHeader
                {
                    SourceMAC = sourceMac,
                    DestinationMAC = destinationMac,
                    Type = FrameType.Data,
                    SequenceNumber = (ushort)System.Threading.Interlocked.Increment(ref _sequenceNumber),
                    PayloadLength = (ushort)data.Length,
                    Flags = flags,
                    Timestamp = DateTime.UtcNow
                },
                Payload = data
            };
        }

        /// <summary>
        /// ファイル転送フレームを作成
        /// </summary>
        public NonIPFrame CreateFileTransferFrame(byte[] sourceMac, byte[] destinationMac, FileTransferFrame fileData)
        {
            if (sourceMac == null || sourceMac.Length != 6)
                throw new ArgumentException("Source MAC address must be 6 bytes", nameof(sourceMac));
            if (destinationMac == null || destinationMac.Length != 6)
                throw new ArgumentException("Destination MAC address must be 6 bytes", nameof(destinationMac));
            if (fileData == null)
                throw new ArgumentNullException(nameof(fileData));

            // FileTransferFrame をバイト配列にシリアライズ（簡易実装）
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);
            
            writer.Write((byte)fileData.Operation);
            writer.Write(fileData.FileName ?? string.Empty);
            writer.Write(fileData.FileSize);
            writer.Write(fileData.ChunkIndex);
            writer.Write(fileData.TotalChunks);
            writer.Write(fileData.ChunkData?.Length ?? 0);
            if (fileData.ChunkData != null && fileData.ChunkData.Length > 0)
            {
                writer.Write(fileData.ChunkData);
            }
            writer.Write(fileData.FileHash ?? string.Empty);
            writer.Write(fileData.SessionId.ToByteArray());
            
            var payload = ms.ToArray();

            return new NonIPFrame
            {
                Header = new FrameHeader
                {
                    SourceMAC = sourceMac,
                    DestinationMAC = destinationMac,
                    Type = FrameType.FileTransfer,
                    SequenceNumber = (ushort)System.Threading.Interlocked.Increment(ref _sequenceNumber),
                    PayloadLength = (ushort)payload.Length,
                    Flags = FrameFlags.None,
                    SessionId = fileData.SessionId,
                    Timestamp = DateTime.UtcNow
                },
                Payload = payload
            };
        }

        /// <summary>
        /// フレームを検証
        /// </summary>
        public bool ValidateFrame(NonIPFrame frame, byte[] rawData)
        {
            if (frame == null || rawData == null)
                return false;

            try
            {
                // CRC32チェックサムの検証
                var dataWithoutChecksum = new byte[rawData.Length - 4];
                Buffer.BlockCopy(rawData, 0, dataWithoutChecksum, 0, dataWithoutChecksum.Length);
                
                var calculatedChecksum = Crc32Calculator.Calculate(dataWithoutChecksum);
                
                return calculatedChecksum == frame.Checksum;
            }
            catch (Exception ex)
            {
                _logger.Error($"Frame validation error: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// チェックサムを計算
        /// </summary>
        public uint CalculateChecksum(byte[] data)
        {
            return Crc32Calculator.Calculate(data);
        }

        // 既存の SerializeFrame と DeserializeFrame メソッドはそのまま維持
        // SerializeHeader, DeserializeHeader などのヘルパーメソッドも必要に応じて追加

        private byte[] SerializeHeader(FrameHeader header)
        {
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);
            
            // Ethernet Header
            writer.Write(header.DestinationMAC);
            writer.Write(header.SourceMAC);
            writer.Write(header.EtherType);
            
            // Custom Header
            writer.Write((byte)header.Type);
            writer.Write(header.SequenceNumber);
            writer.Write(header.PayloadLength);
            writer.Write((byte)header.Flags);
            
            return ms.ToArray();
        }

        private FrameHeader DeserializeHeader(byte[] data)
        {
            using var ms = new System.IO.MemoryStream(data);
            using var reader = new System.IO.BinaryReader(ms);
            
            var header = new FrameHeader
            {
                DestinationMAC = reader.ReadBytes(6),
                SourceMAC = reader.ReadBytes(6),
                EtherType = reader.ReadUInt16(),
                Type = (FrameType)reader.ReadByte(),
                SequenceNumber = reader.ReadUInt16(),
                PayloadLength = reader.ReadUInt16(),
                Flags = (FrameFlags)reader.ReadByte()
            };
            
            return header;
        }
    }
}
```

---

## 5. SecurityService の実装完了

### 問題
```
Error: 'SecurityService' does not implement interface member 'ISecurityService.IsSecurityEnabled'
```

### 修正方法

`src/NonIPFileDelivery/Services/SecurityService.cs` に以下のプロパティを追加:

```csharp
public class SecurityService : ISecurityService
{
    private readonly ILoggingService _logger;
    // ... 他のフィールド

    /// <summary>
    /// セキュリティ機能が有効かどうか
    /// </summary>
    public bool IsSecurityEnabled { get; private set; } = true;

    public SecurityService(ILoggingService logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // ... 他の初期化処理
    }

    /// <summary>
    /// セキュリティ機能の有効/無効を設定
    /// </summary>
    public void SetSecurityEnabled(bool enabled)
    {
        IsSecurityEnabled = enabled;
        _logger.Info($"Security features {(enabled ? "enabled" : "disabled")}");
    }

    // ... 他のメソッド
}
```

また、`ISecurityService` インターフェースにもプロパティが定義されているか確認してください:

```csharp
public interface ISecurityService
{
    bool IsSecurityEnabled { get; }
    // ... 他のメソッド
}
```

---

## 6. RetryPolicy の修正

### 問題
```
Error: The type or namespace name 'ILoggingService' could not be found
```

### 修正方法

`src/NonIPFileDelivery/Resilience/RetryPolicy.cs` のファイル先頭に using ディレクティブを追加:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NonIPFileDelivery.Services; // ← この行を追加

namespace NonIPFileDelivery.Resilience
{
    // ... 既存のコード
}
```

---

## 🧪 ビルド確認

すべての修正を適用した後、以下のコマンドでビルドを確認:

```bash
cd /home/runner/work/Non-IP-File-Delivery/Non-IP-File-Delivery
dotnet clean
dotnet restore
dotnet build
```

**期待される結果:**
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

---

## 📝 修正後のチェックリスト

- [ ] NonIPFileDelivery.csproj に libyara.NET パッケージを追加
- [ ] Crc32Calculator.CalculateComposite を修正
- [ ] ConfigurationService に SaveConfiguration を実装
- [ ] ConfigurationService に ValidateConfiguration を実装
- [ ] FrameService に CreateHeartbeatFrame を実装
- [ ] FrameService に CreateDataFrame を実装
- [ ] FrameService に CreateFileTransferFrame を実装
- [ ] FrameService に ValidateFrame を実装
- [ ] FrameService に CalculateChecksum を実装
- [ ] SecurityService に IsSecurityEnabled プロパティを追加
- [ ] RetryPolicy に using ディレクティブを追加
- [ ] `dotnet build` が成功することを確認

---

## ⚠️ 注意事項

### YARAスキャナーについて

`libyara.NET` パッケージが NuGet で利用できない、または動作しない場合は、以下のいずれかの対応を検討してください:

1. **代替パッケージの使用**
   ```xml
   <PackageReference Include="dnYara" Version="3.2.0" />
   ```

2. **YARAスキャナーの無効化**（一時的）
   - `YARAScanner.cs` をプロジェクトから除外
   - `SecurityService` で YARA スキャンを呼び出している箇所をコメントアウト

3. **モックの実装**
   ```csharp
   // YARAScanner のモック実装
   public class MockYARAScanner
   {
       public Task<YARAScanResult> ScanAsync(byte[] data, int timeoutMs = 5000)
       {
           return Task.FromResult(new YARAScanResult { IsMatch = false });
       }
   }
   ```

---

**最終更新**: 2025-01-10
