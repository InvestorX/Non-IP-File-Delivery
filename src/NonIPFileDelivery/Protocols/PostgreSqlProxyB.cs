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
/// PostgreSQL Wire Protocolプロキシサーバー (B側 - サーバー側)
/// Windows端末A ⇔ 非IP送受信機A ⇔ [Raw Ethernet] ⇔ 非IP送受信機B ⇔ PostgreSQLサーバー
/// このクラスはRaw Ethernetから受信し、実際のPostgreSQLサーバーに接続する
/// </summary>
public class PostgreSqlProxyB : IDisposable
{
    private readonly RawEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly IPEndPoint _targetPgServer;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentDictionary<string, PostgreSqlSession> _sessions;
    private bool _isRunning;

    // プロトコル識別子
    private const byte PROTOCOL_POSTGRESQL = 0x04;

    /// <summary>
    /// PostgreSQLプロキシ(B側)を初期化
    /// </summary>
    /// <param name="transceiver">Raw Ethernetトランシーバー</param>
    /// <param name="inspector">セキュリティインスペクター</param>
    /// <param name="targetPgHost">Windows端末B側のPostgreSQLサーバーホスト</param>
    /// <param name="targetPgPort">Windows端末B側のPostgreSQLサーバーポート</param>
    public PostgreSqlProxyB(
        RawEthernetTransceiver transceiver,
        SecurityInspector inspector,
        string targetPgHost = "192.168.1.100",
        int targetPgPort = 5432)
    {
        _transceiver = transceiver;
        _inspector = inspector;
        _targetPgServer = new IPEndPoint(IPAddress.Parse(targetPgHost), targetPgPort);
        _cts = new CancellationTokenSource();
        _sessions = new ConcurrentDictionary<string, PostgreSqlSession>();

        Log.Information("PostgreSqlProxyB initialized: Target={TargetHost}:{TargetPort}",
            targetPgHost, targetPgPort);
    }

    /// <summary>
    /// PostgreSQLプロキシ(B側)を起動
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning) return;

        _isRunning = true;

        Log.Information("PostgreSqlProxyB started, connecting to PostgreSQL server at {Target}", _targetPgServer);

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
    /// Raw Ethernetパケットから受信したPostgreSQLメッセージを処理
    /// </summary>
    private async Task HandleRawEthernetPacketAsync(PacketDotNet.EthernetPacket packet)
    {
        try
        {
            var payload = packet.PayloadData;

            if (payload.Length < 10) return; // 最小ヘッダサイズ

            var protocolType = payload[0];
            if (protocolType != PROTOCOL_POSTGRESQL) return;

            var sessionId = System.Text.Encoding.ASCII.GetString(payload, 1, 8);
            var data = payload[9..];

            Log.Debug("Received PostgreSQL data via Raw Ethernet: Session={SessionId}, Size={Size}",
                sessionId, data.Length);

            // セッションを取得または作成
            var session = await GetOrCreateSessionAsync(sessionId);
            if (session == null)
            {
                Log.Error("Failed to create PostgreSQL session: {SessionId}", sessionId);
                return;
            }

            // セキュリティ検閲（簡易的なSQLインジェクションチェック）
            if (_inspector.ScanData(data, $"POSTGRESQL-{sessionId}"))
            {
                Log.Warning("Blocked suspicious PostgreSQL data: Session={SessionId}, Size={Size}",
                    sessionId, data.Length);
                
                // エラーメッセージを返送
                var errorMsg = CreatePostgreSqlErrorMessage("Security policy violation");
                var errorPayload = BuildProtocolPayload(PROTOCOL_POSTGRESQL, sessionId, errorMsg);
                await _transceiver.SendAsync(errorPayload, _cts.Token);
                return;
            }

            // PostgreSQLサーバーにデータを送信
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
    private async Task<PostgreSqlSession?> GetOrCreateSessionAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var existingSession))
        {
            return existingSession;
        }

        // 新しいセッションを作成
        try
        {
            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(_targetPgServer.Address, _targetPgServer.Port, _cts.Token);

            var session = new PostgreSqlSession
            {
                SessionId = sessionId,
                Client = tcpClient,
                ServerStream = tcpClient.GetStream()
            };

            _sessions.TryAdd(sessionId, session);

            // サーバーからのレスポンス受信ループを開始
            _ = Task.Run(async () => await ReceiveFromServerAsync(session));

            Log.Information("Created new PostgreSQL session: {SessionId}", sessionId);
            return session;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to connect to PostgreSQL server: {SessionId}", sessionId);
            return null;
        }
    }

    /// <summary>
    /// PostgreSQLサーバーからのレスポンスを受信してRaw Ethernetで返送
    /// </summary>
    private async Task ReceiveFromServerAsync(PostgreSqlSession session)
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
                Log.Debug("Received PostgreSQL data from server: Session={SessionId}, Size={Size}",
                    session.SessionId, bytesRead);

                // Raw Ethernetで返送
                var payload = BuildProtocolPayload(PROTOCOL_POSTGRESQL, session.SessionId, data);
                await _transceiver.SendAsync(payload, _cts.Token);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error receiving from PostgreSQL server: {SessionId}", session.SessionId);
        }
        finally
        {
            // セッションをクリーンアップ
            _sessions.TryRemove(session.SessionId, out _);
            session.Dispose();
            Log.Information("PostgreSQL session closed: {SessionId}", session.SessionId);
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

    /// <summary>
    /// PostgreSQLエラーメッセージを作成
    /// </summary>
    private byte[] CreatePostgreSqlErrorMessage(string message)
    {
        // PostgreSQL Wire Protocolのエラーメッセージフォーマット
        // 'E' (Error) + length + severity + message
        var msgBytes = System.Text.Encoding.UTF8.GetBytes(message);
        var errorMsg = new byte[1 + 4 + 1 + msgBytes.Length + 1];
        
        errorMsg[0] = (byte)'E'; // Error message
        var length = errorMsg.Length - 1;
        errorMsg[1] = (byte)(length >> 24);
        errorMsg[2] = (byte)(length >> 16);
        errorMsg[3] = (byte)(length >> 8);
        errorMsg[4] = (byte)length;
        errorMsg[5] = (byte)'S'; // Severity
        msgBytes.CopyTo(errorMsg, 6);
        errorMsg[^1] = 0; // Null terminator
        
        return errorMsg;
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

        Log.Information("PostgreSqlProxyB stopped");
    }

    /// <summary>
    /// PostgreSQLセッション情報
    /// </summary>
    private class PostgreSqlSession : IDisposable
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
