using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;
using NonIpFileDelivery.Core;
using NonIpFileDelivery.Security;
using PacketDotNet;

namespace NonIPFileDelivery.Services;

public class NetworkService : INetworkService, IDisposable
{
    private readonly ILoggingService _logger;
    private readonly IFrameService _frameService;
    private readonly IQoSService? _qosService;
    private readonly ICryptoService? _cryptoService;
    private NetworkConfig? _config;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _listeningTask;
    private byte[] _localMacAddress = new byte[6];
    private IRawEthernetTransceiver? _rawTransceiver;
    private SecureEthernetTransceiver? _secureTransceiver;
    private bool _useRawEthernet;
    private bool _useSecureTransceiver;

    public bool IsInterfaceReady { get; private set; }
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

    public NetworkService(
        ILoggingService logger, 
        IFrameService frameService, 
        IQoSService? qosService = null,
        ICryptoService? cryptoService = null)
    {
        _logger = logger;
        _frameService = frameService;
        _qosService = qosService;
        _cryptoService = cryptoService;
        
        // Generate a temporary MAC address for simulation
        Random.Shared.NextBytes(_localMacAddress);
        _localMacAddress[0] = (byte)(_localMacAddress[0] & 0xFE | 0x02); // Set local bit, clear multicast bit
        
        if (_qosService != null)
        {
            _logger.Info("NetworkService initialized with QoS support");
        }
        
        if (_cryptoService != null)
        {
            _logger.Info("NetworkService initialized with CryptoService for SecureEthernetTransceiver");
        }
    }

    public Task<bool> InitializeInterface(NetworkConfig config)
    {
        _config = config;
        
        try
        {
            _logger.Info($"Initializing network interface: {config.Interface}");
            
            // Check if the interface exists
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            NetworkInterface? targetInterface = null;
            
            foreach (var ni in networkInterfaces)
            {
                if (ni.Name.Equals(config.Interface, StringComparison.OrdinalIgnoreCase) ||
                    ni.Description.Contains(config.Interface, StringComparison.OrdinalIgnoreCase))
                {
                    targetInterface = ni;
                    break;
                }
            }

            if (targetInterface == null)
            {
                _logger.Error($"Network interface not found: {config.Interface}");
                IsInterfaceReady = false;
                return Task.FromResult(false);
            }

            _logger.Info($"Found network interface: {targetInterface.Name} ({targetInterface.Description})");
            _logger.Info($"Interface type: {targetInterface.NetworkInterfaceType}");
            _logger.Info($"Operational status: {targetInterface.OperationalStatus}");
            
            if (targetInterface.OperationalStatus != OperationalStatus.Up)
            {
                _logger.Warning($"Network interface is not operational: {targetInterface.OperationalStatus}");
                // Don't fail completely - some interfaces might work even if not reporting as Up
            }

            // Configure frame size if supported
            _logger.Info($"Configured frame size: {config.FrameSize} bytes");
            
            // Log encryption status
            if (config.Encryption)
            {
                _logger.Info("Encryption is enabled");
            }
            else
            {
                _logger.Warning("Encryption is disabled - data will be transmitted in plain text");
            }

            _logger.Info($"Using EtherType: {config.EtherType}");
            
            // Initialize transceiver based on configuration
            _useRawEthernet = !string.IsNullOrEmpty(config.RemoteMacAddress);
            _useSecureTransceiver = config.UseSecureTransceiver;
            
            if (_useRawEthernet)
            {
                if (_useSecureTransceiver)
                {
                    // Initialize SecureEthernetTransceiver (暗号化対応)
                    try
                    {
                        if (_cryptoService == null)
                        {
                            _logger.Error("CryptoService is required for SecureEthernetTransceiver but not available");
                            throw new InvalidOperationException("CryptoService is required for SecureEthernetTransceiver");
                        }
                        
                        _logger.Info($"Initializing SecureEthernetTransceiver with remote MAC: {config.RemoteMacAddress}");
                        
                        // Create CryptoEngine with secure password
                        // TODO: パスワードは設定ファイルから取得すべき
                        var cryptoEngine = new CryptoEngine("NonIPFileDeliverySecurePassword2025");
                        
                        _secureTransceiver = new SecureEthernetTransceiver(
                            config.Interface,
                            config.RemoteMacAddress!,
                            cryptoEngine,
                            receiverMode: false,
                            channelCapacity: 10000
                        );
                        
                        _logger.Info("SecureEthernetTransceiver initialized successfully - SECURE PRODUCTION MODE");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Failed to initialize SecureEthernetTransceiver: {ex.Message}");
                        _logger.Warning("Falling back to simulation mode");
                        _useRawEthernet = false;
                        _useSecureTransceiver = false;
                        _secureTransceiver = null;
                    }
                }
                else
                {
                    // Initialize RawEthernetTransceiver (軽量版)
                    try
                    {
                        _logger.Info($"Initializing RawEthernetTransceiver with remote MAC: {config.RemoteMacAddress}");
                        _rawTransceiver = new RawEthernetTransceiver(
                            config.Interface,
                            config.RemoteMacAddress!,
                            channelCapacity: 10000
                        );
                        _logger.Info("RawEthernetTransceiver initialized successfully - PRODUCTION MODE");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Failed to initialize RawEthernetTransceiver: {ex.Message}");
                        _logger.Warning("Falling back to simulation mode");
                        _useRawEthernet = false;
                        _rawTransceiver = null;
                    }
                }
            }
            else
            {
                _logger.Warning("RemoteMacAddress not configured - using SIMULATION MODE");
            }
            
            IsInterfaceReady = true;
            _logger.Info("Network interface initialization completed successfully");
            
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to initialize network interface", ex);
            IsInterfaceReady = false;
            return Task.FromResult(false);
        }
    }

