using System;

namespace NonIPFileDelivery.Models;

/// <summary>
/// Represents a Non-IP Ethernet frame with custom protocol
/// Phase 3: セッション管理・フラグメント処理対応
/// </summary>
public class NonIPFrame
{
    public FrameHeader Header { get; set; } = new();
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public uint Checksum { get; set; }
}

/// <summary>
/// フレームヘッダー（Phase 3拡張版）
/// </summary>
public class FrameHeader
{
    public byte[] DestinationMAC { get; set; } = new byte[6];
    public byte[] SourceMAC { get; set; } = new byte[6];
    public ushort EtherType { get; set; } = 0x88B5; // Custom EtherType
    public FrameType Type { get; set; }
    public ushort SequenceNumber { get; set; }
    public ushort PayloadLength { get; set; }
    public FrameFlags Flags { get; set; }
    
    // ✅ Phase 3で追加: セッション管理
    public Guid SessionId { get; set; } = Guid.Empty;
    
    // ✅ Phase 3で追加: 接続ID（Connection識別）
    public ulong ConnectionId { get; set; } = 0;
    
    // ✅ Phase 3で追加: フラグメント情報
    public FragmentInfo? FragmentInfo { get; set; }
    
    // ✅ Phase 3で追加: タイムスタンプ（UTC）
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// フレームタイプ
/// </summary>
public enum FrameType : byte
{
    Data = 0x01,
    Heartbeat = 0x02,
    Acknowledgment = 0x03,
    Control = 0x04,
    FileTransfer = 0x10,
    SecurityScan = 0x20,
    SessionStart = 0x30,      // ✅ Phase 3追加: セッション開始
    SessionEnd = 0x31,        // ✅ Phase 3追加: セッション終了
    Fragment = 0x40,          // ✅ Phase 3追加: フラグメント
    Error = 0xFF
}

/// <summary>
/// フレームフラグ
/// </summary>
[Flags]
public enum FrameFlags : byte
{
    None = 0x00,
    Encrypted = 0x01,
    Compressed = 0x02,
    Priority = 0x04,
    FragmentStart = 0x08,     // フラグメント開始
    FragmentEnd = 0x10,       // フラグメント終了
    RequireAck = 0x20,        // ACK必須
    Broadcast = 0x40,
    Reserved = 0x80
}

/// <summary>
/// ファイル転送フレーム
/// </summary>
public class FileTransferFrame
{
    public FileOperation Operation { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public uint ChunkIndex { get; set; }
    public uint TotalChunks { get; set; }
    public byte[] ChunkData { get; set; } = Array.Empty<byte>();
    public string FileHash { get; set; } = string.Empty;
    
    // ✅ Phase 3追加: セッションID
    public Guid SessionId { get; set; } = Guid.Empty;
}

/// <summary>
/// ファイル操作種別
/// </summary>
public enum FileOperation : byte
{
    Start = 0x01,
    Data = 0x02,
    End = 0x03,
    Abort = 0x04,
    Resume = 0x05
}