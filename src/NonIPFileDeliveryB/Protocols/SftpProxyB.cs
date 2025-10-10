using NonIpFileDelivery.Core;
using NonIpFileDelivery.Security;
using NonIpFileDelivery.Models;
using Serilog;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NonIpFileDeliveryB.Protocols;

/// <summary>
/// SFTPプロキシB側（受信側）
/// Raw Ethernetから受信 → SFTPサーバへTCP転送
/// </summary>
public class SftpProxyB : IDisposable
{
    private readonly SecureEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly string _targetSftpHost;
    private readonly int _targetSftpPort;
    private readonly SessionManagerB _sessionManager;
    private readonly CancellationTokenSource _cts;
    private bool _isRunning;

    private const byte PROTOCOL_SFTP_SSH_HANDSHAKE = 0x20;
    private const byte PROTOCOL_SFTP_CHANNEL = 0x21;
    private const byte PROTOCOL_SFTP_DATA = 0x22;

    public SftpProxyB(
        SecureEthernetTransceiver transceiver,
        SecurityInspector inspector,
        string targetSftpHost = "192.168.2.101",
        int targetSftpPort = 22)
    {
        _transceiver = transceiver ?? throw new ArgumentNullException(nameof(transceiver));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _targetSftpHost = targetSftpHost;
        _targetSftpPort = targetSftpPort;
        _sessionManager = new SessionManagerB(sessionTimeoutMinutes: 10);
        _cts = new CancellationTokenSource();

        _transceiver.FrameReceived += OnFrameReceived;

        Log.Information("SftpProxyB initialized: Target={Host}:{Port}", targetSftpHost, targetSftpPort);
    }

    public async Task StartAsync()
    {
        if (_isRunning) return;

        _isRunning = true;
        Log.Information("SftpProxyB started");

        await Task.CompletedTask;
    }

    private void OnFrameReceived(object? sender, SecureFrame frame)
    {
        if (frame.Protocol != SecureFrame.ProtocolType.SftpControl &&
            frame.Protocol != SecureFrame.ProtocolType.SftpData)
        {
            return;
        }

        // Fire-and-Forgetパターン: 例外を適切にログ記録
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleSftpFrameAsync(frame);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling SFTP frame: Protocol={Protocol}, SessionId={SessionId}", 
                    frame.Protocol, frame.SessionId);
            }
        });
    }

    private async Task HandleSftpFrameAsync(SecureFrame frame)
    {
        try
        {
            var payload = frame.Payload;
            if (payload.Length < 10) return;

            var protocolType = payload[0];
            var sessionIdBytes = payload[1..9];
            var sessionId = Encoding.ASCII.GetString(sessionIdBytes);
            var data = payload[9..];

            Log.Debug("Handling SFTP frame: Protocol={Protocol}, SessionId={SessionId}, DataLen={DataLen}",
                protocolType, sessionId, data.Length);

            // セキュリティ検閲
            if (_inspector.ScanData(data, $"SFTP-{sessionId}"))
            {
                Log.Warning("Blocked malicious SFTP data: SessionId={SessionId}, Size={Size}",
                    sessionId, data.Length);
                return;
            }

            // セッションに対応するTCP接続を取得（なければ作成）
            var client = _sessionManager.GetClientBySession(sessionId);
            if (client == null)
            {
                client = new TcpClient();
                await client.ConnectAsync(_targetSftpHost, _targetSftpPort, _cts.Token);
                _sessionManager.RegisterSession(sessionId, client);

                Log.Information("New SFTP connection established: SessionId={SessionId}, Server={Host}:{Port}",
                    sessionId, _targetSftpHost, _targetSftpPort);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await MonitorSftpServerResponseAsync(sessionId, client);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error monitoring SFTP server response: SessionId={SessionId}", sessionId);
                    }
                });
            }

            // SFTPサーバにデータを転送
            var stream = client.GetStream();
            await stream.WriteAsync(data, _cts.Token);
            await stream.FlushAsync(_cts.Token);

            Log.Debug("SFTP data forwarded to server: SessionId={SessionId}, DataLen={DataLen}",
                sessionId, data.Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling SFTP frame");
        }
    }

    private async Task MonitorSftpServerResponseAsync(string sessionId, TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];

            while (!_cts.Token.IsCancellationRequested && client.Connected)
            {
                var bytesRead = await stream.ReadAsync(buffer, _cts.Token);
                if (bytesRead == 0) break;

                var responseData = buffer[..bytesRead];

                Log.Debug("SFTP response from server: SessionId={SessionId}, DataLen={DataLen}",
                    sessionId, bytesRead);

                // ダウンロード方向の検閲
                if (_inspector.ScanData(responseData, $"SFTP-DOWNLOAD-{sessionId}"))
                {
                    Log.Warning("Blocked malicious SFTP data download: SessionId={SessionId}, Size={Size}",
                        sessionId, bytesRead);
                    _sessionManager.RemoveSession(sessionId);
                    return;
                }

                var payload = BuildProtocolPayload(PROTOCOL_SFTP_DATA, sessionId, responseData);

                var frame = new SecureFrame
                {
                    SessionId = Guid.Parse(sessionId.PadRight(36, '0')),
                    Protocol = SecureFrame.ProtocolType.SftpData,
                    Payload = payload
                };

                await _transceiver.SendFrameAsync(frame, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error monitoring SFTP server response: SessionId={SessionId}", sessionId);
        }
        finally
        {
            _sessionManager.RemoveSession(sessionId);
        }
    }

    private byte[] BuildProtocolPayload(byte protocolType, string sessionId, byte[] data)
    {
        var payload = new byte[1 + 8 + data.Length];
        payload[0] = protocolType;
        
        var sessionIdBytes = Encoding.ASCII.GetBytes(sessionId.PadRight(8));
        Array.Copy(sessionIdBytes, 0, payload, 1, 8);
        
        Array.Copy(data, 0, payload, 9, data.Length);
        
        return payload;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _sessionManager?.Dispose();
        _isRunning = false;

        Log.Information("SftpProxyB stopped");
    }
}
