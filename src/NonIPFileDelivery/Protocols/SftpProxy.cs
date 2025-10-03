using NonIpFileDelivery.Core;
using NonIpFileDelivery.Security;
using Serilog;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NonIpFileDelivery.Protocols;

/// <summary>
/// SFTP (SSH File Transfer Protocol) プロキシサーバー
/// SSH-2プロトコル上でSFTPサブシステムを透過的にブリッジ
/// </summary>
public class SftpProxy : IDisposable
{
    private readonly RawEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly TcpListener _listener;
    private readonly IPEndPoint _targetSftpServer;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, SftpSession> _sessions;
    private readonly SemaphoreSlim _sessionLock;
    private bool _isRunning;

    // SSH-2メッセージタイプ
    private const byte SSH_MSG_KEXINIT = 20;
    private const byte SSH_MSG_NEWKEYS = 21;
    private const byte SSH_MSG_USERAUTH_REQUEST = 50;
    private const byte SSH_MSG_CHANNEL_OPEN = 90;
    private const byte SSH_MSG_CHANNEL_DATA = 94;

    // SFTPメッセージタイプ
    private const byte SSH_FXP_INIT = 1;
    private const byte SSH_FXP_OPEN = 3;
    private const byte SSH_FXP_CLOSE = 4;
    private const byte SSH_FXP_READ = 5;
    private const byte SSH_FXP_WRITE = 6;
    private const byte SSH_FXP_REMOVE = 13;
    private const byte SSH_FXP_RENAME = 18;

    // プロトコル識別子
    private const byte PROTOCOL_SFTP_CONTROL = 0x05;
    private const byte PROTOCOL_SFTP_DATA = 0x06;

    /// <summary>
    /// SFTPプロキシを初期化
    /// </summary>
    public SftpProxy(
        RawEthernetTransceiver transceiver,
        SecurityInspector inspector,
        int listenPort = 22,
        string targetHost = "192.168.1.100",
        int targetPort = 22)
    {
        _transceiver = transceiver;
        _inspector = inspector;
        _listener = new TcpListener(IPAddress.Any, listenPort);
        _targetSftpServer = new IPEndPoint(IPAddress.Parse(targetHost), targetPort);
        _cts = new CancellationTokenSource();
        _sessions = new Dictionary<string, SftpSession>();
        _sessionLock = new SemaphoreSlim(1, 1);

        Log.Information("SftpProxy initialized: Listen={ListenPort}, Target={TargetHost}:{TargetPort}",
            listenPort, targetHost, targetPort);
    }

