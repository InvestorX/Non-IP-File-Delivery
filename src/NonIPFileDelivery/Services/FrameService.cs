using System.Text;
using System.Text.Json;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Utilities;

namespace NonIPFileDelivery.Services;

public class FrameService : IFrameService
{
    private readonly ILoggingService _logger;
    private ushort _sequenceNumber;
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

            // Ethernet header
            writer.Write(frame.Header.DestinationMac);
            writer.Write(frame.Header.SourceMac);
            writer.Write(frame.Header.EtherType);

            // Custom header
            writer.Write((byte)frame.Header.Type);
            writer.Write(frame.Header.SequenceNumber);
            writer.Write(frame.Header.PayloadLength);
            writer.Write((byte)frame.Header.Flags);

            // Payload
            if (frame.Payload.Length > 0)
            {
                writer.Write(frame.Payload);
            }

            // CRC32 over all previous bytes
            var frameData = stream.ToArray();
            var checksum = Crc32Calculator.Calculate(frameData);
            writer.Write(checksum);

            var finalData = stream.ToArray();
            _logger.Debug($"Serialized frame: Type={frame.Header.Type}, Length={finalData.Length}, CRC32=0x{checksum:X8}");
            return finalData;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to serialize frame: {ex.Message}");
            throw;
        }
    }

    public NonIPFrame? DeserializeFrame(byte[] data)
    {
        try
        {
            if (data.Length < 25)
            {
                _logger.Warning($"Frame too small: {data.Length} bytes");
                return null;
            }

            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            var frame = new NonIPFrame();

            // Ethernet header
            frame.Header.DestinationMac = reader.ReadBytes(6);
            frame.Header.SourceMac = reader.ReadBytes(6);
            frame.Header.EtherType = reader.ReadUInt16();

            if (frame.Header.EtherType != 0x88B5)
            {
                _logger.Warning($"Invalid EtherType: 0x{frame.Header.EtherType:X4}");
                return null;
            }

            // Custom header
            frame.Header.Type = (FrameType)reader.ReadByte();
            frame.Header.SequenceNumber = reader.ReadUInt16();
            frame.Header.PayloadLength = reader.ReadUInt16();
            frame.Header.Flags = (FrameFlags)reader.ReadByte();

            // Payload
            if (frame.Header.PayloadLength > 0)
            {
                var remaining = (int)(stream.Length - stream.Position - 4);
                var size = Math.Min(frame.Header.PayloadLength, remaining);
                frame.Payload = reader.ReadBytes(size);
            }

            // CRC32
            frame.Checksum = reader.ReadUInt32();

            var withoutCrc = new byte[data.Length - 4];
            Array.Copy(data, 0, withoutCrc, 0, withoutCrc.Length);
            var calculated = Crc32Calculator.Calculate(withoutCrc);

            if (calculated != frame.Checksum)
            {
                _logger.Warning($"CRC32 mismatch: calculated=0x{calculated:X8}, received=0x{frame.Checksum:X8}");
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

    public NonIPFrame CreateHeartbeatFrame(byte[] sourceMac)
    {
        var frame = new NonIPFrame
        {
            Header = new FrameHeader
            {
                DestinationMac = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF },
                SourceMac = sourceMac,
                Type = FrameType.Heartbeat,
                SequenceNumber = GetNextSequenceNumber(),
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

    public NonIPFrame CreateDataFrame(byte[] sourceMac, byte[] destinationMac, byte[] data, FrameFlags flags = FrameFlags.None)
    {
        return new NonIPFrame
        {
            Header = new FrameHeader
            {
                DestinationMac = destinationMac,
                SourceMac = sourceMac,
                Type = FrameType.Data,
                SequenceNumber = GetNextSequenceNumber(),
                PayloadLength = (ushort)data.Length,
                Flags = flags
            },
            Payload = data
        };
    }

    public NonIPFrame CreateFileTransferFrame(byte[] sourceMac, byte[] destinationMac, FileTransferFrame fileData)
    {
        var frame = new NonIPFrame
        {
            Header = new FrameHeader
            {
                DestinationMac = destinationMac,
                SourceMac = sourceMac,
                Type = FrameType.FileTransfer,
                SequenceNumber = GetNextSequenceNumber(),
                Flags = FrameFlags.RequireAck
            }
        };

        var json = JsonSerializer.Serialize(fileData);
        frame.Payload = Encoding.UTF8.GetBytes(json);
        frame.Header.PayloadLength = (ushort)frame.Payload.Length;
        return frame;
    }

    public bool ValidateFrame(NonIPFrame frame, byte[] rawData)
    {
        try
        {
            if (frame.Header.EtherType != 0x88B5) return false;
            if (frame.Header.PayloadLength != frame.Payload.Length) return false;

            var withoutCrc = new byte[rawData.Length - 4];
            Array.Copy(rawData, 0, withoutCrc, 0, withoutCrc.Length);
            var calculated = Crc32Calculator.Calculate(withoutCrc);
            return calculated == frame.Checksum;
        }
        catch (Exception ex)
        {
            _logger.Error($"Frame validation error: {ex.Message}");
            return false;
        }
    }

    private ushort GetNextSequenceNumber()
    {
        lock (_sequenceLock)
        {
            return ++_sequenceNumber;
        }
    }
}
