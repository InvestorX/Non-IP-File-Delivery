using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

public class NonIPFileDeliveryService
{
    private readonly ILoggingService _logger;
    private readonly IConfigurationService _configService;
    private readonly INetworkService _networkService;
    private readonly ISecurityService _securityService;
    private readonly IFrameService _frameService;
    
    private Configuration? _configuration;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _serviceTask;

    public bool IsRunning { get; private set; }

    public NonIPFileDeliveryService(
        ILoggingService logger,
        IConfigurationService configService,
        INetworkService networkService,
        ISecurityService securityService,
        IFrameService frameService)
    {
        _logger = logger;
        _configService = configService;
        _networkService = networkService;
        _securityService = securityService;
        _frameService = frameService;
    }

    public async Task<bool> StartAsync(Configuration configuration)
    {
        try
        {
            _logger.Info("Starting Non-IP File Delivery Service...");
            _configuration = configuration;

            // Validate configuration
            if (!_configService.ValidateConfiguration(configuration))
            {
                _logger.Error("Configuration validation failed");
                return false;
            }

            _logger.Info("Configuration validated successfully");

            // Initialize security module
            _logger.Info("Initializing security module...");
            if (!await _securityService.InitializeSecurity(configuration.Security))
            {
                _logger.Error("Security module initialization failed");
                return false;
            }

            // Initialize network interface
            _logger.Info("Initializing network interface...");
            if (!await _networkService.InitializeInterface(configuration.Network))
            {
                _logger.Error("Network interface initialization failed");
                return false;
            }

            // Start network listening
            _logger.Info("Starting network listening...");
            if (!await _networkService.StartListening())
            {
                _logger.Error("Failed to start network listening");
                return false;
            }

            // Subscribe to frame received events
            _networkService.FrameReceived += OnFrameReceived;

            // Start main service loop
            _cancellationTokenSource = new CancellationTokenSource();
            _serviceTask = RunServiceLoopAsync(_cancellationTokenSource.Token);

            IsRunning = true;
            _logger.Info("Non-IP File Delivery Service started successfully");
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to start service", ex);
            return false;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            _logger.Info("Stopping Non-IP File Delivery Service...");
            
            IsRunning = false;

            // Stop main service loop
            _cancellationTokenSource?.Cancel();
            
            if (_serviceTask != null)
            {
                await _serviceTask;
            }

            // Stop network listening
            await _networkService.StopListening();
            
            // Unsubscribe from events
            _networkService.FrameReceived -= OnFrameReceived;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _serviceTask = null;

            _logger.Info("Non-IP File Delivery Service stopped");
        }
        catch (Exception ex)
        {
            _logger.Error("Error stopping service", ex);
        }
    }

    private async Task RunServiceLoopAsync(CancellationToken cancellationToken)
    {
        _logger.Debug("Service main loop started");
        
        var lastHeartbeat = DateTime.UtcNow;
        var heartbeatInterval = TimeSpan.FromMilliseconds(_configuration?.Redundancy.HeartbeatInterval ?? 1000);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;

                // Send heartbeat if needed
                if (now - lastHeartbeat >= heartbeatInterval)
                {
                    await SendHeartbeat();
                    lastHeartbeat = now;
                }

                // Check system status
                await CheckSystemStatus();

                // Wait before next iteration
                await Task.Delay(1000, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Service loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.Error("Error in service main loop", ex);
        }

        _logger.Debug("Service main loop ended");
    }

    private async void OnFrameReceived(object? sender, FrameReceivedEventArgs e)
    {
        try
        {
            _logger.Debug($"Frame received from {e.SourceMac}, size: {e.Data.Length} bytes");

            // Process the received frame
            await ProcessReceivedFrame(e.Data, e.SourceMac);
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing received frame from {e.SourceMac}", ex);
        }
    }