    public Task<bool> StartListening()
    {
        if (!IsInterfaceReady || _config == null)
        {
            _logger.Error("Cannot start listening - interface not ready or not configured");
            return Task.FromResult(false);
        }

        try
        {
            _logger.Info("Starting network listening...");
            
            _cancellationTokenSource = new CancellationTokenSource();
            
            if (_useRawEthernet)
            {
                if (_useSecureTransceiver && _secureTransceiver != null)
                {
                    // Secure production mode: Use SecureEthernetTransceiver
                    _logger.Info("Starting SecureEthernetTransceiver (SECURE PRODUCTION MODE)");
                    _secureTransceiver.Start();
                    _listeningTask = ListenForSecureFramesAsync(_cancellationTokenSource.Token);
                }
                else if (_rawTransceiver != null)
                {
                    // Production mode: Use RawEthernetTransceiver
                    _logger.Info("Starting RawEthernetTransceiver (PRODUCTION MODE)");
                    _rawTransceiver.Start();
                    _listeningTask = ListenForRawFramesAsync(_cancellationTokenSource.Token);
                }
                else
                {
                    // Fallback to simulation
                    _logger.Info("Starting frame simulation (SIMULATION MODE - transceiver init failed)");
                    _listeningTask = ListenForFramesAsync(_cancellationTokenSource.Token);
                }
            }
            else
            {
                // Simulation mode
                _logger.Info("Starting frame simulation (SIMULATION MODE)");
                _listeningTask = ListenForFramesAsync(_cancellationTokenSource.Token);
            }
            
            _logger.Info("Network listening started successfully");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to start network listening", ex);
            return Task.FromResult(false);
        }
    }

