using System.Buffers.Binary;
using Serilog;

namespace NonIpFileDelivery.Core;

/// <summary>
/// 暗号化されたRaw Ethernetフレームの構造
/// </summary>
public class SecureEthernetFrame
{
    // フレーム構造: [Header(16) | EncryptedPayload(N) | CRC32(4)]
    private const int HEADER_SIZE = 16;
    private const int CRC_SIZE = 4;
    private const int MIN_FRAME_SIZE = HEADER_SIZE + CRC_SIZE;

    /// <summary>
    /// フレームヘッダー
    /// </summary>
    public struct FrameHeader
    {
        public ushort Version;           // プロトコルバージョン（現在: 1）
        public byte ProtocolType;        // プロトコル種別（FTP=1, SFTP=2, PostgreSQL=3）
        public byte Flags;               // フラグ（暗号化=0x01, 圧縮=0x02）
        public uint SequenceNumber;      // シーケンス番号（再送・順序制御）
        public uint Timestamp;           // Unixタイムスタンプ（秒）
        public uint PayloadLength;       // ペイロード長（暗号化後）
    }

    public FrameHeader Header { get; set; }
    public byte[] EncryptedPayload { get; set; }
    public uint Crc32 { get; set; }

    public SecureEthernetFrame()
    {
        Header = new FrameHeader
        {
            Version = 1,
            Flags = 0x01 // デフォルトで暗号化有効
        };
        EncryptedPayload = Array.Empty<byte>();
    }

    /// <summary>
    /// 平文ペイロードから暗号化フレームを構築
    /// </summary>
    /// <param name="plainPayload">平文ペイロード</param>
    /// <param name="cryptoEngine">暗号化エンジン</param>
    /// <param name="protocolType">プロトコル種別</param>
    /// <param name="sequenceNumber">シーケンス番号</param>
    /// <returns>暗号化されたフレーム</returns>
    public static SecureEthernetFrame CreateEncrypted(
        byte[] plainPayload,
        Security.CryptoEngine cryptoEngine,
        byte protocolType,
        uint sequenceNumber)
    {
        if (plainPayload == null || plainPayload.Length == 0)
        {
            throw new ArgumentException("Payload cannot be null or empty", nameof(plainPayload));
        }

        var frame = new SecureEthernetFrame
        {
            Header = new FrameHeader
            {
                Version = 1,
                ProtocolType = protocolType,
                Flags = 0x01, // 暗号化フラグ
                SequenceNumber = sequenceNumber,
                Timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                PayloadLength = 0 // 暗号化後に設定
            }
        };

        // 関連データとしてヘッダーを含める（改ざん検知）
        var associatedData = SerializeHeader(frame.Header);

        // ペイロードを暗号化
        frame.EncryptedPayload = cryptoEngine.Encrypt(plainPayload, associatedData);
        frame.Header.PayloadLength = (uint)frame.EncryptedPayload.Length;

        // CRC32チェックサムを計算（全体の整合性検証）
        frame.Crc32 = CalculateCrc32(frame);

        Log.Debug("Created encrypted frame: Seq={Seq}, Protocol={Proto}, Size={Size}",
            sequenceNumber, protocolType, frame.EncryptedPayload.Length);

        return frame;
    }

    /// <summary>
    /// 暗号化フレームから平文ペイロードを復号化
    /// </summary>
    /// <param name="cryptoEngine">暗号化エンジン</param>
    /// <returns>復号化された平文ペイロード</returns>
    public byte[] DecryptPayload(Security.CryptoEngine cryptoEngine)
    {
        if ((Header.Flags & 0x01) == 0)
        {
            // 暗号化されていない場合はそのまま返す
            Log.Warning("Frame is not encrypted: Seq={Seq}", Header.SequenceNumber);
            return EncryptedPayload;
        }

        // CRC32検証
        var expectedCrc = CalculateCrc32(this);
        if (expectedCrc != Crc32)
        {
            throw new InvalidDataException($"CRC32 mismatch: Expected={expectedCrc:X8}, Actual={Crc32:X8}");
        }

        // 関連データとしてヘッダーを含める
        var associatedData = SerializeHeader(Header);

        // ペイロードを復号化
        var plainPayload = cryptoEngine.Decrypt(EncryptedPayload, associatedData);

        Log.Debug("Decrypted frame: Seq={Seq}, Protocol={Proto}, Size={Size}",
            Header.SequenceNumber, Header.ProtocolType, plainPayload.Length);

        return plainPayload;
    }

