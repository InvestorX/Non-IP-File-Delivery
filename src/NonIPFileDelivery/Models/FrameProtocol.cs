using System;

namespace NonIPFileDelivery.Models;

/// <summary>
/// Represents a Non-IP Ethernet frame with custom protocol
/// </summary>
public class NonIPFrame
{
    public FrameHeader Header { get; set; } = new();
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public uint Checksum { get; set; }
}

public class FrameHeader
{
    public byte[] DestinationMac { get; set; } = new byte[6];
    public byte[] SourceMac { get; set; } = new byte[6];
    public ushort EtherType { get; set; } = 0x88B5; // Custom EtherType
    public FrameType Type { get; set; }
    public ushort SequenceNumber { get; set; }
    public ushort PayloadLength { get; set; }
    public FrameFlags Flags { get; set; }
}

public enum FrameType : byte
{
    Data = 0x01,
    Heartbeat = 0x02,
    Acknowledgment = 0x03,
    Control = 0x04,
    FileTransfer = 0x10,
    SecurityScan = 0x20,
    Error = 0xFF
}

[Flags]
public enum FrameFlags : byte
{
    None = 0x00,
    Encrypted = 0x01,
    Compressed = 0x02,
    Priority = 0x04,
    FragmentStart = 0x08,
    FragmentEnd = 0x10,
    RequireAck = 0x20,
    Broadcast = 0x40,
    Reserved = 0x80
}

public class FileTransferFrame
{
    public FileOperation Operation { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public uint ChunkIndex { get; set; }
    public uint TotalChunks { get; set; }
    public byte[] ChunkData { get; set; } = Array.Empty<byte>();
    public string FileHash { get; set; } = string.Empty;
}

public enum FileOperation : byte
{
    Start = 0x01,
    Data = 0x02,
    End = 0x03,
    Abort = 0x04,
    Resume = 0x05
}