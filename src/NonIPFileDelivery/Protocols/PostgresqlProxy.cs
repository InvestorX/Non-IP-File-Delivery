using NonIpFileDelivery.Core;
using NonIpFileDelivery.Security;
using Serilog;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NonIpFileDelivery.Protocols;

/// <summary>
/// PostgreSQL Wire Protocolのプロキシサーバー
/// Windows端末A ⇔ 非IP送受信機A ⇔ [Raw Ethernet] ⇔ 非IP送受信機B ⇔ PostgreSQLサーバー
/// </summary>
public class PostgresqlProxy : IDisposable
{
    private readonly RawEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly TcpListener _listener;
    private readonly IPEndPoint _targetPostgresServer;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, PostgresqlSession> _sessions;
    private readonly object _sessionLock = new();
    private bool _isRunning;

    // プロトコル識別子
    private const byte PROTOCOL_POSTGRESQL = 0x03;

    // PostgreSQLメッセージタイプ
    private const byte MSG_QUERY = (byte)'Q';              // Simple Query
    private const byte MSG_PARSE = (byte)'P';              // Prepared Statement Parse
    private const byte MSG_BIND = (byte)'B';               // Bind
    private const byte MSG_EXECUTE = (byte)'E';            // Execute
    private const byte MSG_CLOSE = (byte)'C';              // Close
    private const byte MSG_DESCRIBE = (byte)'D';           // Describe
    private const byte MSG_SYNC = (byte)'S';               // Sync
    private const byte MSG_TERMINATE = (byte)'X';          // Terminate
    private const byte MSG_PASSWORD = (byte)'p';           // Password
    private const byte MSG_STARTUP = 0x00;                 // Startup Message

    /// <summary>
    /// PostgreSQLプロキシを初期化
    /// </summary>
    /// <param name="transceiver">Raw Ethernetトランシーバー</param>
    /// <param name="inspector">セキュリティインスペクター</param>
    /// <param name="listenPort">Windows端末Aからの接続待ち受けポート</param>
    /// <param name="targetHost">Windows端末B側のPostgreSQLサーバーホスト</param>
    /// <param name="targetPort">Windows端末B側のPostgreSQLサーバーポート</param>
    public PostgresqlProxy(
        RawEthernetTransceiver transceiver,
        SecurityInspector inspector,
        int listenPort = 5432,
        string targetHost = "192.168.1.100",
        int targetPort = 5432)
    {
        _transceiver = transceiver;
        _inspector = inspector;
        _listener = new TcpListener(IPAddress.Any, listenPort);
        _targetPostgresServer = new IPEndPoint(IPAddress.Parse(targetHost), targetPort);
        _cts = new CancellationTokenSource();
        _sessions = new Dictionary<string, PostgresqlSession>();

        Log.Information("PostgresqlProxy initialized: Listen={ListenPort}, Target={TargetHost}:{TargetPort}",
            listenPort, targetHost, targetPort);
    }

