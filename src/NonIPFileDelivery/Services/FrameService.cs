// FrameService.cs（既存ファイルに追加・修正）

using NonIPFileDelivery.Services;

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

        // ... 既存メソッド（CreateHeartbeatFrame, CreateDataFrame等）は変更なし
    }
}
