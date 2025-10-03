using NonIpFileDelivery.Security;
using System.Text;

namespace NonIpFileDelivery.Core;

/// <summary>
/// 暗号化されたRaw Ethernetフレームのプロトコル定義
/// </summary>
public class SecureFrame
{
    // フレームヘッダー構造
    private const int VERSION_SIZE = 1;           // プロトコルバージョン
    private const int SESSION_ID_SIZE = 16;       // セッションID（GUID）
    private const int SEQUENCE_SIZE = 8;          // シーケンス番号
    private const int TIMESTAMP_SIZE = 8;         // タイムスタンプ（UnixTime）
    private const int PROTOCOL_TYPE_SIZE = 1;     // プロトコル種別
    private const int FLAGS_SIZE = 2;             // フラグ
    
    public const int HEADER_SIZE = VERSION_SIZE + SESSION_ID_SIZE + SEQUENCE_SIZE + 
                                   TIMESTAMP_SIZE + PROTOCOL_TYPE_SIZE + FLAGS_SIZE; // 36バイト

    // プロトコルバージョン
    public const byte PROTOCOL_VERSION = 0x01;

    // プロトコル種別
    public enum ProtocolType : byte
    {
        FtpControl = 0x01,
        FtpData = 0x02,
        SftpControl = 0x03,
        SftpData = 0x04,
        PostgreSql = 0x05,
        Heartbeat = 0xFE,
        ControlMessage = 0xFF
    }

    // フラグ定義
    [Flags]
    public enum FrameFlags : ushort
    {
        None = 0x0000,
        Compressed = 0x0001,      // ペイロードが圧縮されている
        Fragmented = 0x0002,      // フラグメント化されている
        LastFragment = 0x0004,    // 最後のフラグメント
        HighPriority = 0x0008,    // 高優先度
        RequireAck = 0x0010,      // ACK要求
    }

    // フレームプロパティ
    public byte Version { get; set; }
    public Guid SessionId { get; set; }
    public long SequenceNumber { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public ProtocolType Protocol { get; set; }
    public FrameFlags Flags { get; set; }
    public byte[] Payload { get; set; }

    public SecureFrame()
    {
        Version = PROTOCOL_VERSION;
        SessionId = Guid.NewGuid();
        SequenceNumber = 0;
        Timestamp = DateTimeOffset.UtcNow;
        Protocol = ProtocolType.FtpControl;
        Flags = FrameFlags.None;
        Payload = Array.Empty<byte>();
    }

    /// <summary>
    /// フレームを暗号化してバイト配列にシリアライズ
    /// </summary>
    /// <param name="cryptoEngine">暗号化エンジン</param>
    /// <returns>暗号化されたフレームデータ</returns>
    public byte[] Serialize(CryptoEngine cryptoEngine)
    {
        // ヘッダーを構築（平文）
        var header = new byte[HEADER_SIZE];
        var offset = 0;

        header[offset++] = Version;
        SessionId.ToByteArray().CopyTo(header, offset);
        offset += SESSION_ID_SIZE;
        BitConverter.GetBytes(SequenceNumber).CopyTo(header, offset);
        offset += SEQUENCE_SIZE;
        BitConverter.GetBytes(Timestamp.ToUnixTimeSeconds()).CopyTo(header, offset);
        offset += TIMESTAMP_SIZE;
        header[offset++] = (byte)Protocol;
        BitConverter.GetBytes((ushort)Flags).CopyTo(header, offset);

        // ペイロードを暗号化（ヘッダーをAADとして使用）
        var encryptedPayload = cryptoEngine.Encrypt(Payload, header);

        // ヘッダー + 暗号化ペイロード
        var frame = new byte[HEADER_SIZE + encryptedPayload.Length];
        header.CopyTo(frame, 0);
        encryptedPayload.CopyTo(frame, HEADER_SIZE);

        return frame;
    }

    /// <summary>
    /// 暗号化されたバイト配列からフレームをデシリアライズ
    /// </summary>
    /// <param name="data">暗号化フレームデータ</param>
    /// <param name="cryptoEngine">暗号化エンジン</param>
    /// <returns>復号化されたフレーム</returns>
    public static SecureFrame Deserialize(byte[] data, CryptoEngine cryptoEngine)
    {
        if (data.Length < HEADER_SIZE)
            throw new ArgumentException("Frame data too short");

        var frame = new SecureFrame();
        var offset = 0;

        // ヘッダー解析
        frame.Version = data[offset++];
        
        if (frame.Version != PROTOCOL_VERSION)
            throw new NotSupportedException($"Unsupported protocol version: {frame.Version}");

        var sessionIdBytes = new byte[SESSION_ID_SIZE];
        Array.Copy(data, offset, sessionIdBytes, 0, SESSION_ID_SIZE);
        frame.SessionId = new Guid(sessionIdBytes);
        offset += SESSION_ID_SIZE;

        frame.SequenceNumber = BitConverter.ToInt64(data, offset);
        offset += SEQUENCE_SIZE;

        var unixTime = BitConverter.ToInt64(data, offset);
        frame.Timestamp = DateTimeOffset.FromUnixTimeSeconds(unixTime);
        offset += TIMESTAMP_SIZE;

        frame.Protocol = (ProtocolType)data[offset++];
        frame.Flags = (FrameFlags)BitConverter.ToUInt16(data, offset);
        offset += FLAGS_SIZE;

        // 暗号化ペイロードを抽出
        var encryptedPayload = new byte[data.Length - HEADER_SIZE];
        Array.Copy(data, HEADER_SIZE, encryptedPayload, 0, encryptedPayload.Length);

        // ヘッダーをAADとして復号化
        var header = new byte[HEADER_SIZE];
        Array.Copy(data, 0, header, 0, HEADER_SIZE);
        frame.Payload = cryptoEngine.Decrypt(encryptedPayload, header);

        return frame;
    }

    /// <summary>
    /// フレーム情報を文字列表現で取得
    /// </summary>
    public override string ToString()
    {
        return $"SecureFrame [Session={SessionId:N}, Seq={SequenceNumber}, " +
               $"Protocol={Protocol}, Flags={Flags}, PayloadSize={Payload.Length}]";
    }
}