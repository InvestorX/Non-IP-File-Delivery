using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

public class NetworkService : INetworkService
{
    private readonly ILoggingService _logger;
    private readonly IFrameService _frameService;
    private readonly IQoSService? _qosService;
    private NetworkConfig? _config;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _listeningTask;
    private byte[] _localMacAddress = new byte[6];

    public bool IsInterfaceReady { get; private set; }
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

    public NetworkService(ILoggingService logger, IFrameService frameService, IQoSService? qosService = null)
    {
        _logger = logger;
        _frameService = frameService;
        _qosService = qosService;
        
        // Generate a temporary MAC address for simulation
        Random.Shared.NextBytes(_localMacAddress);
        _localMacAddress[0] = (byte)(_localMacAddress[0] & 0xFE | 0x02); // Set local bit, clear multicast bit
        
        if (_qosService != null)
        {
            _logger.Info("NetworkService initialized with QoS support");
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
            _listeningTask = ListenForFramesAsync(_cancellationTokenSource.Token);
            
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
                
                return true;
            }
            else
            {
                // QoS無効時は直接送信
                _logger.Debug($"Sending frame to {destinationMac}, size: {serializedFrame.Length} bytes, type: {frame.Header.Type}, priority: {priority}");
                
                // Simulate raw socket transmission with realistic network timing
                // In production, this would interface with RawEthernetTransceiver or libpcap
                var transmissionTime = CalculateTransmissionTime(serializedFrame.Length);
                await Task.Delay(transmissionTime);
                
                _logger.Debug($"Frame sent successfully to {destinationMac} (seq: {frame.Header.SequenceNumber})");
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