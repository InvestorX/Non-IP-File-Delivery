using System;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Utilities;

namespace NonIPFileDelivery.Services
{
    public class FrameService : IFrameService
    {
        private readonly ILoggingService _logger;
        private readonly ICryptoService _cryptoService;
        private readonly IFragmentationService _fragmentationService;
        private int _sequenceNumber;
        
        // ACK/NACK管理
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, DateTime> _pendingAcks;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, NonIPFrame> _retryQueue;
        private const int ACK_TIMEOUT_MS = 5000; // 5秒
        private const int MAX_RETRY_ATTEMPTS = 3;

        // コンストラクタ
        public FrameService(ILoggingService logger, ICryptoService cryptoService, IFragmentationService fragmentationService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cryptoService = cryptoService ?? throw new ArgumentNullException(nameof(cryptoService));
            _fragmentationService = fragmentationService ?? throw new ArgumentNullException(nameof(fragmentationService));
            _sequenceNumber = 0;
            _pendingAcks = new System.Collections.Concurrent.ConcurrentDictionary<ushort, DateTime>();
            _retryQueue = new System.Collections.Concurrent.ConcurrentDictionary<ushort, NonIPFrame>();
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

        /// <summary>
        /// ACKフレームを作成
        /// </summary>
        public NonIPFrame CreateAckFrame(byte[] sourceMac, byte[] destinationMac, ushort sequenceNumber)
        {
            if (sourceMac == null || sourceMac.Length != 6)
                throw new ArgumentException("Source MAC address must be 6 bytes", nameof(sourceMac));
            if (destinationMac == null || destinationMac.Length != 6)
                throw new ArgumentException("Destination MAC address must be 6 bytes", nameof(destinationMac));

            // ACKペイロード（受信確認したシーケンス番号）
            var payload = BitConverter.GetBytes(sequenceNumber);

            var frame = new NonIPFrame
            {
                Header = new FrameHeader
                {
                    SourceMAC = sourceMac,
                    DestinationMAC = destinationMac,
                    Type = FrameType.Ack,
                    SequenceNumber = (ushort)System.Threading.Interlocked.Increment(ref _sequenceNumber),
                    PayloadLength = (ushort)payload.Length,
                    Flags = FrameFlags.None,
                    Timestamp = DateTime.UtcNow
                },
                Payload = payload
            };

            _logger.Debug($"Created ACK frame for sequence {sequenceNumber}");
            return frame;
        }

        /// <summary>
        /// NACKフレームを作成（再送要求）
        /// </summary>
        public NonIPFrame CreateNackFrame(byte[] sourceMac, byte[] destinationMac, ushort sequenceNumber, string reason = "")
        {
            if (sourceMac == null || sourceMac.Length != 6)
                throw new ArgumentException("Source MAC address must be 6 bytes", nameof(sourceMac));
            if (destinationMac == null || destinationMac.Length != 6)
                throw new ArgumentException("Destination MAC address must be 6 bytes", nameof(destinationMac));

            // NACKペイロード（再送要求するシーケンス番号 + 理由）
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);
            writer.Write(sequenceNumber);
            writer.Write(reason ?? string.Empty);
            var payload = ms.ToArray();

            var frame = new NonIPFrame
            {
                Header = new FrameHeader
                {
                    SourceMAC = sourceMac,
                    DestinationMAC = destinationMac,
                    Type = FrameType.Nack,
                    SequenceNumber = (ushort)System.Threading.Interlocked.Increment(ref _sequenceNumber),
                    PayloadLength = (ushort)payload.Length,
                    Flags = FrameFlags.None,
                    Timestamp = DateTime.UtcNow
                },
                Payload = payload
            };

            _logger.Warning($"Created NACK frame for sequence {sequenceNumber}: {reason}");
            return frame;
        }

