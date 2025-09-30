using System;
using System.IO;
using System.Text;
using System.Text.Json;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Utilities;

namespace NonIPFileDelivery.Services;

public class FrameService : IFrameService
{
    private readonly ILoggingService _logger;
    private ushort _sequenceNumber = 0;
    private readonly object _sequenceLock = new();

    public FrameService(ILoggingService logger)
    {
        _logger = logger;
    }

    public byte[] SerializeFrame(NonIPFrame frame)
    {
        try
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            // Ethernetヘッダー書き込み
            writer.Write(frame.Header.DestinationMac);
            writer.Write(frame.Header.SourceMac);
            writer.Write(frame.Header.EtherType);

            // カスタムプロトコルヘッダー書き込み
            writer.Write((byte)frame.Header.Type);
            writer.Write(frame.Header.SequenceNumber);
            writer.Write(frame.Header.PayloadLength);
            writer.Write((byte)frame.Header.Flags);

            // Write payload
            if (frame.Payload.Length > 0)
            {
                writer.Write(frame.Payload);
            }

            // CRC32チェックサム計算と書き込み（改善点）
            var frameData = stream.ToArray();
            var checksum = Crc32Calculator.Calculate(frameData);
            writer.Write(checksum);

            var finalData = stream.ToArray();
            _logger.Debug($"Serialized frame: Type={frame.Header.Type}, Length={finalData.Length}, Checksum=0x{checksum:X8}");

            return finalData;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to serialize frame: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// バイト配列からフレームをデシリアライズ
    /// </summary>
    public NonIPFrame? DeserializeFrame(byte[] data)
    {
        try
        {
            if (data.Length < 25) // Minimum frame size
            {
                _logger.Warning($"Frame too small: {data.Length} bytes");
                return null;
            }

            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            var frame = new NonIPFrame();

            // Read Ethernet header
            frame.Header.DestinationMac = reader.ReadBytes(6);
            frame.Header.SourceMac = reader.ReadBytes(6);
            frame.Header.EtherType = reader.ReadUInt16();

            // Validate EtherType
            if (frame.Header.EtherType != 0x88B5)
            {
                _logger.Warning($"Invalid EtherType: 0x{frame.Header.EtherType:X4}");
                return null;
            }

            // Read custom protocol header
            frame.Header.Type = (FrameType)reader.ReadByte();
            frame.Header.SequenceNumber = reader.ReadUInt16();
            frame.Header.PayloadLength = reader.ReadUInt16();
            frame.Header.Flags = (FrameFlags)reader.ReadByte();

            // Read payload
            if (frame.Header.PayloadLength > 0)
            {
                var remainingData = (int)(stream.Length - stream.Position - 4); // Subtract checksum size
                var payloadSize = Math.Min(frame.Header.PayloadLength, remainingData);
                frame.Payload = reader.ReadBytes(payloadSize);
            }

            // Read checksum
            frame.Checksum = reader.ReadUInt32();

             // CRC32チェックサム検証（改善点）
            var frameDataWithoutChecksum = new byte[data.Length - 4];
            Array.Copy(data, 0, frameDataWithoutChecksum, 0, frameDataWithoutChecksum.Length);
            var calculatedChecksum = Crc32Calculator.Calculate(frameDataWithoutChecksum);

           if (calculatedChecksum != frame.Checksum)
            {
                _logger.Warning($"CRC32 mismatch: calculated=0x{calculatedChecksum:X8}, received=0x{frame.Checksum:X8}");
                return null;
            }

            _logger.Debug($"Deserialized frame: Type={frame.Header.Type}, Seq={frame.Header.SequenceNumber}, PayloadLen={frame.Header.PayloadLength}");
            return frame;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to deserialize frame: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// ハートビートフレーム作成
    /// </summary>
    public NonIPFrame CreateHeartbeatFrame(byte[] sourceMac)
    {
        var frame = new NonIPFrame
        {
            Header = new FrameHeader
            {
                DestinationMac = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, // Broadcast
                SourceMac = sourceMac,
                Type = FrameType.Heartbeat,
                SequenceNumber = ++_sequenceNumber,
                Flags = FrameFlags.Broadcast
            }
        };

        var heartbeatData = JsonSerializer.Serialize(new
        {
            Timestamp = DateTime.UtcNow,
            Version = "1.1.0",
            Status = "Active"
        });

        frame.Payload = Encoding.UTF8.GetBytes(heartbeatData);
        frame.Header.PayloadLength = (ushort)frame.Payload.Length;

        return frame;
    }

    /// <summary>
    /// データフレーム作成
    /// </summary>
    public NonIPFrame CreateDataFrame(byte[] sourceMac, byte[] destinationMac, byte[] data, FrameFlags flags = FrameFlags.None)
    {
        var frame = new NonIPFrame
        {
            Header = new FrameHeader
            {
                DestinationMac = destinationMac,
                SourceMac = sourceMac,
                Type = FrameType.Data,
                SequenceNumber = ++_sequenceNumber,
                PayloadLength = (ushort)data.Length,
                Flags = flags
            },
            Payload = data
        };

        return frame;
    }

    /// <summary>
    /// ファイル転送フレーム作成
    /// </summary>
    public NonIPFrame CreateFileTransferFrame(byte[] sourceMac, byte[] destinationMac, FileTransferFrame fileData)
    {
        var frame = new NonIPFrame
        {
            Header = new FrameHeader
            {
                DestinationMac = destinationMac,
                SourceMac = sourceMac,
                Type = FrameType.FileTransfer,
                SequenceNumber = ++_sequenceNumber,
                Flags = FrameFlags.RequireAck
            }
        };

        var jsonData = JsonSerializer.Serialize(fileData);
        frame.Payload = Encoding.UTF8.GetBytes(jsonData);
        frame.Header.PayloadLength = (ushort)frame.Payload.Length;

        return frame;
    }

    /// <summary>
    /// フレーム検証（CRC32チェックサム含む）
    /// </summary>
    public bool ValidateFrame(NonIPFrame frame, byte[] rawData)
    {
        try
        {
            // 基本検証
            if (frame.Header.EtherType != 0x88B5)
                return false;

            if (frame.Header.PayloadLength != frame.Payload.Length)
                return false;

            // CRC32チェックサム検証（改善点）
            var frameDataWithoutChecksum = new byte[rawData.Length - 4];
            Array.Copy(rawData, 0, frameDataWithoutChecksum, 0, frameDataWithoutChecksum.Length);
            var calculatedChecksum = Crc32Calculator.Calculate(frameDataWithoutChecksum);

            return calculatedChecksum == frame.Checksum;
        }
        catch (Exception ex)
        {
            _logger.Error($"Frame validation error: {ex.Message}");
            return false;
        }
    }

    // public uint CalculateChecksum(byte[] data)
    // {
    //     uint checksum = 0;
        
    //     for (int i = 0; i < data.Length; i += 4)
    //     {
    //         uint value = 0;
    //         for (int j = 0; j < 4 && i + j < data.Length; j++)
    //         {
    //             value |= (uint)(data[i + j] << (j * 8));
    //         }
    //         checksum ^= value;
    //     }

    //     // Simple hash mixing to improve distribution
    //     checksum ^= (checksum >> 16);
    //     checksum ^= (checksum >> 8);
        
    //     return checksum;
    // }

    /// <summary>
    /// スレッドセーフなシーケンス番号取得
    /// </summary>
    private ushort GetNextSequenceNumber()
    {
        lock (_sequenceLock)
        {
            return ++_sequenceNumber;
        }
    }

    /// <summary>
    /// CRC32チェックサム計算（互換性のため残す）
    /// </summary>
    [Obsolete("Use Crc32Calculator.Calculate instead")]
    public uint CalculateChecksum(byte[] data)
    {
        return Crc32Calculator.Calculate(data);
    }
    
}
