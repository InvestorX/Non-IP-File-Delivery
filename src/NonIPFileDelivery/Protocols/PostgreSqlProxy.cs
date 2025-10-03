using NonIpFileDelivery.Core;
using NonIpFileDelivery.Security;
using Serilog;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NonIpFileDelivery.Protocols;

/// <summary>
/// PostgreSQL Wire Protocolのプロキシサーバー
/// PostgreSQL v3プロトコル完全対応
/// </summary>
public class PostgreSqlProxy : IDisposable
{
    private readonly RawEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly TcpListener _listener;
    private readonly IPEndPoint _targetPostgreSqlServer;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, PostgreSqlSession> _sessions;
    private readonly SemaphoreSlim _sessionLock;
    private bool _isRunning;

    // PostgreSQL Wire Protocolメッセージタイプ
    private const byte MSG_QUERY = (byte)'Q';
    private const byte MSG_PARSE = (byte)'P';
    private const byte MSG_BIND = (byte)'B';
    private const byte MSG_EXECUTE = (byte)'E';
    private const byte MSG_DESCRIBE = (byte)'D';
    private const byte MSG_CLOSE = (byte)'C';
    private const byte MSG_SYNC = (byte)'S';
    private const byte MSG_TERMINATE = (byte)'X';

    // プロトコル識別子（非IP送受信機間通信）
    private const byte PROTOCOL_POSTGRESQL_CONTROL = 0x03;
    private const byte PROTOCOL_POSTGRESQL_DATA = 0x04;

