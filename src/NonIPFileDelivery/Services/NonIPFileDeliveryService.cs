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
                    _logger.Debug("Processing encrypted frame (decryption handled at lower layer)");
                    // Note: Decryption is typically handled at the SecureEthernetTransceiver layer
                    // The frame received here should already be decrypted if encryption is enabled
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

    private Task ProcessHeartbeatFrame(NonIPFrame frame, string sourceMac)
    {
        _logger.Debug($"Heartbeat received from {sourceMac}");
        
        try
        {
            var payloadText = System.Text.Encoding.UTF8.GetString(frame.Payload);
            _logger.Debug($"Heartbeat data: {payloadText}");
            
            // ピアのステータスを更新
            // RedundancyServiceがある場合、ハートビート情報を記録
            var sessionId = frame.Header.SessionId;
            if (sessionId != Guid.Empty)
            {
                _logger.Debug($"Heartbeat for session {sessionId} from {sourceMac}");
                // セッション管理にハートビート受信を記録
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing heartbeat from {sourceMac}", ex);
        }
        
        return Task.CompletedTask;
    }

    private Task ProcessDataFrame(NonIPFrame frame, string sourceMac)
    {
        _logger.Info($"Data frame received from {sourceMac}, size: {frame.Payload.Length} bytes");
        
        try
        {
            // データペイロードを処理
            var sessionId = frame.Header.SessionId;
            
            // セッションに関連付けられたデータを処理
            if (sessionId != Guid.Empty)
            {
                _logger.Debug($"Processing data for session {sessionId}");
                
                // フラグメント化されたデータの場合は再構築が必要
                if ((frame.Header.Flags & FrameFlags.FragmentStart) == FrameFlags.FragmentStart ||
                    (frame.Header.Flags & FrameFlags.FragmentEnd) == FrameFlags.FragmentEnd)
                {
                    _logger.Debug($"Fragment detected: FragmentInfo={frame.Header.FragmentInfo?.FragmentIndex}/{frame.Header.FragmentInfo?.TotalFragments}");
                }
            }
            
            // ログ用にペイロードのプレビューを表示
            if (frame.Payload.Length > 0)
            {
                var previewLength = Math.Min(100, frame.Payload.Length);
                var payloadPreview = System.Text.Encoding.UTF8.GetString(frame.Payload, 0, previewLength);
                if (frame.Payload.Length > 100) payloadPreview += "...";
                _logger.Debug($"Data payload preview: {payloadPreview}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing data frame from {sourceMac}", ex);
        }
        
        return Task.CompletedTask;
    }    private Task ProcessFileTransferFrame(NonIPFrame frame, string sourceMac)
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
                
                // ファイル転送データの処理
                var sessionId = frame.Header.SessionId;
                
                // チャンクの組み立てとストレージ処理
                // TODO: 実際のファイルストレージ実装が必要な場合は、IFileStorageServiceを追加
                _logger.Debug($"File chunk data size: {fileTransferData.ChunkData?.Length ?? 0} bytes");
                _logger.Debug($"File metadata: Size={fileTransferData.FileSize}, Hash={fileTransferData.FileHash}");
                
                // 最終チャンクの場合
                if (fileTransferData.ChunkIndex == fileTransferData.TotalChunks - 1)
                {
                    _logger.Info($"Final chunk received for file: {fileTransferData.FileName}");
                    // ファイル完成処理
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing file transfer frame from {sourceMac}", ex);
        }
        
        return Task.CompletedTask;
    }

    private Task ProcessControlFrame(NonIPFrame frame, string sourceMac)
    {
        _logger.Debug($"Control frame received from {sourceMac}");
        
        try
        {
            // コントロールメッセージの処理
            var sessionId = frame.Header.SessionId;
            var connectionId = frame.Header.ConnectionId;
            
            if (frame.Payload.Length > 0)
            {
                var controlMessage = System.Text.Encoding.UTF8.GetString(frame.Payload);
                _logger.Debug($"Control message: {controlMessage}");
                
                // コントロールコマンドの解析と実行
                // 例: "PAUSE", "RESUME", "CLOSE", "RESET" など
                switch (controlMessage.ToUpperInvariant())
                {
                    case "PAUSE":
                        _logger.Info($"Pause request from {sourceMac}, Session={sessionId}");
                        break;
                    case "RESUME":
                        _logger.Info($"Resume request from {sourceMac}, Session={sessionId}");
                        break;
                    case "CLOSE":
                        _logger.Info($"Close request from {sourceMac}, Session={sessionId}");
                        break;
                    default:
                        _logger.Debug($"Unknown control command: {controlMessage}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing control frame from {sourceMac}", ex);
        }
        
        return Task.CompletedTask;
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
            
            // Send heartbeat frame to peer systems for redundancy monitoring
            var heartbeatData = System.Text.Encoding.UTF8.GetBytes($"HEARTBEAT:{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}");
            
            // Broadcast to all peers
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
                // TODO: Implement automatic failover to standby node
                // when network interface becomes unavailable
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.Error("Error checking system status", ex);
        }
    }
}