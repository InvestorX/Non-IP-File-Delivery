// FrameService.cs（修正箇所のみ抜粋）

public class FrameService : IFrameService
{
    private readonly ILoggingService _logger;
    private readonly ICryptoService _cryptoService; // 🆕 追加
    private int _sequenceNumber;

    public FrameService(ILoggingService logger, ICryptoService cryptoService) // 🆕 修正
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cryptoService = cryptoService ?? throw new ArgumentNullException(nameof(cryptoService)); // 🆕 追加
        _sequenceNumber = 0;
    }

    /// <summary>
    /// フレームをシリアライズ（暗号化統合）
    /// </summary>
    public byte[] SerializeFrame(NonIPFrame frame)
    {
        if (frame == null)
            throw new ArgumentNullException(nameof(frame));

        // 🆕 暗号化処理追加
        if ((frame.Header.Flags & FrameFlags.Encrypted) != 0)
        {
            _logger.Debug($"Encrypting frame payload ({frame.Payload.Length} bytes)");
            frame.Payload = _cryptoService.Encrypt(frame.Payload);
            _logger.Debug($"Payload encrypted ({frame.Payload.Length} bytes)");
        }

        // フレーム構築
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Ethernet Header
        writer.Write(frame.Header.DestinationMAC);
        writer.Write(frame.Header.SourceMAC);
        writer.Write((ushort)0x88B5); // EtherType

        // Custom Protocol Header
        writer.Write((byte)frame.Header.Type);
        writer.Write((ushort)frame.Header.SequenceNumber);
        writer.Write((ushort)frame.Payload.Length);
        writer.Write((byte)frame.Header.Flags);

        // Payload
        writer.Write(frame.Payload);

        var frameData = ms.ToArray();

        // CRC32計算
        var checksum = Crc32Calculator.Calculate(frameData);
        writer.Write(checksum);

        var finalFrame = ms.ToArray();

        _logger.Debug($"Frame serialized: SeqNum={frame.Header.SequenceNumber}, PayloadSize={frame.Payload.Length}, TotalSize={finalFrame.Length}");

        return finalFrame;
    }

    /// <summary>
    /// フレームをデシリアライズ（復号化統合）
    /// </summary>
    public NonIPFrame? DeserializeFrame(byte[] data)
    {
        if (data == null || data.Length < 24) // 最小フレームサイズ
            return null;

        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            // Ethernet Header
            var destMac = reader.ReadBytes(6);
            var srcMac = reader.ReadBytes(6);
            var etherType = reader.ReadUInt16();

            if (etherType != 0x88B5)
            {
                _logger.Warning($"Invalid EtherType: 0x{etherType:X4}");
                return null;
            }

            // Custom Protocol Header
            var type = (FrameType)reader.ReadByte();
            var seqNum = reader.ReadUInt16();
            var payloadLength = reader.ReadUInt16();
            var flags = (FrameFlags)reader.ReadByte();

            // Payload
            var payload = reader.ReadBytes(payloadLength);

            // CRC32検証
            var storedChecksum = reader.ReadUInt32();
            var calculatedChecksum = Crc32Calculator.Calculate(data.Take(data.Length - 4).ToArray());

            if (storedChecksum != calculatedChecksum)
            {
                _logger.Error($"CRC32 mismatch: stored=0x{storedChecksum:X8}, calculated=0x{calculatedChecksum:X8}");
                return null;
            }

            // 🆕 復号化処理追加
            if ((flags & FrameFlags.Encrypted) != 0)
            {
                _logger.Debug($"Decrypting frame payload ({payload.Length} bytes)");
                payload = _cryptoService.Decrypt(payload);
                _logger.Debug($"Payload decrypted ({payload.Length} bytes)");
            }

            var frame = new NonIPFrame
            {
                Header = new FrameHeader
                {
                    DestinationMAC = destMac,
                    SourceMAC = srcMac,
                    Type = type,
                    SequenceNumber = seqNum,
                    Flags = flags
                },
                Payload = payload
            };

            _logger.Debug($"Frame deserialized: SeqNum={seqNum}, PayloadSize={payload.Length}");

            return frame;
        }
        catch (Exception ex)
        {
            _logger.Error($"Frame deserialization error: {ex.Message}", ex);
            return null;
        }
    }

    // ... その他のメソッドは既存のまま
}