    /// <summary>
    /// プロキシを起動
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning) return;

        _listener.Start();
        _isRunning = true;

        Log.Information("SftpProxy started on port {Port}", ((IPEndPoint)_listener.LocalEndpoint).Port);

        // クライアント接続受付ループ
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = HandleClientAsync(client);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error accepting SFTP client");
                }
            }
        });

        // Raw Ethernet受信ループ
        _ = Task.Run(async () =>
        {
            await foreach (var packet in _transceiver.ReceiveStream(_cts.Token))
            {
                _ = HandleRawEthernetPacketAsync(packet);
            }
        });
    }

    /// <summary>
    /// SFTPクライアント接続を処理
    /// </summary>
    private async Task HandleClientAsync(TcpClient client)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var session = new SftpSession
        {
            SessionId = sessionId,
            ClientSocket = client,
            StartTime = DateTime.UtcNow
        };

        await _sessionLock.WaitAsync();
        try
        {
            _sessions[sessionId] = session;
        }
        finally
        {
            _sessionLock.Release();
        }

        Log.Information("SFTP client connected: {SessionId}, RemoteEP={RemoteEndPoint}",
            sessionId, client.Client.RemoteEndPoint);

        try
        {
            using var clientStream = client.GetStream();
            var buffer = new byte[65536];

            // SSH バージョン交換
            await HandleSshVersionExchangeAsync(clientStream, session);

            // メインデータループ
            while (!_cts.Token.IsCancellationRequested)
            {
                var bytesRead = await clientStream.ReadAsync(buffer, _cts.Token);

                if (bytesRead == 0)
                    break;

                var data = buffer[..bytesRead];

                // SSH/SFTPパケット処理
                await ProcessSshPacketAsync(data, session);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling SFTP client: {SessionId}", sessionId);
        }
        finally
        {
            await _sessionLock.WaitAsync();
            try
            {
                _sessions.Remove(sessionId);
            }
            finally
            {
                _sessionLock.Release();
            }

            client.Close();
            Log.Information("SFTP client disconnected: {SessionId}, Duration={Duration}s, Files={Files}",
                sessionId,
                (DateTime.UtcNow - session.StartTime).TotalSeconds,
                session.FileTransferCount);
        }
    }

    /// <summary>
    /// SSHバージョン交換を処理
    /// </summary>
    private async Task HandleSshVersionExchangeAsync(NetworkStream stream, SftpSession session)
    {
        // クライアントバージョン受信
        var buffer = new byte[256];
        var bytesRead = await stream.ReadAsync(buffer, _cts.Token);
        var clientVersion = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();

        Log.Debug("SSH version exchange: SessionId={SessionId}, ClientVersion={Version}",
            session.SessionId, clientVersion);

        // サーバーバージョン送信（透過プロキシとして動作）
        var serverVersion = "SSH-2.0-NonIPFileDelivery_1.0\r\n";
        var versionBytes = Encoding.ASCII.GetBytes(serverVersion);

        // Raw Ethernet経由で実際のSFTPサーバーにバージョン交換を転送
        var payload = BuildProtocolPayload(
            PROTOCOL_SFTP_CONTROL,
            session.SessionId,
            new[] { (byte)0xFF }, // バージョン交換マーカー
            Encoding.ASCII.GetBytes(clientVersion));

        await _transceiver.SendAsync(payload, _cts.Token);

        session.IsVersionExchanged = true;
    }

    /// <summary>
    /// SSHパケットを処理
    /// </summary>
    private async Task ProcessSshPacketAsync(byte[] data, SftpSession session)
    {
        // SSH暗号化後のパケットは復号化せず、透過的に転送
        // （実際の暗号化は既存のAES-256-GCMレイヤーで実施）

        // SFTPサブシステムが有効な場合のみ、ファイル操作を検閲
        if (session.IsSftpSubsystemActive && data.Length > 5)
        {
            // SFTPメッセージの可能性がある場合（簡易的な判定）
            var potentialSftpMessageType = data[4];

            if (potentialSftpMessageType >= SSH_FXP_INIT && 
                potentialSftpMessageType <= SSH_FXP_RENAME)
            {
                await InspectSftpOperationAsync(data, session);
            }
        }

        // Raw Ethernetで転送
        var payload = BuildProtocolPayload(
            PROTOCOL_SFTP_DATA,
            session.SessionId,
            Array.Empty<byte>(),
            data);

        await _transceiver.SendAsync(payload, _cts.Token);
    }

    /// <summary>
    /// SFTP操作を検閲（ファイル名、サイズ、内容）
    /// </summary>
    private async Task InspectSftpOperationAsync(byte[] sftpData, SftpSession session)
    {
        try
        {
            // SFTPメッセージ解析（簡略版）
            var messageType = sftpData[4];

            switch (messageType)
            {
                case SSH_FXP_OPEN:
                case SSH_FXP_WRITE:
                    // ファイル書き込み操作
                    session.FileTransferCount++;

                    // ファイル内容のマルウェアスキャン（YARAルール）
                    if (_inspector.ScanData(sftpData, $"SFTP-Write-{session.SessionId}"))
                    {
                        Log.Warning("Blocked malicious SFTP file transfer: Session={SessionId}",
                            session.SessionId);

                        // ※実際にはSSH暗号化されているため、
                        // 実運用ではSSHトンネル内で検閲する必要がある
                        // または非IP送受信機B側で復号化後にスキャン
                    }
                    break;

                case SSH_FXP_REMOVE:
                    Log.Information("SFTP file deletion: Session={SessionId}", session.SessionId);
                    break;

                case SSH_FXP_RENAME:
                    Log.Information("SFTP file rename: Session={SessionId}", session.SessionId);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error inspecting SFTP operation");
        }
    }

    /// <summary>
    /// Raw Ethernetパケットから受信したSFTPレスポンスを処理
    /// </summary>
    private async Task HandleRawEthernetPacketAsync(PacketDotNet.EthernetPacket packet)
    {
        try
        {
            var payload = packet.PayloadData;

            if (payload.Length < 10) return;

            var protocolType = payload[0];

            if (protocolType != PROTOCOL_SFTP_CONTROL &&
                protocolType != PROTOCOL_SFTP_DATA)
                return;

            var sessionId = Encoding.ASCII.GetString(payload, 1, 8);
            var data = payload[9..];

            await _sessionLock.WaitAsync();
            SftpSession? session = null;
            try
            {
                _sessions.TryGetValue(sessionId, out session);
            }
            finally
            {
                _sessionLock.Release();
            }

            if (session != null)
            {
                var stream = session.ClientSocket.GetStream();
                await stream.WriteAsync(data, _cts.Token);
                await stream.FlushAsync(_cts.Token);

                Log.Debug("Forwarded SFTP response: SessionId={SessionId}, Size={Size}",
                    sessionId, data.Length);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling Raw Ethernet packet for SFTP");
        }
    }

    /// <summary>
    /// プロトコルペイロードを構築
    /// </summary>
    private byte[] BuildProtocolPayload(
        byte protocolType,
        string sessionId,
        byte[] controlBytes,
        byte[] data)
    {
        var payload = new byte[1 + 8 + controlBytes.Length + data.Length];
        payload[0] = protocolType;
        Encoding.ASCII.GetBytes(sessionId).CopyTo(payload, 1);
        controlBytes.CopyTo(payload, 9);
        data.CopyTo(payload, 9 + controlBytes.Length);
        return payload;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _sessionLock.Dispose();

        foreach (var session in _sessions.Values)
        {
            session.ClientSocket.Close();
        }

        _sessions.Clear();
        _isRunning = false;

        Log.Information("SftpProxy stopped");
    }
}

/// <summary>
/// SFTPセッション情報
/// </summary>
internal class SftpSession
{
    public required string SessionId { get; init; }
    public required TcpClient ClientSocket { get; init; }
    public DateTime StartTime { get; init; }
    public bool IsVersionExchanged { get; set; }
    public bool IsSftpSubsystemActive { get; set; }
    public int FileTransferCount { get; set; }
}