        /// <summary>
        /// 大きなペイロードをフラグメント化して送信用フレーム作成
        /// </summary>
        public async System.Threading.Tasks.Task<System.Collections.Generic.List<NonIPFrame>> CreateFragmentedFramesAsync(
            byte[] sourceMac, 
            byte[] destinationMac, 
            byte[] data, 
            int maxFragmentSize = 8000,
            FrameFlags additionalFlags = FrameFlags.None)
        {
            if (sourceMac == null || sourceMac.Length != 6)
                throw new ArgumentException("Source MAC address must be 6 bytes", nameof(sourceMac));
            if (destinationMac == null || destinationMac.Length != 6)
                throw new ArgumentException("Destination MAC address must be 6 bytes", nameof(destinationMac));
            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty", nameof(data));

            _logger.Info($"Creating fragmented frames: DataSize={data.Length}, MaxFragmentSize={maxFragmentSize}");

            // FragmentationServiceを使用してフラグメント化
            var fragments = await _fragmentationService.FragmentPayloadAsync(data, maxFragmentSize);

            // MAC アドレスを設定
            foreach (var frame in fragments)
            {
                frame.Header.SourceMAC = sourceMac;
                frame.Header.DestinationMAC = destinationMac;
                frame.Header.Flags |= additionalFlags; // 追加フラグをマージ
            }

            _logger.Info($"Created {fragments.Count} fragmented frames");
            return fragments;
        }

        /// <summary>
        /// フラグメントを追加して再構築を試行
        /// </summary>
        public async System.Threading.Tasks.Task<byte[]?> AddFragmentAndReassembleAsync(NonIPFrame fragmentFrame)
        {
            if (fragmentFrame == null)
                return null;

            try
            {
                _logger.Debug($"Adding fragment: Seq={fragmentFrame.Header.SequenceNumber}, GroupId={fragmentFrame.Header.FragmentInfo?.FragmentGroupId}");
                
                var result = await _fragmentationService.AddFragmentAsync(fragmentFrame);
                
                if (result != null)
                {
                    _logger.Info($"Fragment reassembly complete: GroupId={result.FragmentGroupId}, Size={result.ReassembledPayload.Length} bytes");
                    return result.ReassembledPayload;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error($"Fragment reassembly failed: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// フラグメント受信進捗を取得
        /// </summary>
        public async System.Threading.Tasks.Task<double?> GetFragmentProgressAsync(Guid fragmentGroupId)
        {
            return await _fragmentationService.GetFragmentProgressAsync(fragmentGroupId);
        }

        /// <summary>
        /// ACK待機中のフレームを追加
        /// </summary>
        public void RegisterPendingAck(NonIPFrame frame)
        {
            if (frame == null) return;

            var sequenceNumber = frame.Header.SequenceNumber;
            _pendingAcks[sequenceNumber] = DateTime.UtcNow;
            _retryQueue[sequenceNumber] = frame;

            _logger.Debug($"Registered pending ACK for sequence {sequenceNumber}");
        }

        /// <summary>
        /// ACK受信を処理
        /// </summary>
        public bool ProcessAck(ushort sequenceNumber)
        {
            if (_pendingAcks.TryRemove(sequenceNumber, out _))
            {
                _retryQueue.TryRemove(sequenceNumber, out _);
                _logger.Debug($"Processed ACK for sequence {sequenceNumber}");
                return true;
            }

            _logger.Warning($"Received ACK for unknown sequence {sequenceNumber}");
            return false;
        }

        /// <summary>
        /// タイムアウトしたフレームを取得（再送用）
        /// </summary>
        public System.Collections.Generic.List<NonIPFrame> GetTimedOutFrames()
        {
            var timedOutFrames = new System.Collections.Generic.List<NonIPFrame>();
            var now = DateTime.UtcNow;

            foreach (var kvp in _pendingAcks)
            {
                var sequenceNumber = kvp.Key;
                var sentTime = kvp.Value;

                if ((now - sentTime).TotalMilliseconds > ACK_TIMEOUT_MS)
                {
                    if (_retryQueue.TryGetValue(sequenceNumber, out var frame))
                    {
                        timedOutFrames.Add(frame);
                        _logger.Warning($"Frame sequence {sequenceNumber} timed out, adding to retry queue");
                    }

                    // タイムアウトエントリを削除
                    _pendingAcks.TryRemove(sequenceNumber, out _);
                }
            }

            return timedOutFrames;
        }

        /// <summary>
        /// 再送キューをクリア
        /// </summary>
        public void ClearRetryQueue()
        {
            _pendingAcks.Clear();
            _retryQueue.Clear();
            _logger.Info("Retry queue cleared");
        }

        /// <summary>
        /// 統計情報を取得
        /// </summary>
        public (int PendingAcks, int RetryQueueSize) GetStatistics()
        {
            return (_pendingAcks.Count, _retryQueue.Count);
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