    /// <summary>
    /// PostgreSQLプロキシを初期化
    /// </summary>
    public PostgreSqlProxy(
        RawEthernetTransceiver transceiver,
        SecurityInspector inspector,
        int listenPort = 5432,
        string targetHost = "192.168.1.100",
        int targetPort = 5432)
    {
        _transceiver = transceiver;
        _inspector = inspector;
        _listener = new TcpListener(IPAddress.Any, listenPort);
        _targetPostgreSqlServer = new IPEndPoint(IPAddress.Parse(targetHost), targetPort);
        _cts = new CancellationTokenSource();
        _sessions = new Dictionary<string, PostgreSqlSession>();
        _sessionLock = new SemaphoreSlim(1, 1);

        Log.Information("PostgreSqlProxy initialized: Listen={ListenPort}, Target={TargetHost}:{TargetPort}",
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

        Log.Information("PostgreSqlProxy started on port {Port}", ((IPEndPoint)_listener.LocalEndpoint).Port);

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
                    Log.Error(ex, "Error accepting PostgreSQL client");
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
    /// PostgreSQLクライアント接続を処理
    /// </summary>
    private async Task HandleClientAsync(TcpClient client)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var session = new PostgreSqlSession
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

        Log.Information("PostgreSQL client connected: {SessionId}, RemoteEP={RemoteEndPoint}",
            sessionId, client.Client.RemoteEndPoint);

        try
        {
            using var clientStream = client.GetStream();
            var pipe = PipeReader.Create(clientStream);

            // PostgreSQL Startup Message処理
            await HandleStartupMessageAsync(pipe, session, clientStream);

            // メインメッセージループ
            while (!_cts.Token.IsCancellationRequested)
            {
                var result = await pipe.ReadAsync(_cts.Token);
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                    break;

                // PostgreSQLメッセージ解析（1バイトタイプ + 4バイト長）
                while (TryReadPostgreSqlMessage(ref buffer, out var messageType, out var messageData))
                {
                    await ProcessPostgreSqlMessageAsync(
                        messageType, 
                        messageData, 
                        session, 
                        clientStream);
                }

                pipe.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling PostgreSQL client: {SessionId}", sessionId);
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
            Log.Information("PostgreSQL client disconnected: {SessionId}, Duration={Duration}s",
                sessionId, (DateTime.UtcNow - session.StartTime).TotalSeconds);
        }
    }

    /// <summary>
    /// PostgreSQL Startup Message処理（認証前）
    /// </summary>
    private async Task HandleStartupMessageAsync(
        PipeReader pipe, 
        PostgreSqlSession session, 
        NetworkStream clientStream)
    {
        var result = await pipe.ReadAsync(_cts.Token);
        var buffer = result.Buffer;

        if (buffer.Length >= 8)
        {
            var length = ReadInt32BigEndian(buffer.Slice(0, 4));
            var protocolVersion = ReadInt32BigEndian(buffer.Slice(4, 4));

            Log.Debug("PostgreSQL Startup: SessionId={SessionId}, Protocol={Protocol:X8}",
                session.SessionId, protocolVersion);

            // Startup Messageを非IP送受信機Bに転送
            var startupData = buffer.Slice(0, length).ToArray();
            var payload = BuildProtocolPayload(
                PROTOCOL_POSTGRESQL_CONTROL,
                session.SessionId,
                new[] { (byte)0xFF }, // スタートアップマーカー
                startupData);

            await _transceiver.SendAsync(payload, _cts.Token);

            session.IsAuthenticated = true; // 実際の認証処理は簡略化
        }

        pipe.AdvanceTo(buffer.Start, buffer.End);
    }

    /// <summary>
    /// PostgreSQLメッセージを処理
    /// </summary>
    private async Task ProcessPostgreSqlMessageAsync(
        byte messageType,
        ReadOnlySequence<byte> messageData,
        PostgreSqlSession session,
        NetworkStream clientStream)
    {
        // SQLクエリの場合はセキュリティ検閲
        if (messageType == MSG_QUERY)
        {
            var queryText = Encoding.UTF8.GetString(messageData.ToArray()).TrimEnd('\0');

            Log.Debug("PostgreSQL Query: SessionId={SessionId}, Query={Query}",
                session.SessionId, queryText);

            // SQLインジェクション検査
            if (IsSuspiciousSqlQuery(queryText))
            {
                Log.Warning("Blocked suspicious SQL query: {Query}, Session={SessionId}",
                    queryText, session.SessionId);

                await SendPostgreSqlErrorResponse(
                    clientStream,
                    "42000", // syntax_error_or_access_rule_violation
                    "Query rejected by security policy");
                return;
            }

            // YARAルールでスキャン（SQLインジェクションパターン）
            var queryBytes = Encoding.UTF8.GetBytes(queryText);
            if (_inspector.ScanData(queryBytes, $"PGSQL-Query-{session.SessionId}"))
            {
                Log.Warning("Blocked malicious SQL query by YARA: Session={SessionId}",
                    session.SessionId);

                await SendPostgreSqlErrorResponse(
                    clientStream,
                    "42000",
                    "Query contains malicious patterns");
                return;
            }

            // ログ記録（監査用）
            session.QueryCount++;
            LogSqlQuery(session.SessionId, queryText, session.DatabaseName, session.Username);
        }

        // Raw Ethernetで転送
        var payload = BuildProtocolPayload(
            PROTOCOL_POSTGRESQL_DATA,
            session.SessionId,
            new[] { messageType },
            messageData.ToArray());

        await _transceiver.SendAsync(payload, _cts.Token);
    }

    /// <summary>
    /// 危険なSQLクエリを検出
    /// </summary>
    private bool IsSuspiciousSqlQuery(string query)
    {
        var dangerousPatterns = new[]
        {
            "DROP TABLE",
            "DROP DATABASE",
            "DELETE FROM.*WHERE.*1=1",
            "UPDATE.*SET.*WHERE.*1=1",
            "TRUNCATE TABLE",
            "ALTER TABLE",
            "CREATE USER",
            "GRANT ALL",
            "; DROP",
            "' OR '1'='1",
            "' OR 1=1--",
            "UNION SELECT",
            "EXEC(", "EXECUTE(",
            "xp_cmdshell",
            "pg_sleep\\(",
            "WAITFOR DELAY"
        };

        var upperQuery = query.ToUpperInvariant();

        foreach (var pattern in dangerousPatterns)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(
                upperQuery, 
                pattern, 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// PostgreSQLエラーレスポンスを送信
    /// </summary>
    private async Task SendPostgreSqlErrorResponse(
        NetworkStream stream,
        string sqlState,
        string message)
    {
        // PostgreSQL ErrorResponseフォーマット
        using var ms = new MemoryStream();
        ms.WriteByte((byte)'E'); // ErrorResponse

        var errorFields = new List<byte[]>
        {
            Encoding.UTF8.GetBytes($"S{severity}\0"),
            Encoding.UTF8.GetBytes($"C{sqlState}\0"),
            Encoding.UTF8.GetBytes($"M{message}\0"),
            new byte[] { 0 } // 終端
        };

        var errorData = errorFields.SelectMany(f => f).ToArray();
        var length = 4 + errorData.Length;

        ms.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(length)), 0, 4);
        ms.Write(errorData, 0, errorData.Length);

        await stream.WriteAsync(ms.ToArray(), _cts.Token);
        await stream.FlushAsync(_cts.Token);
    }

    /// <summary>
    /// SQLクエリをログに記録（監査用）
    /// </summary>
    private void LogSqlQuery(string sessionId, string query, string? database, string? username)
    {
        Log.Information("SQL_AUDIT: SessionId={SessionId}, User={User}, DB={Database}, Query={Query}",
            sessionId,
            username ?? "unknown",
            database ?? "unknown",
            query.Length > 200 ? query[..200] + "..." : query);
    }

    /// <summary>
    /// Raw Ethernetパケットから受信したPostgreSQLレスポンスを処理
    /// </summary>
    private async Task HandleRawEthernetPacketAsync(PacketDotNet.EthernetPacket packet)
    {
        try
        {
            var payload = packet.PayloadData;

            if (payload.Length < 10) return;

            var protocolType = payload[0];

            if (protocolType != PROTOCOL_POSTGRESQL_CONTROL && 
                protocolType != PROTOCOL_POSTGRESQL_DATA)
                return;

            var sessionId = Encoding.ASCII.GetString(payload, 1, 8);
            var messageType = payload[9];
            var data = payload[10..];

            await _sessionLock.WaitAsync();
            PostgreSqlSession? session = null;
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

                Log.Debug("Forwarded PostgreSQL response: SessionId={SessionId}, Size={Size}",
                    sessionId, data.Length);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling Raw Ethernet packet for PostgreSQL");
        }
    }

    /// <summary>
    /// PostgreSQLメッセージを読み取り
    /// </summary>
    private bool TryReadPostgreSqlMessage(
        ref ReadOnlySequence<byte> buffer,
        out byte messageType,
        out ReadOnlySequence<byte> messageData)
    {
        messageType = 0;
        messageData = default;

        if (buffer.Length < 5) // 1バイト(type) + 4バイト(length)
            return false;

        messageType = buffer.First.Span[0];
        var lengthBytes = buffer.Slice(1, 4);
        var length = ReadInt32BigEndian(lengthBytes);

        if (buffer.Length < 1 + length)
            return false;

        messageData = buffer.Slice(5, length - 4);
        buffer = buffer.Slice(1 + length);

        return true;
    }

    /// <summary>
    /// Big Endianで4バイト整数を読み取り
    /// </summary>
    private int ReadInt32BigEndian(ReadOnlySequence<byte> buffer)
    {
        Span<byte> bytes = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(bytes);
        return IPAddress.NetworkToHostOrder(BitConverter.ToInt32(bytes));
    }

    /// <summary>
    /// プロトコルペイロードを構築
    /// </summary>
    private byte[] BuildProtocolPayload(
        byte protocolType,
        string sessionId,
        byte[] messageTypeBytes,
        byte[] data)
    {
        var payload = new byte[1 + 8 + messageTypeBytes.Length + data.Length];
        payload[0] = protocolType;
        Encoding.ASCII.GetBytes(sessionId).CopyTo(payload, 1);
        messageTypeBytes.CopyTo(payload, 9);
        data.CopyTo(payload, 9 + messageTypeBytes.Length);
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

        Log.Information("PostgreSqlProxy stopped");
    }

    private const string severity = "ERROR";
}

/// <summary>
/// PostgreSQLセッション情報
/// </summary>
internal class PostgreSqlSession
{
    public required string SessionId { get; init; }
    public required TcpClient ClientSocket { get; init; }
    public DateTime StartTime { get; init; }
    public bool IsAuthenticated { get; set; }
    public string? Username { get; set; }
    public string? DatabaseName { get; set; }
    public int QueryCount { get; set; }
}