    public async Task StopListening()
    {
        _logger.Info("Stopping network listening...");
        
        if (_cancellationTokenSource != null)
        {
            _cancellationTokenSource.Cancel();
        }

        if (_listeningTask != null)
        {
            try
            {
                await _listeningTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
        }

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        _listeningTask = null;
        
        _logger.Info("Network listening stopped");
    }
    
    /// <summary>
    /// リソースを解放
    /// </summary>
    public void Dispose()
    {
        _logger.Debug("Disposing NetworkService...");
        
        // Stop listening if still active
        if (_listeningTask != null)
        {
            StopListening().Wait();
        }
        
        // Dispose transceivers
        _rawTransceiver?.Dispose();
        _rawTransceiver = null;
        
        _secureTransceiver?.Dispose();
        _secureTransceiver = null;
        
        _logger.Info("NetworkService disposed");
    }

    public async Task<bool> SendFrame(byte[] data, string destinationMac)
    {
        // デフォルトは通常優先度で送信
        return await SendFrame(data, destinationMac, FramePriority.Normal);
    }

    public async Task<bool> SendFrame(byte[] data, string destinationMac, FramePriority priority)
    {
        if (!IsInterfaceReady || _config == null)
        {
            _logger.Error("Cannot send frame - interface not ready");
            return false;
        }

        try
        {
            // Parse destination MAC address
            var destMac = ParseMacAddress(destinationMac);
            if (destMac == null)
            {
                _logger.Error($"Invalid destination MAC address: {destinationMac}");
                return false;
            }

            // Create and serialize frame
            var frame = _frameService.CreateDataFrame(_localMacAddress, destMac, data);
            
            // 優先度に応じてフラグを設定
            if (priority == FramePriority.High)
            {
                frame.Header.Flags |= FrameFlags.Priority;
            }
            
            if (_config.Encryption)
            {
                frame.Header.Flags |= FrameFlags.Encrypted;
                // Note: Actual encryption is performed by SecureEthernetTransceiver layer
                _logger.Debug("Frame marked for encryption");
            }

            // ✅ ACK/NAK統合: データフレームとファイル転送フレームにはACKを要求
            if (frame.Header.Type == FrameType.Data || 
                frame.Header.Type == FrameType.FileTransfer)
            {
                frame.Header.Flags |= FrameFlags.RequireAck;
                _logger.Debug($"Frame marked for ACK requirement: Type={frame.Header.Type}, Seq={frame.Header.SequenceNumber}");
            }

            var serializedFrame = _frameService.SerializeFrame(frame);
            
            // QoSサービスが有効な場合はキュー経由で送信
            if (_qosService != null && _qosService.IsEnabled)
            {
                _logger.Debug($"Enqueueing frame via QoS: destination={destinationMac}, size={serializedFrame.Length} bytes, priority={priority}");
                
                // 優先度に応じてフラグを設定
                FrameFlags? qosPriority = priority switch
                {
                    FramePriority.High => FrameFlags.Priority,
                    FramePriority.Low => null, // 低優先度は特にフラグなし
                    _ => null
                };
                
                await _qosService.EnqueueFrameAsync(frame, qosPriority);
                
                // QoS経由の場合、実際の送信はバックグラウンドタスクが行う
                // ここでは帯域制限チェックのみ実施
                var canSend = await _qosService.TryConsumeAsync(serializedFrame.Length);
                if (!canSend)
                {
                    _logger.Warning($"Bandwidth limit reached, frame queued: {destinationMac}");
                }
                
                // ✅ ACK/NAK統合: ACK要求フレームを待機キューに登録
                if (frame.Header.Flags.HasFlag(FrameFlags.RequireAck))
                {
                    _frameService.RegisterPendingAck(frame);
                    _logger.Debug($"Frame registered for ACK (QoS path): Seq={frame.Header.SequenceNumber}");
                }
                
                return true;
            }
            else
            {
                // QoS無効時は直接送信
                _logger.Debug($"Sending frame to {destinationMac}, size: {serializedFrame.Length} bytes, type: {frame.Header.Type}, priority: {priority}");
                
                if (_useRawEthernet)
                {
                    if (_useSecureTransceiver && _secureTransceiver != null)
                    {
                        // Secure production mode: Send via SecureEthernetTransceiver
                        // Convert NonIPFrame to SecureFrame
                        var secureFrame = new NonIpFileDelivery.Core.SecureFrame
                        {
                            SessionId = Guid.NewGuid(), // TODO: セッション管理と統合
                            Protocol = MapFrameTypeToProtocol(frame.Header.Type),
                            Payload = serializedFrame
                        };
                        
                        await _secureTransceiver.SendFrameAsync(secureFrame, CancellationToken.None);
                        _logger.Debug($"Frame sent via Secure Ethernet to {destinationMac} (seq: {frame.Header.SequenceNumber})");
                    }
                    else if (_rawTransceiver != null)
                    {
                        // Production mode: Send via RawEthernetTransceiver
                        await _rawTransceiver.SendAsync(serializedFrame, CancellationToken.None);
                        _logger.Debug($"Frame sent via Raw Ethernet to {destinationMac} (seq: {frame.Header.SequenceNumber})");
                    }
                    else
                    {
                        // Fallback to simulation
                        var transmissionTime = CalculateTransmissionTime(serializedFrame.Length);
                        await Task.Delay(transmissionTime);
                        _logger.Debug($"Frame sent (simulated - transceiver init failed) to {destinationMac} (seq: {frame.Header.SequenceNumber})");
                    }
                }
                else
                {
                    // Simulation mode: Use Task.Delay
                    var transmissionTime = CalculateTransmissionTime(serializedFrame.Length);
                    await Task.Delay(transmissionTime);
                    _logger.Debug($"Frame sent (simulated) to {destinationMac} (seq: {frame.Header.SequenceNumber})");
                }
                
                // ✅ ACK/NAK統合: ACK要求フレームを待機キューに登録
                if (frame.Header.Flags.HasFlag(FrameFlags.RequireAck))
                {
                    _frameService.RegisterPendingAck(frame);
                    _logger.Debug($"Frame registered for ACK (direct path): Seq={frame.Header.SequenceNumber}");
                }
                
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to send frame to {destinationMac}", ex);
            return false;
        }
    }

    private byte[]? ParseMacAddress(string macAddress)
    {
        try
        {
            if (macAddress == "FF:FF:FF:FF:FF:FF")
            {
                return new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
            }

            var parts = macAddress.Split(':');
            if (parts.Length != 6)
                return null;

            var mac = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                mac[i] = Convert.ToByte(parts[i], 16);
            }
            return mac;
        }
        catch
        {
            return null;
        }
    }

    private int CalculateTransmissionTime(int frameSize)
    {
        // Calculate transmission time based on frame size and network speed
        // Assume 1 Gbps baseline speed with some overhead
        var bitsToTransmit = frameSize * 8;
        var transmissionTimeMs = (double)bitsToTransmit / (1_000_000_000.0 / 1000.0); // 1 Gbps
        
        // Add some random network latency (1-10ms)
        var networkLatency = Random.Shared.Next(1, 11);
        
        return Math.Max(1, (int)Math.Ceiling(transmissionTimeMs) + networkLatency);
    }

    /// <summary>
    /// NonIPFrame.FrameTypeをSecureFrame.ProtocolTypeにマッピング
    /// </summary>
    private NonIpFileDelivery.Core.SecureFrame.ProtocolType MapFrameTypeToProtocol(FrameType frameType)
    {
        return frameType switch
        {
            FrameType.Data => NonIpFileDelivery.Core.SecureFrame.ProtocolType.FtpData,
            FrameType.Control => NonIpFileDelivery.Core.SecureFrame.ProtocolType.PostgreSql,
            FrameType.Ack => NonIpFileDelivery.Core.SecureFrame.ProtocolType.ControlMessage,
            FrameType.Nack => NonIpFileDelivery.Core.SecureFrame.ProtocolType.ControlMessage,
            FrameType.FileTransfer => NonIpFileDelivery.Core.SecureFrame.ProtocolType.SftpData,
            FrameType.Heartbeat => NonIpFileDelivery.Core.SecureFrame.ProtocolType.Heartbeat,
            _ => NonIpFileDelivery.Core.SecureFrame.ProtocolType.FtpControl
        };
    }

    /// <summary>
    /// Raw Ethernetフレーム受信ループ (PRODUCTION MODE)
    /// </summary>
    private async Task ListenForRawFramesAsync(CancellationToken cancellationToken)
    {
        if (_rawTransceiver == null)
        {
            _logger.Error("RawEthernetTransceiver is not initialized");
            return;
        }

        _logger.Info("Raw Ethernet frame listening loop started (PRODUCTION MODE)");
        
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Receive raw Ethernet packet
                    var ethPacket = await _rawTransceiver.ReceiveAsync(cancellationToken);
                    
                    // Extract payload (NonIPFrame serialized data)
                    var payloadData = ethPacket.PayloadData;
                    
                    // Deserialize to NonIPFrame
                    var frame = _frameService.DeserializeFrame(payloadData);
                    if (frame != null)
                    {
                        var sourceMacString = MacAddressToString(frame.Header.SourceMAC);
                        _logger.Debug($"Received frame via Raw Ethernet: Type={frame.Header.Type}, From={sourceMacString}, Seq={frame.Header.SequenceNumber}");
                        
                        // Fire FrameReceived event
                        FrameReceived?.Invoke(this, new FrameReceivedEventArgs(payloadData, sourceMacString));
                    }
                    else
                    {
                        _logger.Warning("Failed to deserialize received frame");
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error processing raw Ethernet frame", ex);
                    // Continue listening despite errors
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Raw Ethernet frame listening cancelled");
        }
        catch (Exception ex)
        {
            _logger.Error("Fatal error in raw Ethernet listening loop", ex);
        }
        
        _logger.Info("Raw Ethernet frame listening loop ended");
    }

    /// <summary>
    /// Secure Ethernetフレーム受信ループ (SECURE PRODUCTION MODE)
    /// </summary>
    private async Task ListenForSecureFramesAsync(CancellationToken cancellationToken)
    {
        if (_secureTransceiver == null)
        {
            _logger.Error("SecureEthernetTransceiver is not initialized");
            return;
        }

        _logger.Info("Secure Ethernet frame listening loop started (SECURE PRODUCTION MODE)");
        
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Receive secure frame (already decrypted by SecureEthernetTransceiver)
                    var secureFrame = await _secureTransceiver.ReceiveFrameAsync(cancellationToken);
                    
                    // Convert SecureFrame to NonIPFrame
                    // SecureFrame has: SessionId, Protocol, Payload, SequenceNumber, Timestamp
                    // We need to map this to NonIPFrame format
                    
                    // For now, we'll pass the payload directly to FrameReceived event
                    // TODO: Implement proper SecureFrame → NonIPFrame conversion
                    
                    var sourceMacString = "secure-node"; // SecureFrame doesn't have MAC address
                    _logger.Debug($"Received secure frame: Protocol={secureFrame.Protocol}, Session={secureFrame.SessionId}, Seq={secureFrame.SequenceNumber}");
                    
                    // Fire FrameReceived event with payload
                    FrameReceived?.Invoke(this, new FrameReceivedEventArgs(secureFrame.Payload, sourceMacString));
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error processing secure frame", ex);
                    // Continue listening despite errors
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Secure frame listening cancelled");
        }
        catch (Exception ex)
        {
            _logger.Error("Fatal error in secure listening loop", ex);
        }
        
        _logger.Info("Secure Ethernet frame listening loop ended");
    }