    private async Task ProcessReceivedFrame(byte[] frameData, string sourceMac)
    {
        try
        {
            // Deserialize the frame using the frame service
            var frame = _frameService.DeserializeFrame(frameData);
            if (frame == null)
            {
                _logger.Warning($"Failed to deserialize frame from {sourceMac}");
                return;
            }

            _logger.Debug($"Processing frame: Type={frame.Header.Type}, Seq={frame.Header.SequenceNumber}, Flags={frame.Header.Flags}");

            // Handle encrypted frames
            if (frame.Header.Flags.HasFlag(FrameFlags.Encrypted))
            {
                if (_configuration?.Network.Encryption == true)
                {
                    _logger.Debug("Decrypting received frame...");
                    // In a real implementation, this would decrypt the payload
                    // For now, we just log that decryption would happen
                }
                else
                {
                    _logger.Warning("Received encrypted frame but encryption is disabled");
                    return;
                }
            }

            // Scan for security threats if it's a data or file transfer frame
            if (frame.Header.Type == FrameType.Data || frame.Header.Type == FrameType.FileTransfer)
            {
                var scanResult = await _securityService.ScanData(frame.Payload, $"frame_{frame.Header.Type}_{frame.Header.SequenceNumber}");
                if (!scanResult.IsClean)
                {
                    _logger.Warning($"Security threat detected in frame from {sourceMac}: {scanResult.ThreatName}");
                    await _securityService.QuarantineFile($"frame_{DateTime.Now:yyyyMMdd_HHmmss}_{frame.Header.SequenceNumber}.bin", 
                        $"Threat: {scanResult.ThreatName}");
                    return;
                }
            }

            // Process frame based on type
            switch (frame.Header.Type)
            {
                case FrameType.Heartbeat:
                    await ProcessHeartbeatFrame(frame, sourceMac);
                    break;

                case FrameType.Data:
                    await ProcessDataFrame(frame, sourceMac);
                    break;

                case FrameType.FileTransfer:
                    await ProcessFileTransferFrame(frame, sourceMac);
                    break;

                case FrameType.Control:
                    await ProcessControlFrame(frame, sourceMac);
                    break;

                default:
                    _logger.Warning($"Unknown frame type received: {frame.Header.Type} from {sourceMac}");
                    break;
            }

            // Send acknowledgment if required
            if (frame.Header.Flags.HasFlag(FrameFlags.RequireAck))
            {
                await SendAcknowledgment(frame.Header.SequenceNumber, sourceMac);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing frame from {sourceMac}", ex);
        }
    }

    private async Task ProcessHeartbeatFrame(NonIPFrame frame, string sourceMac)
    {
        _logger.Debug($"Heartbeat received from {sourceMac}");
        
        try
        {
            var payloadText = System.Text.Encoding.UTF8.GetString(frame.Payload);
            _logger.Debug($"Heartbeat data: {payloadText}");
            
            // In a real implementation, this would update peer status and handle failover
            // For now, we just log the heartbeat
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing heartbeat from {sourceMac}", ex);
        }
    }

    private async Task ProcessDataFrame(NonIPFrame frame, string sourceMac)
    {
        _logger.Info($"Data frame received from {sourceMac}, size: {frame.Payload.Length} bytes");
        
        try
        {
            // In a real implementation, this would process application data
            // For now, we just log the reception
            var dataPreview = frame.Payload.Length > 50 ? 
                System.Text.Encoding.UTF8.GetString(frame.Payload.Take(50).ToArray()) + "..." :
                System.Text.Encoding.UTF8.GetString(frame.Payload);
                
            _logger.Debug($"Data preview: {dataPreview}");
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing data frame from {sourceMac}", ex);
        }
    }

    private async Task ProcessFileTransferFrame(NonIPFrame frame, string sourceMac)
    {
        _logger.Info($"File transfer frame received from {sourceMac}");
        
        try
        {
            var payloadText = System.Text.Encoding.UTF8.GetString(frame.Payload);
            var fileTransferData = System.Text.Json.JsonSerializer.Deserialize<FileTransferFrame>(payloadText);
            
            if (fileTransferData != null)
            {
                _logger.Info($"File transfer: {fileTransferData.Operation} - {fileTransferData.FileName} " +
                           $"(chunk {fileTransferData.ChunkIndex}/{fileTransferData.TotalChunks})");
                
                // In a real implementation, this would handle file assembly and storage
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing file transfer frame from {sourceMac}", ex);
        }
    }

    private async Task ProcessControlFrame(NonIPFrame frame, string sourceMac)
    {
        _logger.Debug($"Control frame received from {sourceMac}");
        
        try
        {
            // In a real implementation, this would handle control messages
            // like connection management, flow control, etc.
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing control frame from {sourceMac}", ex);
        }
    }

    private async Task SendAcknowledgment(ushort sequenceNumber, string destinationMac)
    {
        try
        {
            var ackData = System.Text.Encoding.UTF8.GetBytes($"ACK:{sequenceNumber}");
            await _networkService.SendFrame(ackData, destinationMac);
            _logger.Debug($"Acknowledgment sent for sequence {sequenceNumber} to {destinationMac}");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to send acknowledgment to {destinationMac}", ex);
        }
    }

    private async Task SendHeartbeat()
    {
        try
        {
            _logger.Debug("Sending heartbeat...");
            
            // In a real implementation, this would send a heartbeat frame
            // to peer systems for redundancy monitoring
            var heartbeatData = System.Text.Encoding.UTF8.GetBytes($"HEARTBEAT:{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}");
            
            // Send to broadcast or known peers
            await _networkService.SendFrame(heartbeatData, "FF:FF:FF:FF:FF:FF");
            
            _logger.Debug("Heartbeat sent");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to send heartbeat", ex);
        }
    }

    private async Task CheckSystemStatus()
    {
        try
        {
            // Check memory usage
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var memoryUsageMB = process.WorkingSet64 / (1024 * 1024);
            var maxMemoryMB = _configuration?.Performance.MaxMemoryMB ?? 8192;

            if (memoryUsageMB > maxMemoryMB * 0.9) // 90% threshold
            {
                _logger.Warning($"High memory usage: {memoryUsageMB} MB (limit: {maxMemoryMB} MB)");
            }

            // Check network interface status
            if (!_networkService.IsInterfaceReady)
            {
                _logger.Error("Network interface is not ready");
                // In a real implementation, this might trigger failover
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.Error("Error checking system status", ex);
        }
    }
}