    /// <summary>
    /// PostgreSQLプロキシを起動
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning) return;

        _listener.Start();
        _isRunning = true;

        Log.Information("PostgresqlProxy started, listening on port {Port}",
            ((IPEndPoint)_listener.LocalEndpoint).Port);

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
    /// Windows端末Aからの接続を処理
    /// </summary>
    private async Task HandleClientAsync(TcpClient client)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var session = new PostgresqlSession
        {
            SessionId = sessionId,
            Client = client,
            ConnectedAt = DateTime.UtcNow
        };

        lock (_sessionLock)
        {
            _sessions[sessionId] = session;
        }

        Log.Information("PostgreSQL client connected: {SessionId}, RemoteEP={RemoteEndPoint}",
            sessionId, client.Client.RemoteEndPoint);

        try
        {
            using var clientStream = client.GetStream();
            var pipe = PipeReader.Create(clientStream);

            while (!_cts.Token.IsCancellationRequested)
            {
                var result = await pipe.ReadAsync(_cts.Token);
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                    break;

                // PostgreSQLメッセージ解析
                while (TryReadPostgresMessage(ref buffer, out var messageType, out var messageData))
                {
                    // セキュリティ検閲
                    if (await InspectPostgresMessageAsync(sessionId, messageType, messageData))
                    {
                        Log.Warning("Blocked malicious PostgreSQL message: Type={MessageType}, Session={SessionId}",
                            (char)messageType, sessionId);

                        // エラーレスポンスを返す
                        await SendErrorResponse(clientStream, "Security policy violation");
                        continue;
                    }

                    // Raw Ethernetで送信
                    var payload = BuildProtocolPayload(PROTOCOL_POSTGRESQL, sessionId, messageType, messageData);
                    await _transceiver.SendAsync(payload, _cts.Token);

                    Log.Debug("Forwarded PostgreSQL message via Raw Ethernet: Type={MessageType}, Size={Size}",
                        (char)messageType, messageData.Length);
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
            lock (_sessionLock)
            {
                _sessions.Remove(sessionId);
            }
            client.Close();
            Log.Information("PostgreSQL client disconnected: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// PostgreSQLメッセージの検閲
    /// </summary>
    private async Task<bool> InspectPostgresMessageAsync(string sessionId, byte messageType, byte[] messageData)
    {
        // Queryメッセージの場合、SQL文を解析
        if (messageType == MSG_QUERY)
        {
            var sql = Encoding.UTF8.GetString(messageData).TrimEnd('\0');

            Log.Information("PostgreSQL Query: Session={SessionId}, SQL={Sql}",
                sessionId, sql.Length > 200 ? sql[..200] + "..." : sql);

            // SQLインジェクション検出
            if (DetectSqlInjection(sql))
            {
                Log.Warning("SQL Injection detected: Session={SessionId}, SQL={Sql}",
                    sessionId, sql);
                return true; // ブロック
            }

            // 危険なSQL文の検出
            if (DetectDangerousSql(sql))
            {
                Log.Warning("Dangerous SQL detected: Session={SessionId}, SQL={Sql}",
                    sessionId, sql);
                return true; // ブロック
            }
        }

        // Prepared Statementの解析
        else if (messageType == MSG_PARSE)
        {
            // ステートメント名（null-terminated string）とSQL文を抽出
            var nullIndex = Array.IndexOf(messageData, (byte)0);
            if (nullIndex > 0 && nullIndex < messageData.Length - 1)
            {
                var sql = Encoding.UTF8.GetString(messageData, nullIndex + 1, messageData.Length - nullIndex - 1)
                    .TrimEnd('\0');

                if (DetectSqlInjection(sql) || DetectDangerousSql(sql))
                {
                    return true; // ブロック
                }
            }
        }

        return false; // 許可
    }

    /// <summary>
    /// SQLインジェクション検出
    /// </summary>
    private bool DetectSqlInjection(string sql)
    {
        var suspiciousPatterns = new[]
        {
            "' OR '1'='1",
            "' OR 1=1--",
            "'; DROP TABLE",
            "'; DELETE FROM",
            "UNION SELECT",
            "EXEC(",
            "EXECUTE(",
            "xp_cmdshell",
            "/**/",
            "||", // 文字列連結によるインジェクション
        };

        var normalizedSql = sql.ToUpperInvariant();

        foreach (var pattern in suspiciousPatterns)
        {
            if (normalizedSql.Contains(pattern.ToUpperInvariant()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 危険なSQL文の検出
    /// </summary>
    private bool DetectDangerousSql(string sql)
    {
        var normalizedSql = sql.ToUpperInvariant().Trim();

        // WHERE句のないDELETE/UPDATE
        if ((normalizedSql.StartsWith("DELETE ") || normalizedSql.StartsWith("UPDATE ")) &&
            !normalizedSql.Contains("WHERE"))
        {
            return true;
        }

        // DROP/TRUNCATE文
        if (normalizedSql.StartsWith("DROP ") || normalizedSql.StartsWith("TRUNCATE "))
        {
            return true;
        }

        // システムテーブルへのアクセス
        if (normalizedSql.Contains("PG_SHADOW") ||
            normalizedSql.Contains("PG_USER") ||
            normalizedSql.Contains("INFORMATION_SCHEMA"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// PostgreSQLメッセージを読み取り
    /// </summary>
    private bool TryReadPostgresMessage(ref ReadOnlySequence<byte> buffer, out byte messageType, out byte[] messageData)
    {
        messageType = 0;
        messageData = Array.Empty<byte>();

        // 最小メッセージサイズ: 1バイト(type) + 4バイト(length)
        if (buffer.Length < 5)
            return false;

        var reader = new SequenceReader<byte>(buffer);

        // メッセージタイプを読み取り
        reader.TryRead(out messageType);

        // メッセージ長を読み取り（Big Endian, 長さ自体を含む）
        Span<byte> lengthBytes = stackalloc byte[4];
        reader.TryCopyTo(lengthBytes);
        var messageLength = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);

        // バッファに十分なデータがあるか確認
        if (buffer.Length < 1 + messageLength)
            return false;

        // メッセージデータを読み取り（長さフィールドを除く）
        messageData = new byte[messageLength - 4];
        buffer.Slice(5, messageLength - 4).CopyTo(messageData);

        // バッファを進める
        buffer = buffer.Slice(1 + messageLength);

        return true;
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
            if (protocolType != PROTOCOL_POSTGRESQL) return;

            var sessionId = Encoding.ASCII.GetString(payload, 1, 8);
            var messageType = payload[9];
            var data = payload[10..];

            PostgresqlSession? session;
            lock (_sessionLock)
            {
                if (!_sessions.TryGetValue(sessionId, out session))
                {
                    Log.Warning("PostgreSQL session not found: {SessionId}", sessionId);
                    return;
                }
            }

            // Windows端末Aに返送
            var stream = session.Client.GetStream();
            await stream.WriteAsync(new byte[] { messageType }, _cts.Token);
            
            // メッセージ長（Big Endian）
            var lengthBytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(lengthBytes, data.Length + 4);
            await stream.WriteAsync(lengthBytes, _cts.Token);
            
            // データ
            await stream.WriteAsync(data, _cts.Token);

            Log.Debug("Forwarded PostgreSQL response to client: Type={MessageType}, Size={Size}",
                (char)messageType, data.Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling PostgreSQL Raw Ethernet packet");
        }
    }

    /// <summary>
    /// プロトコルペイロードを構築
    /// </summary>
    private byte[] BuildProtocolPayload(byte protocolType, string sessionId, byte messageType, byte[] data)
    {
        var payload = new byte[1 + 8 + 1 + data.Length];
        payload[0] = protocolType;
        Encoding.ASCII.GetBytes(sessionId).CopyTo(payload, 1);
        payload[9] = messageType;
        data.CopyTo(payload, 10);
        return payload;
    }

    /// <summary>
    /// エラーレスポンスを送信
    /// </summary>
    private async Task SendErrorResponse(NetworkStream stream, string message)
    {
        // PostgreSQL Error Response format: 'E' + length + fields
        var errorMessage = Encoding.UTF8.GetBytes($"SERROR\0C42501\0M{message}\0\0");
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, errorMessage.Length + 4);

        await stream.WriteAsync(new byte[] { (byte)'E' }, _cts.Token);
        await stream.WriteAsync(lengthBytes, _cts.Token);
        await stream.WriteAsync(errorMessage, _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();

        lock (_sessionLock)
        {
            foreach (var session in _sessions.Values)
            {
                session.Client?.Close();
            }
            _sessions.Clear();
        }

        _isRunning = false;
        Log.Information("PostgresqlProxy stopped");
    }

    /// <summary>
    /// PostgreSQLセッション情報
    /// </summary>
    private class PostgresqlSession
    {
        public required string SessionId { get; init; }
        public required TcpClient Client { get; init; }
        public DateTime ConnectedAt { get; init; }
    }
}