    /// <summary>
    /// シミュレーションモードのフレーム受信ループ
    /// </summary>
    private async Task ListenForFramesAsync(CancellationToken cancellationToken)
    {
        _logger.Debug("Frame listening loop started");
        
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Simulate frame reception for testing and development
                // Production: Replace with raw socket/libpcap integration via RawEthernetTransceiver
                await Task.Delay(3000, cancellationToken); // Check every 3 seconds
                
                // Simulate receiving different types of frames
                if (Random.Shared.Next(1, 4) == 1) // 33% chance
                {
                    await SimulateFrameReception(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Frame listening cancelled");
        }
        catch (Exception ex)
        {
            _logger.Error("Error in frame listening loop", ex);
        }
        
        _logger.Debug("Frame listening loop ended");
    }

    private Task SimulateFrameReception(CancellationToken cancellationToken)
    {
        try
        {
            // フレーム受信のシミュレーション
            // 本番環境では実際のネットワークインターフェースから受信
            var frameType = Random.Shared.Next(1, 4);
            NonIPFrame? receivedFrame = null;
            
            switch (frameType)
            {
                case 1: // Heartbeat frame
                    var remoteMac = GenerateRandomMacAddress();
                    receivedFrame = _frameService.CreateHeartbeatFrame(remoteMac);
                    break;
                    
                case 2: // Data frame
                    var senderMac = GenerateRandomMacAddress();
                    var testData = System.Text.Encoding.UTF8.GetBytes($"Test data from {MacAddressToString(senderMac)} at {DateTime.Now}");
                    receivedFrame = _frameService.CreateDataFrame(senderMac, _localMacAddress, testData);
                    break;
                    
                case 3: // File transfer frame
                    var fileSenderMac = GenerateRandomMacAddress();
                    var fileTransferData = new FileTransferFrame
                    {
                        Operation = FileOperation.Data,
                        FileName = "test-file.txt",
                        FileSize = 1024,
                        ChunkIndex = 1,
                        TotalChunks = 4,
                        ChunkData = new byte[256],
                        FileHash = "abc123def456"
                    };
                    Random.Shared.NextBytes(fileTransferData.ChunkData);
                    receivedFrame = _frameService.CreateFileTransferFrame(fileSenderMac, _localMacAddress, fileTransferData);
                    break;
            }

            if (receivedFrame != null)
            {
                // Serialize and deserialize to simulate real network reception
                var serializedFrame = _frameService.SerializeFrame(receivedFrame);
                var deserializedFrame = _frameService.DeserializeFrame(serializedFrame);
                
                if (deserializedFrame != null)
                {
                    var sourceMacString = MacAddressToString(deserializedFrame.Header.SourceMAC);
                    _logger.Debug($"Received frame: Type={deserializedFrame.Header.Type}, From={sourceMacString}, Seq={deserializedFrame.Header.SequenceNumber}");
                    
                    FrameReceived?.Invoke(this, new FrameReceivedEventArgs(serializedFrame, sourceMacString));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error simulating frame reception", ex);
        }
        
        return Task.CompletedTask;
    }

    private byte[] GenerateRandomMacAddress()
    {
        var mac = new byte[6];
        Random.Shared.NextBytes(mac);
        mac[0] = (byte)(mac[0] & 0xFE | 0x02); // Set local bit, clear multicast bit
        return mac;
    }

    private string MacAddressToString(byte[] mac)
    {
        return string.Join(":", mac.Select(b => b.ToString("X2")));
    }
}