    /// <summary>
    /// フレームをバイト配列にシリアライズ
    /// </summary>
    /// <returns>Raw Ethernetフレームデータ</returns>
    public byte[] Serialize()
    {
        var frameSize = HEADER_SIZE + EncryptedPayload.Length + CRC_SIZE;
        var buffer = new byte[frameSize];

        // ヘッダーをシリアライズ
        var headerBytes = SerializeHeader(Header);
        headerBytes.CopyTo(buffer, 0);

        // ペイロードをコピー
        EncryptedPayload.CopyTo(buffer, HEADER_SIZE);

        // CRC32を追加
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(HEADER_SIZE + EncryptedPayload.Length),
            Crc32
        );

        return buffer;
    }

    /// <summary>
    /// バイト配列からフレームをデシリアライズ
    /// </summary>
    /// <param name="data">Raw Ethernetフレームデータ</param>
    /// <returns>デシリアライズされたフレーム</returns>
    public static SecureEthernetFrame Deserialize(byte[] data)
    {
        if (data == null || data.Length < MIN_FRAME_SIZE)
        {
            throw new ArgumentException($"Invalid frame data: Minimum size is {MIN_FRAME_SIZE} bytes", nameof(data));
        }

        var frame = new SecureEthernetFrame();

        // ヘッダーをデシリアライズ
        frame.Header = DeserializeHeader(data.AsSpan(0, HEADER_SIZE));

        // ペイロードを抽出
        var payloadLength = (int)frame.Header.PayloadLength;
        if (data.Length < HEADER_SIZE + payloadLength + CRC_SIZE)
        {
            throw new InvalidDataException("Frame data is truncated");
        }

        frame.EncryptedPayload = new byte[payloadLength];
        Array.Copy(data, HEADER_SIZE, frame.EncryptedPayload, 0, payloadLength);

        // CRC32を抽出
        frame.Crc32 = BinaryPrimitives.ReadUInt32LittleEndian(
            data.AsSpan(HEADER_SIZE + payloadLength)
        );

        return frame;
    }

    /// <summary>
    /// ヘッダーをバイト配列にシリアライズ
    /// </summary>
    private static byte[] SerializeHeader(FrameHeader header)
    {
        var buffer = new byte[HEADER_SIZE];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt16LittleEndian(span[0..], header.Version);
        buffer[2] = header.ProtocolType;
        buffer[3] = header.Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], header.SequenceNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], header.Timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], header.PayloadLength);

        return buffer;
    }

    /// <summary>
    /// バイト配列からヘッダーをデシリアライズ
    /// </summary>
    private static FrameHeader DeserializeHeader(ReadOnlySpan<byte> data)
    {
        return new FrameHeader
        {
            Version = BinaryPrimitives.ReadUInt16LittleEndian(data[0..]),
            ProtocolType = data[2],
            Flags = data[3],
            SequenceNumber = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            Timestamp = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            PayloadLength = BinaryPrimitives.ReadUInt32LittleEndian(data[12..])
        };
    }

    /// <summary>
    /// CRC32チェックサムを計算
    /// </summary>
    private static uint CalculateCrc32(SecureEthernetFrame frame)
    {
        using var crc32 = new System.IO.Hashing.Crc32();

        var headerBytes = SerializeHeader(frame.Header);
        crc32.Append(headerBytes);
        crc32.Append(frame.EncryptedPayload);

        return BinaryPrimitives.ReadUInt32LittleEndian(crc32.GetCurrentHash());
    }
}