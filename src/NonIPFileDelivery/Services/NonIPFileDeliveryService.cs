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
    private readonly IFileStorageService _fileStorageService;
    private readonly ISessionManager _sessionManager;
    private readonly IRedundancyService? _redundancyService;
    private readonly IQoSService? _qosService;
    
    private Configuration? _configuration;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _serviceTask;

    public bool IsRunning { get; private set; }

    public NonIPFileDeliveryService(
        ILoggingService logger,
        IConfigurationService configService,
        INetworkService networkService,
        ISecurityService securityService,
        IFrameService frameService,
        IFileStorageService fileStorageService,
        ISessionManager sessionManager,
        IRedundancyService? redundancyService = null,
        IQoSService? qosService = null)
    {
        _logger = logger;
        _configService = configService;
        _networkService = networkService;
        _securityService = securityService;
        _frameService = frameService;
        _fileStorageService = fileStorageService;
        _sessionManager = sessionManager;
        _redundancyService = redundancyService;
        _qosService = qosService;
        
        if (_qosService != null)
        {
            _logger.Info("NonIPFileDeliveryService initialized with QoS support");
        }
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
        var lastRetryCheck = DateTime.UtcNow;
        var lastQoSStats = DateTime.UtcNow;
        var heartbeatInterval = TimeSpan.FromMilliseconds(_configuration?.Redundancy.HeartbeatInterval ?? 1000);
        var retryCheckInterval = TimeSpan.FromSeconds(2); // 2秒ごとに再送チェック
        var qosStatsInterval = TimeSpan.FromSeconds(30); // 30秒ごとにQoS統計

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

                // Check for timed out frames and retry
                if (now - lastRetryCheck >= retryCheckInterval)
                {
                    await CheckAndRetryTimedOutFrames();
                    lastRetryCheck = now;
                }

                // Log QoS statistics if enabled
                if (_qosService != null && _qosService.IsEnabled && now - lastQoSStats >= qosStatsInterval)
                {
                    _qosService.LogStatistics();
                    lastQoSStats = now;
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

                case FrameType.Ack:
                    await ProcessAckFrame(frame, sourceMac);
                    break;

                case FrameType.Nack:
                    await ProcessNackFrame(frame, sourceMac);
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
            
            // セッションのハートビート情報を更新
            var sessionId = frame.Header.SessionId;
            if (sessionId != Guid.Empty)
            {
                _logger.Debug($"Heartbeat for session {sessionId} from {sourceMac}");
                
                // セッション管理にハートビート受信を記録
                var session = await _sessionManager.GetSessionAsync(sessionId);
                if (session != null)
                {
                    await _sessionManager.UpdateSessionActivityAsync(sessionId);
                    _logger.Debug($"Session activity updated: {sessionId}");
                }
                else
                {
                    _logger.Debug($"Heartbeat for unknown session: {sessionId}");
                }
            }
            
            // 冗長性サービスがある場合、ノードステータスを更新
            if (_redundancyService != null)
            {
                try
                {
                    // ハートビート情報をパース
                    var heartbeatInfo = ParseHeartbeatInfo(payloadText);
                    
                    if (heartbeatInfo != null)
                    {
                        // 冗長性サービスにハートビート情報を記録
                        // TODO: IRedundancyServiceにRecordHeartbeatメソッドを追加する必要がある
                        _logger.Debug($"Heartbeat info recorded: NodeId={heartbeatInfo.NodeId}, Status={heartbeatInfo.Status}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to parse heartbeat info from {sourceMac}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing heartbeat from {sourceMac}", ex);
        }
    }

    /// <summary>
    /// ハートビート情報をパース
    /// </summary>
    private HeartbeatInfo? ParseHeartbeatInfo(string payloadText)
    {
        try
        {
            // 形式: "HEARTBEAT:2025-10-19T12:34:56.789Z:NodeId:Status"
            var parts = payloadText.Split(':');
            
            if (parts.Length >= 2 && parts[0] == "HEARTBEAT")
            {
                return new HeartbeatInfo
                {
                    Timestamp = DateTime.Parse(parts[1]),
                    NodeId = parts.Length > 2 ? parts[2] : string.Empty,
                    Status = parts.Length > 3 ? parts[3] : "Active"
                };
            }
        }
        catch
        {
            // パース失敗時はnullを返す
        }

        return null;
    }

    /// <summary>
    /// 簡易ハートビート情報クラス
    /// </summary>
    private class HeartbeatInfo
    {
        public DateTime Timestamp { get; set; }
        public string NodeId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    private async Task ProcessDataFrame(NonIPFrame frame, string sourceMac)
    {
        _logger.Info($"Data frame received from {sourceMac}, size: {frame.Payload.Length} bytes");
        
        try
        {
            // データペイロードを処理
            var sessionId = frame.Header.SessionId;
            
            // セッションを取得または作成
            if (sessionId == Guid.Empty)
            {
                _logger.Warning($"Data frame without session ID from {sourceMac}");
                return;
            }

            var session = await _sessionManager.GetSessionAsync(sessionId);
            if (session == null)
            {
                _logger.Warning($"Unknown session ID: {sessionId} from {sourceMac}");
                return;
            }

            // セッションのアクティビティを更新
            await _sessionManager.UpdateSessionActivityAsync(sessionId);

            _logger.Debug($"Processing data for session {sessionId}");
            
            // フラグメント化されたデータの場合は再構築
            if ((frame.Header.Flags & FrameFlags.FragmentStart) == FrameFlags.FragmentStart ||
                (frame.Header.Flags & FrameFlags.FragmentEnd) == FrameFlags.FragmentEnd ||
                frame.Header.FragmentInfo != null)
            {
                await ProcessFragmentedData(frame, session, sourceMac);
            }
            else
            {
                // 通常のデータフレーム処理
                await ProcessCompleteDataFrame(frame, session, sourceMac);
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
    }

    /// <summary>
    /// フラグメント化されたデータフレームを処理
    /// </summary>
    private Task ProcessFragmentedData(NonIPFrame frame, SessionInfo session, string sourceMac)
    {
        try
        {
            var fragmentInfo = frame.Header.FragmentInfo;
            if (fragmentInfo == null)
            {
                _logger.Warning($"Fragment flags set but no FragmentInfo: SessionId={session.SessionId}");
                return Task.CompletedTask;
            }

            _logger.Debug($"Fragment detected: {fragmentInfo.FragmentIndex + 1}/{fragmentInfo.TotalFragments}, SessionId={session.SessionId}");

            // フラグメントデータをバッファリング（実装簡略化のため、ここではログのみ）
            // 本格的な実装では、IFragmentationServiceを使用してフラグメントを再構築
            _logger.Info($"Fragment {fragmentInfo.FragmentIndex + 1}/{fragmentInfo.TotalFragments} received for session {session.SessionId}");

            // 最終フラグメントの場合
            if ((frame.Header.Flags & FrameFlags.FragmentEnd) == FrameFlags.FragmentEnd)
            {
                _logger.Info($"Final fragment received for session {session.SessionId}, data reconstruction needed");
                // TODO: フラグメント再構築処理
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing fragmented data: SessionId={session.SessionId}", ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 完全なデータフレームを処理（上位プロトコルへ転送）
    /// </summary>
    private Task ProcessCompleteDataFrame(NonIPFrame frame, SessionInfo session, string sourceMac)
    {
        try
        {
            _logger.Debug($"Processing complete data frame: SessionId={session.SessionId}, Size={frame.Payload.Length} bytes");

            // プロトコルタイプに応じて適切な処理を実行
            // 実際の実装では、プロトコルプロキシ（FtpProxy、SftpProxy等）へデータを転送
            _logger.Info($"Data frame ready for protocol handler: SessionId={session.SessionId}, ConnectionId={frame.Header.ConnectionId}");

            // TODO: プロトコルハンドラへのデータ転送実装
            // 例: await _protocolHandlerRegistry.RouteDataAsync(session, frame.Payload);
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing complete data frame: SessionId={session.SessionId}", ex);
        }

        return Task.CompletedTask;
    }

    private async Task ProcessFileTransferFrame(NonIPFrame frame, string sourceMac)
    {
        _logger.Info($"File transfer frame received from {sourceMac}");
        
        try
        {
            var payloadText = System.Text.Encoding.UTF8.GetString(frame.Payload);
            var fileTransferData = System.Text.Json.JsonSerializer.Deserialize<FileTransferFrame>(payloadText);
            
            if (fileTransferData == null)
            {
                _logger.Warning($"Failed to deserialize file transfer data from {sourceMac}");
                return;
            }

            _logger.Info($"File transfer: {fileTransferData.Operation} - {fileTransferData.FileName} " +
                       $"(chunk {fileTransferData.ChunkIndex + 1}/{fileTransferData.TotalChunks})");
            
            var sessionId = fileTransferData.SessionId;
            if (sessionId == Guid.Empty)
            {
                sessionId = frame.Header.SessionId;
            }

            if (sessionId == Guid.Empty)
            {
                _logger.Warning($"File transfer without session ID from {sourceMac}");
                return;
            }

            // セッションを取得または作成
            var session = await _sessionManager.GetSessionAsync(sessionId);
            if (session == null)
            {
                _logger.Warning($"Unknown session ID: {sessionId} for file transfer from {sourceMac}");
                return;
            }

            // セッションのアクティビティを更新
            await _sessionManager.UpdateSessionActivityAsync(sessionId);

            // ファイル操作に応じた処理
            switch (fileTransferData.Operation)
            {
                case FileOperation.Start:
                    _logger.Info($"File transfer started: {fileTransferData.FileName}");
                    break;

                case FileOperation.Data:
                    await ProcessFileUpload(fileTransferData, sessionId, sourceMac);
                    break;

                case FileOperation.End:
                    _logger.Info($"File transfer completed: {fileTransferData.FileName}");
                    break;

                case FileOperation.Abort:
                    _logger.Warning($"File transfer aborted: {fileTransferData.FileName}");
                    await _fileStorageService.CleanupSessionAsync(sessionId);
                    break;

                case FileOperation.Resume:
                    _logger.Info($"File transfer resumed: {fileTransferData.FileName}");
                    break;

                default:
                    _logger.Warning($"Unknown file operation: {fileTransferData.Operation} from {sourceMac}");
                    break;
            }
        }
        catch (System.Text.Json.JsonException jsonEx)
        {
            _logger.Error($"JSON deserialization error for file transfer frame from {sourceMac}", jsonEx);
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing file transfer frame from {sourceMac}", ex);
        }
    }

    /// <summary>
    /// ファイルアップロード処理
    /// </summary>
    private async Task ProcessFileUpload(FileTransferFrame fileData, Guid sessionId, string sourceMac)
    {
        try
        {
            _logger.Debug($"Processing file upload: {fileData.FileName}, Chunk {fileData.ChunkIndex + 1}/{fileData.TotalChunks}");

            if (fileData.ChunkData == null || fileData.ChunkData.Length == 0)
            {
                _logger.Warning($"Empty chunk data for file upload: {fileData.FileName}");
                return;
            }

            // チャンクを保存
            var saved = await _fileStorageService.SaveChunkAsync(
                sessionId, 
                fileData.FileName, 
                (int)fileData.ChunkIndex, 
                (int)fileData.TotalChunks, 
                fileData.ChunkData);

            if (!saved)
            {
                _logger.Error($"Failed to save chunk: {fileData.FileName}, Chunk {fileData.ChunkIndex}");
                return;
            }

            _logger.Debug($"Chunk saved: {fileData.FileName}, Chunk {fileData.ChunkIndex + 1}/{fileData.TotalChunks}, Size={fileData.ChunkData.Length} bytes");

            // 全てのチャンクが揃ったかチェック
            if (await _fileStorageService.AreAllChunksReceivedAsync(sessionId, fileData.FileName, (int)fileData.TotalChunks))
            {
                _logger.Info($"All chunks received for file: {fileData.FileName}, starting assembly");

                // ファイルを組み立て
                var destinationPath = GetDestinationPath(fileData.FileName, sessionId);
                var assembledPath = await _fileStorageService.AssembleFileAsync(
                    sessionId, 
                    fileData.FileName, 
                    (int)fileData.TotalChunks, 
                    destinationPath);

                _logger.Info($"File assembled: {assembledPath}");

                // ファイルハッシュを検証
                if (!string.IsNullOrWhiteSpace(fileData.FileHash))
                {
                    var isValid = await _fileStorageService.ValidateFileHashAsync(assembledPath, fileData.FileHash);
                    
                    if (isValid)
                    {
                        _logger.Info($"File hash validation passed: {fileData.FileName}");
                    }
                    else
                    {
                        _logger.Error($"File hash validation failed: {fileData.FileName}");
                        
                        // ハッシュ検証失敗時はセキュリティ検疫へ
                        await _securityService.QuarantineFile(assembledPath, "File hash mismatch");
                        return;
                    }
                }

                // 最終的なセキュリティスキャン
                var fileBytes = await File.ReadAllBytesAsync(assembledPath);
                var scanResult = await _securityService.ScanData(fileBytes, fileData.FileName);
                
                if (!scanResult.IsClean)
                {
                    _logger.Warning($"Security threat detected in uploaded file: {fileData.FileName}, Threat: {scanResult.ThreatName}");
                    await _securityService.QuarantineFile(assembledPath, $"Threat: {scanResult.ThreatName}");
                }
                else
                {
                    _logger.Info($"File upload completed successfully: {fileData.FileName}, Path: {assembledPath}");
                }

                // セッションの一時ファイルをクリーンアップ
                await _fileStorageService.CleanupSessionAsync(sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing file upload: {fileData.FileName}", ex);
        }
    }

    /// <summary>
    /// ファイルの最終保存先パスを取得
    /// </summary>
    private string GetDestinationPath(string fileName, Guid sessionId)
    {
        // 設定から保存先ディレクトリを取得
        var baseDirectory = _configuration?.Security.QuarantinePath ?? Path.Combine(Path.GetTempPath(), "NonIPFileDelivery", "received");
        
        if (!Directory.Exists(baseDirectory))
        {
            Directory.CreateDirectory(baseDirectory);
        }

        // ファイル名の重複を避けるためにセッションIDを含める
        var safeFileName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var uniqueFileName = $"{safeFileName}_{sessionId:N}{extension}";

        return Path.Combine(baseDirectory, uniqueFileName);
    }

    private async Task ProcessControlFrame(NonIPFrame frame, string sourceMac)
    {
        _logger.Debug($"Control frame received from {sourceMac}");
        
        try
        {
            // コントロールメッセージの処理
            var sessionId = frame.Header.SessionId;
            var connectionId = frame.Header.ConnectionId;
            
            if (sessionId == Guid.Empty)
            {
                _logger.Warning($"Control frame without session ID from {sourceMac}");
                return;
            }

            // セッションを取得
            var session = await _sessionManager.GetSessionAsync(sessionId);
            if (session == null)
            {
                _logger.Warning($"Unknown session ID: {sessionId} for control frame from {sourceMac}");
                return;
            }

            if (frame.Payload.Length > 0)
            {
                var controlMessage = System.Text.Encoding.UTF8.GetString(frame.Payload);
                _logger.Debug($"Control message: {controlMessage}, SessionId={sessionId}");
                
                // コントロールコマンドの解析と実行
                // 例: "PAUSE", "RESUME", "CLOSE", "RESET" など
                switch (controlMessage.ToUpperInvariant())
                {
                    case "PAUSE":
                        _logger.Info($"Pause request from {sourceMac}, Session={sessionId}");
                        // セッションを一時停止状態にマーク
                        // TODO: SessionInfoにState/Status属性を追加する必要がある
                        break;
                        
                    case "RESUME":
                        _logger.Info($"Resume request from {sourceMac}, Session={sessionId}");
                        // セッションを再開状態にマーク
                        break;
                        
                    case "CLOSE":
                        _logger.Info($"Close request from {sourceMac}, Session={sessionId}");
                        // セッションを終了
                        await _sessionManager.CloseSessionAsync(sessionId, "Closed by remote request");
                        // セッションの一時ファイルをクリーンアップ
                        await _fileStorageService.CleanupSessionAsync(sessionId);
                        break;
                        
                    case "RESET":
                        _logger.Info($"Reset request from {sourceMac}, Session={sessionId}");
                        // セッションをリセット（一時データ削除）
                        await _fileStorageService.CleanupSessionAsync(sessionId);
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
    }

    private async Task SendAcknowledgment(ushort sequenceNumber, string destinationMac)
    {
        try
        {
            // MACアドレスをパース
            var destMac = ParseMacAddress(destinationMac);
            if (destMac == null)
            {
                _logger.Error($"Invalid destination MAC address: {destinationMac}");
                return;
            }

            // 自分のMACアドレス（仮想的に生成）
            var sourceMac = new byte[6];
            Random.Shared.NextBytes(sourceMac);
            sourceMac[0] = (byte)(sourceMac[0] & 0xFE | 0x02); // Set local bit

            // ACKフレームを作成して送信
            var ackFrame = _frameService.CreateAckFrame(sourceMac, destMac, sequenceNumber);
            var serializedFrame = _frameService.SerializeFrame(ackFrame);
            
            await _networkService.SendFrame(serializedFrame, destinationMac);
            _logger.Debug($"ACK sent for sequence {sequenceNumber} to {destinationMac}");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to send acknowledgment to {destinationMac}", ex);
        }
    }

    private async Task ProcessAckFrame(NonIPFrame frame, string sourceMac)
    {
        try
        {
            _logger.Debug($"ACK frame received from {sourceMac}, Seq={frame.Header.SequenceNumber}");

            // ACKペイロードから確認対象のシーケンス番号を取得
            if (frame.Payload.Length >= 2)
            {
                var ackedSequenceNumber = BitConverter.ToUInt16(frame.Payload, 0);
                
                // FrameServiceのACK処理を呼び出し
                var processed = _frameService.ProcessAck(ackedSequenceNumber);
                
                if (processed)
                {
                    _logger.Info($"ACK processed successfully for sequence {ackedSequenceNumber} from {sourceMac}");
                }
                else
                {
                    _logger.Warning($"ACK received for unknown sequence {ackedSequenceNumber} from {sourceMac}");
                }
            }
            else
            {
                _logger.Warning($"Invalid ACK frame payload from {sourceMac} (size: {frame.Payload.Length})");
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing ACK frame from {sourceMac}", ex);
        }
    }

    private async Task ProcessNackFrame(NonIPFrame frame, string sourceMac)
    {
        try
        {
            _logger.Warning($"NACK frame received from {sourceMac}, Seq={frame.Header.SequenceNumber}");

            // NACKペイロードから再送要求されたシーケンス番号と理由を取得
            if (frame.Payload.Length >= 2)
            {
                using var ms = new System.IO.MemoryStream(frame.Payload);
                using var reader = new System.IO.BinaryReader(ms);
                
                var nackedSequenceNumber = reader.ReadUInt16();
                var reason = reader.BaseStream.Position < reader.BaseStream.Length 
                    ? reader.ReadString() 
                    : "No reason provided";
                
                _logger.Warning($"NACK for sequence {nackedSequenceNumber} from {sourceMac}: {reason}");
                
                // TODO: 即座に再送を試みる（タイムアウトを待たない）
                // 現在の実装では、GetTimedOutFrames()によるタイムアウト再送のみ
                // 将来的には、NACK受信時に即座に再送するロジックを追加
                
                _logger.Info($"NACK processed: sequence {nackedSequenceNumber} will be retransmitted on timeout");
            }
            else
            {
                _logger.Warning($"Invalid NACK frame payload from {sourceMac} (size: {frame.Payload.Length})");
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing NACK frame from {sourceMac}", ex);
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

    private async Task SendHeartbeat()
    {
        try
        {
            _logger.Debug("Sending heartbeat...");
            
            // Send heartbeat frame to peer systems for redundancy monitoring
            var heartbeatData = System.Text.Encoding.UTF8.GetBytes($"HEARTBEAT:{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}");
            
            // Broadcast to all peers with high priority (heartbeat is critical)
            await _networkService.SendFrame(heartbeatData, "FF:FF:FF:FF:FF:FF", FramePriority.High);
            
            _logger.Debug("Heartbeat sent");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to send heartbeat", ex);
        }
    }

    private async Task CheckAndRetryTimedOutFrames()
    {
        try
        {
            // タイムアウトしたフレームを取得
            var timedOutFrames = _frameService.GetTimedOutFrames();
            
            if (timedOutFrames.Count > 0)
            {
                _logger.Warning($"Found {timedOutFrames.Count} timed out frames, attempting retransmission");
                
                foreach (var frame in timedOutFrames)
                {
                    try
                    {
                        // フレームを再シリアライズして送信
                        var serializedFrame = _frameService.SerializeFrame(frame);
                        var destMac = string.Join(":", frame.Header.DestinationMAC.Select(b => b.ToString("X2")));
                        
                        _logger.Info($"Retransmitting frame: Type={frame.Header.Type}, Seq={frame.Header.SequenceNumber} to {destMac}");
                        
                        await _networkService.SendFrame(serializedFrame, destMac);
                        
                        // 再送したフレームを再度ACK待機キューに登録
                        _frameService.RegisterPendingAck(frame);
                        
                        _logger.Debug($"Frame retransmitted and re-registered: Seq={frame.Header.SequenceNumber}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Failed to retransmit frame Seq={frame.Header.SequenceNumber}", ex);
                    }
                }
                
                // 統計情報をログ出力
                var stats = _frameService.GetStatistics();
                _logger.Debug($"Retry queue statistics: PendingAcks={stats.PendingAcks}, RetryQueueSize={stats.RetryQueueSize}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error checking and retrying timed out frames", ex);
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