using System;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Utilities;

namespace NonIPFileDelivery.Services
{
    public class FrameService : IFrameService
    {
        private readonly ILoggingService _logger;
        private readonly ICryptoService _cryptoService; // 🆕 追加
        private int _sequenceNumber;

        // 🆕 コンストラクタ修正（ICryptoServiceを追加）
        public FrameService(ILoggingService logger, ICryptoService cryptoService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cryptoService = cryptoService ?? throw new ArgumentNullException(nameof(cryptoService));
            _sequenceNumber = 0;
        }

        /// <summary>
        /// フレームをシリアライズ（暗号化対応）
        /// </summary>
        public byte[] SerializeFrame(NonIPFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            try
            {
                // 🆕 暗号化フラグが立っている場合、Payloadを暗号化
                if ((frame.Header.Flags & FrameFlags.Encrypted) != 0)
                {
                    _logger.Debug($"Encrypting frame payload ({frame.Payload.Length} bytes)");
                    frame.Payload = _cryptoService.Encrypt(frame.Payload);
                    _logger.Debug($"Encrypted payload size: {frame.Payload.Length} bytes");
                }

                // フレームのシリアライズ（既存ロジック）
                var headerBytes = SerializeHeader(frame.Header);
                var frameData = new byte[headerBytes.Length + frame.Payload.Length];
                
                Buffer.BlockCopy(headerBytes, 0, frameData, 0, headerBytes.Length);
                Buffer.BlockCopy(frame.Payload, 0, frameData, headerBytes.Length, frame.Payload.Length);

                // CRC32計算
                var checksum = Crc32Calculator.Calculate(frameData);
                var result = new byte[frameData.Length + 4];
                
                Buffer.BlockCopy(frameData, 0, result, 0, frameData.Length);
                Buffer.BlockCopy(BitConverter.GetBytes(checksum), 0, result, frameData.Length, 4);

                _logger.Debug($"Frame serialized: {result.Length} bytes (Checksum: 0x{checksum:X8})");
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Frame serialization failed: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// フレームをデシリアライズ（復号化対応）
        /// </summary>
        public NonIPFrame? DeserializeFrame(byte[] data)
        {
            if (data == null || data.Length < 24) // 最小フレームサイズ
                return null;

            try
            {
                // CRC32検証（既存ロジック）
                var receivedChecksum = BitConverter.ToUInt32(data, data.Length - 4);
                var frameData = new byte[data.Length - 4];
                Buffer.BlockCopy(data, 0, frameData, 0, frameData.Length);
                var calculatedChecksum = Crc32Calculator.Calculate(frameData);

                if (receivedChecksum != calculatedChecksum)
                {
                    _logger.Warning($"CRC32 mismatch: expected 0x{receivedChecksum:X8}, got 0x{calculatedChecksum:X8}");
                    return null;
                }

                // ヘッダー解析（既存ロジック）
                var header = DeserializeHeader(frameData);
                var payload = new byte[header.PayloadLength];
                Buffer.BlockCopy(frameData, 20, payload, 0, payload.Length); // Ethernet(14) + CustomHeader(6)

                // 🆕 暗号化フラグが立っている場合、Payloadを復号化
                if ((header.Flags & FrameFlags.Encrypted) != 0)
                {
                    _logger.Debug($"Decrypting frame payload ({payload.Length} bytes)");
                    payload = _cryptoService.Decrypt(payload);
                    _logger.Debug($"Decrypted payload size: {payload.Length} bytes");
                }

                return new NonIPFrame
                {
                    Header = header,
                    Payload = payload,
                    Checksum = receivedChecksum
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Frame deserialization failed: {ex.Message}", ex);
                return null;
            }
        }

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
