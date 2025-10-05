using NonIpFileDelivery.Core;
using NonIpFileDelivery.Security;
using Serilog;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace NonIpFileDelivery.Protocols;

/// <summary>
/// SFTP (SSH File Transfer Protocol) プロキシサーバー (B側 - サーバー側)
/// Windows端末A ⇔ 非IP送受信機A ⇔ [Raw Ethernet] ⇔ 非IP送受信機B ⇔ SFTPサーバー
/// このクラスはRaw Ethernetから受信し、実際のSFTPサーバーに接続する
/// </summary>
public class SftpProxyB : IDisposable
{
    private readonly RawEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly IPEndPoint _targetSftpServer;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentDictionary<string, SftpSession> _sessions;
    private bool _isRunning;

    // プロトコル識別子
    private const byte PROTOCOL_SFTP = 0x03;

    /// <summary>
    /// SFTPプロキシ(B側)を初期化
    /// </summary>
    /// <param name="transceiver">Raw Ethernetトランシーバー</param>
    /// <param name="inspector">セキュリティインスペクター</param>
    /// <param name="targetSftpHost">Windows端末B側のSFTPサーバーホスト</param>
    /// <param name="targetSftpPort">Windows端末B側のSFTPサーバーポート</param>
    public SftpProxyB(
        RawEthernetTransceiver transceiver,
        SecurityInspector inspector,
        string targetSftpHost = "192.168.1.100",
        int targetSftpPort = 22)
    {
        _transceiver = transceiver;
        _inspector = inspector;
        _targetSftpServer = new IPEndPoint(IPAddress.Parse(targetSftpHost), targetSftpPort);
        _cts = new CancellationTokenSource();
        _sessions = new ConcurrentDictionary<string, SftpSession>();

        Log.Information("SftpProxyB initialized: Target={TargetHost}:{TargetPort}",
            targetSftpHost, targetSftpPort);
    }

    /// <summary>
    /// SFTPプロキシ(B側)を起動
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning) return;

        _isRunning = true;

        Log.Information("SftpProxyB started, connecting to SFTP server at {Target}", _targetSftpServer);

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
    /// Raw Ethernetパケットから受信したSSH/SFTPデータを処理
    /// </summary>
    private async Task HandleRawEthernetPacketAsync(PacketDotNet.EthernetPacket packet)
    {
        try
        {
            var payload = packet.PayloadData;

            if (payload.Length < 10) return; // 最小ヘッダサイズ

            var protocolType = payload[0];
            if (protocolType != PROTOCOL_SFTP) return;

            var sessionId = System.Text.Encoding.ASCII.GetString(payload, 1, 8);
            var data = payload[9..];

            Log.Debug("Received SFTP data via Raw Ethernet: Session={SessionId}, Size={Size}",
                sessionId, data.Length);

            // セッションを取得または作成
            var session = await GetOrCreateSessionAsync(sessionId);
            if (session == null)
            {
                Log.Error("Failed to create SFTP session: {SessionId}", sessionId);
                return;
            }

            // セキュリティ検閲（SSH暗号化されているため制限的）
            if (_inspector.ScanData(data, $"SFTP-{sessionId}"))
            {
                Log.Warning("Blocked suspicious SFTP data: Session={SessionId}, Size={Size}",
                    sessionId, data.Length);
                return;
            }

            // SFTPサーバーにデータを送信
            await session.ServerStream.WriteAsync(data, _cts.Token);
            await session.ServerStream.FlushAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling Raw Ethernet packet");
        }
    }

    /// <summary>
    /// セッションを取得または作成
    /// </summary>
    private async Task<SftpSession?> GetOrCreateSessionAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var existingSession))
        {
            return existingSession;
        }

        // 新しいセッションを作成
        try
        {
            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(_targetSftpServer.Address, _targetSftpServer.Port, _cts.Token);

            var session = new SftpSession
            {
                SessionId = sessionId,
                Client = tcpClient,
                ServerStream = tcpClient.GetStream()
            };

            _sessions.TryAdd(sessionId, session);

            // サーバーからのレスポンス受信ループを開始
            _ = Task.Run(async () => await ReceiveFromServerAsync(session));

            Log.Information("Created new SFTP session: {SessionId}", sessionId);
            return session;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to connect to SFTP server: {SessionId}", sessionId);
            return null;
        }
    }

    /// <summary>
    /// SFTPサーバーからのレスポンスを受信してRaw Ethernetで返送
    /// </summary>
    private async Task ReceiveFromServerAsync(SftpSession session)
    {
        try
        {
            var buffer = new byte[65536]; // 64KB buffer

            while (!_cts.Token.IsCancellationRequested)
            {
                var bytesRead = await session.ServerStream.ReadAsync(buffer, _cts.Token);

                if (bytesRead == 0)
                    break;

                var data = buffer[..bytesRead];
                Log.Debug("Received SFTP data from server: Session={SessionId}, Size={Size}",
                    session.SessionId, bytesRead);

                // Raw Ethernetで返送
                var payload = BuildProtocolPayload(PROTOCOL_SFTP, session.SessionId, data);
                await _transceiver.SendAsync(payload, _cts.Token);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error receiving from SFTP server: {SessionId}", session.SessionId);
        }
        finally
        {
            // セッションをクリーンアップ
            _sessions.TryRemove(session.SessionId, out _);
            session.Dispose();
            Log.Information("SFTP session closed: {SessionId}", session.SessionId);
        }
    }

    /// <summary>
    /// プロトコルペイロードを構築
    /// </summary>
    private byte[] BuildProtocolPayload(byte protocolType, string sessionId, byte[] data)
    {
        var payload = new byte[1 + 8 + data.Length];
        payload[0] = protocolType;
        System.Text.Encoding.ASCII.GetBytes(sessionId.PadRight(8)[..8]).CopyTo(payload, 1);
        data.CopyTo(payload, 9);
        return payload;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _isRunning = false;

        // すべてのセッションをクローズ
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();

        Log.Information("SftpProxyB stopped");
    }

    /// <summary>
    /// SFTPセッション情報
    /// </summary>
    private class SftpSession : IDisposable
    {
        public required string SessionId { get; init; }
        public required TcpClient Client { get; init; }
        public required NetworkStream ServerStream { get; init; }

        public void Dispose()
        {
            ServerStream?.Dispose();
            Client?.Dispose();
        }
    }
}
