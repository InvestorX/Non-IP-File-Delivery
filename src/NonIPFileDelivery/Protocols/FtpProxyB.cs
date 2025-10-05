using NonIpFileDelivery.Core;
using NonIpFileDelivery.Security;
using Serilog;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NonIpFileDelivery.Protocols;

/// <summary>
/// FTPプロトコルのプロキシサーバー (B側 - サーバー側)
/// Windows端末A ⇔ 非IP送受信機A ⇔ [Raw Ethernet] ⇔ 非IP送受信機B ⇔ FTPサーバー
/// このクラスはRaw Ethernetから受信し、実際のFTPサーバーに接続する
/// </summary>
public class FtpProxyB : IDisposable
{
    private readonly RawEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly IPEndPoint _targetFtpServer;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentDictionary<string, FtpSession> _sessions;
    private bool _isRunning;

    // プロトコル識別子
    private const byte PROTOCOL_FTP_CONTROL = 0x01;
    private const byte PROTOCOL_FTP_DATA = 0x02;

    /// <summary>
    /// FTPプロキシ(B側)を初期化
    /// </summary>
    /// <param name="transceiver">Raw Ethernetトランシーバー</param>
    /// <param name="inspector">セキュリティインスペクター</param>
    /// <param name="targetFtpHost">Windows端末B側のFTPサーバーホスト</param>
    /// <param name="targetFtpPort">Windows端末B側のFTPサーバーポート</param>
    public FtpProxyB(
        RawEthernetTransceiver transceiver,
        SecurityInspector inspector,
        string targetFtpHost = "192.168.1.100",
        int targetFtpPort = 21)
    {
        _transceiver = transceiver;
        _inspector = inspector;
        _targetFtpServer = new IPEndPoint(IPAddress.Parse(targetFtpHost), targetFtpPort);
        _cts = new CancellationTokenSource();
        _sessions = new ConcurrentDictionary<string, FtpSession>();

        Log.Information("FtpProxyB initialized: Target={TargetHost}:{TargetPort}",
            targetFtpHost, targetFtpPort);
    }

    /// <summary>
    /// FTPプロキシ(B側)を起動
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning) return;

        _isRunning = true;

        Log.Information("FtpProxyB started, connecting to FTP server at {Target}", _targetFtpServer);

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
    /// Raw Ethernetパケットから受信したFTPコマンドを処理
    /// </summary>
    private async Task HandleRawEthernetPacketAsync(PacketDotNet.EthernetPacket packet)
    {
        try
        {
            var payload = packet.PayloadData;

            if (payload.Length < 10) return; // 最小ヘッダサイズ

            var protocolType = payload[0];
            var sessionId = Encoding.ASCII.GetString(payload, 1, 8);
            var data = payload[9..];

            if (protocolType == PROTOCOL_FTP_CONTROL)
            {
                // FTPコマンドをFTPサーバーに転送
                var command = Encoding.ASCII.GetString(data);
                Log.Debug("Received FTP command via Raw Ethernet: {Command}, Session={SessionId}",
                    command.Trim(), sessionId);

                // セッションを取得または作成
                var session = await GetOrCreateSessionAsync(sessionId);
                if (session == null)
                {
                    Log.Error("Failed to create FTP session: {SessionId}", sessionId);
                    return;
                }

                // セキュリティ検閲
                if (_inspector.ValidateFtpCommand(command))
                {
                    Log.Warning("Blocked malicious FTP command: {Command}, Session={SessionId}",
                        command.Trim(), sessionId);

                    // エラーレスポンスを返送
                    var errorResponse = Encoding.ASCII.GetBytes("550 Command rejected by security policy.\r\n");
                    var errorPayload = BuildProtocolPayload(PROTOCOL_FTP_CONTROL, sessionId, errorResponse);
                    await _transceiver.SendAsync(errorPayload, _cts.Token);
                    return;
                }

                // FTPサーバーにコマンドを送信
                await session.ServerStream.WriteAsync(data, _cts.Token);
                await session.ServerStream.FlushAsync(_cts.Token);
            }
            else if (protocolType == PROTOCOL_FTP_DATA)
            {
                // FTPデータ転送（PUT/GET）
                if (_inspector.ScanData(data, $"FTP-DATA-{sessionId}"))
                {
                    Log.Warning("Blocked malicious FTP data transfer: Session={SessionId}, Size={Size}",
                        sessionId, data.Length);
                    return;
                }

                // TODO: データチャンネル処理（パッシブモード対応）
                Log.Debug("Received FTP data via Raw Ethernet: Session={SessionId}, Size={Size}",
                    sessionId, data.Length);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling Raw Ethernet packet");
        }
    }

    /// <summary>
    /// セッションを取得または作成
    /// </summary>
    private async Task<FtpSession?> GetOrCreateSessionAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var existingSession))
        {
            return existingSession;
        }

        // 新しいセッションを作成
        try
        {
            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(_targetFtpServer.Address, _targetFtpServer.Port, _cts.Token);

            var session = new FtpSession
            {
                SessionId = sessionId,
                Client = tcpClient,
                ServerStream = tcpClient.GetStream()
            };

            _sessions.TryAdd(sessionId, session);

            // サーバーからのレスポンス受信ループを開始
            _ = Task.Run(async () => await ReceiveFromServerAsync(session));

            Log.Information("Created new FTP session: {SessionId}", sessionId);
            return session;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to connect to FTP server: {SessionId}", sessionId);
            return null;
        }
    }

    /// <summary>
    /// FTPサーバーからのレスポンスを受信してRaw Ethernetで返送
    /// </summary>
    private async Task ReceiveFromServerAsync(FtpSession session)
    {
        try
        {
            var pipe = PipeReader.Create(session.ServerStream);

            while (!_cts.Token.IsCancellationRequested)
            {
                var result = await pipe.ReadAsync(_cts.Token);
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                    break;

                // FTPレスポンス解析（\r\n区切り）
                while (TryReadLine(ref buffer, out var line))
                {
                    var response = Encoding.ASCII.GetString(line);
                    Log.Debug("Received FTP response from server: {Response}, Session={SessionId}",
                        response.Trim(), session.SessionId);

                    // Raw Ethernetで返送
                    var payload = BuildProtocolPayload(PROTOCOL_FTP_CONTROL, session.SessionId, line.ToArray());
                    await _transceiver.SendAsync(payload, _cts.Token);
                }

                pipe.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error receiving from FTP server: {SessionId}", session.SessionId);
        }
        finally
        {
            // セッションをクリーンアップ
            _sessions.TryRemove(session.SessionId, out _);
            session.Dispose();
            Log.Information("FTP session closed: {SessionId}", session.SessionId);
        }
    }

    /// <summary>
    /// プロトコルペイロードを構築
    /// </summary>
    private byte[] BuildProtocolPayload(byte protocolType, string sessionId, byte[] data)
    {
        var payload = new byte[1 + 8 + data.Length];
        payload[0] = protocolType;
        Encoding.ASCII.GetBytes(sessionId.PadRight(8)[..8]).CopyTo(payload, 1);
        data.CopyTo(payload, 9);
        return payload;
    }

    /// <summary>
    /// パイプから1行読み取り（\r\n区切り）
    /// </summary>
    private bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        var position = buffer.PositionOf((byte)'\n');

        if (position == null)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(0, position.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
        return true;
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

        Log.Information("FtpProxyB stopped");
    }

    /// <summary>
    /// FTPセッション情報
    /// </summary>
    private class FtpSession : IDisposable
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
