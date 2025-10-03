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
/// PostgreSQL Wire Protocolプロキシサーバー
/// Windows端末A ⇔ 非IP送受信機A ⇔ [Raw Ethernet] ⇔ 非IP送受信機B ⇔ PostgreSQLサーバー
/// </summary>
public class PostgreSqlProxy : IDisposable
{
    private readonly RawEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly TcpListener _listener;
    private readonly IPEndPoint _targetPgServer;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, PostgreSqlSession> _sessions;
    private readonly object _sessionLock = new();
    private bool _isRunning;

    // プロトコル識別子
    private const byte PROTOCOL_PGSQL_STARTUP = 0x10;
    private const byte PROTOCOL_PGSQL_QUERY = 0x11;
    private const byte PROTOCOL_PGSQL_DATA = 0x12;
    private const byte PROTOCOL_PGSQL_RESPONSE = 0x13;

    // PostgreSQLメッセージタイプ
    private const byte MSG_QUERY = (byte)'Q';
    private const byte MSG_PARSE = (byte)'P';
    private const byte MSG_BIND = (byte)'B';
    private const byte MSG_EXECUTE = (byte)'E';
    private const byte MSG_TERMINATE = (byte)'X';

    /// <summary>
    /// PostgreSQLプロキシを初期化
    /// </summary>
    /// <param name="transceiver">Raw Ethernetトランシーバー</param>
    /// <param name="inspector">セキュリティインスペクター</param>
    /// <param name="listenPort">Windows端末Aからの接続待ち受けポート</param>
    /// <param name="targetPgHost">Windows端末B側のPostgreSQLサーバーホスト</param>
    /// <param name="targetPgPort">Windows端末B側のPostgreSQLサーバーポート</param>
    public PostgreSqlProxy(
        RawEthernetTransceiver transceiver,
        SecurityInspector inspector,
        int listenPort = 5432,
        string targetPgHost = "192.168.1.100",
        int targetPgPort = 5432)
    {
        _transceiver = transceiver;
        _inspector = inspector;
        _listener = new TcpListener(IPAddress.Any, listenPort);
        _targetPgServer = new IPEndPoint(IPAddress.Parse(targetPgHost), targetPgPort);
        _cts = new CancellationTokenSource();
        _sessions = new Dictionary<string, PostgreSqlSession>();

        Log.Information("PostgreSqlProxy initialized: Listen={ListenPort}, Target={TargetHost}:{TargetPort}",
            listenPort, targetPgHost, targetPgPort);
    }

    /// <summary>
    /// PostgreSQLプロキシを起動
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning) return;

        _listener.Start();
        _isRunning = true;

        Log.Information("PostgreSqlProxy started, listening on port {Port}",
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
        Log.Information("PostgreSQL client connected: {SessionId}, RemoteEP={RemoteEndPoint}",
            sessionId, client.Client.RemoteEndPoint);

        var session = new PostgreSqlSession(sessionId, client);

        lock (_sessionLock)
        {
            _sessions[sessionId] = session;
        }

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
                while (TryReadPostgreSqlMessage(ref buffer, out var messageType, out var messageData))
                {
                    await ProcessPostgreSqlMessageAsync(sessionId, messageType, messageData, clientStream);
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
    /// PostgreSQLメッセージを処理
    /// </summary>
    private async Task ProcessPostgreSqlMessageAsync(
        string sessionId,
        byte messageType,
        ReadOnlySequence<byte> messageData,
        NetworkStream clientStream)
    {
        try
        {
            byte protocolType = messageType switch
            {
                0x00 => PROTOCOL_PGSQL_STARTUP, // スタートアップメッセージ（タイプバイトなし）
                MSG_QUERY => PROTOCOL_PGSQL_QUERY,
                MSG_PARSE => PROTOCOL_PGSQL_QUERY,
                MSG_BIND => PROTOCOL_PGSQL_QUERY,
                MSG_EXECUTE => PROTOCOL_PGSQL_QUERY,
                MSG_TERMINATE => PROTOCOL_PGSQL_QUERY,
                _ => PROTOCOL_PGSQL_DATA
            };

            // SQLクエリの場合はセキュリティ検閲
            if (messageType == MSG_QUERY || messageType == MSG_PARSE)
            {
                var sqlQuery = ExtractSqlQuery(messageData);

                if (!string.IsNullOrEmpty(sqlQuery))
                {
                    Log.Debug("PostgreSQL Query: {SessionId}, SQL={Sql}", sessionId, sqlQuery);

                    // SQLインジェクション検出
                    if (DetectSqlInjection(sqlQuery))
                    {
                        Log.Warning("Blocked SQL injection attempt: {SessionId}, SQL={Sql}",
                            sessionId, sqlQuery);

                        await SendPostgreSqlError(clientStream,
                            "FATAL", "42000", "Query rejected by security policy");
                        return;
                    }

                    // 危険なSQL操作の検出
                    if (DetectDangerousSql(sqlQuery))
                    {
                        Log.Warning("Blocked dangerous SQL operation: {SessionId}, SQL={Sql}",
                            sessionId, sqlQuery);

                        await SendPostgreSqlError(clientStream,
                            "FATAL", "42000", "Dangerous operation rejected by security policy");
                        return;
                    }
                }
            }

            // Raw Ethernetで送信（既存の暗号化レイヤーを使用）
            var payload = BuildProtocolPayload(protocolType, sessionId, messageType, messageData);
            await _transceiver.SendAsync(payload, _cts.Token);

            Log.Debug("Forwarded PostgreSQL message via Raw Ethernet: SessionId={SessionId}, Type=0x{Type:X2}",
                sessionId, messageType);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing PostgreSQL message: {SessionId}", sessionId);
        }
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
            var sessionId = Encoding.ASCII.GetString(payload, 1, 8);
            var messageType = payload[9];
            var data = payload[10..];

            // PostgreSQLプロトコルの処理
            if (protocolType is PROTOCOL_PGSQL_STARTUP or PROTOCOL_PGSQL_QUERY 
                or PROTOCOL_PGSQL_DATA or PROTOCOL_PGSQL_RESPONSE)
            {
                lock (_sessionLock)
                {
                    if (_sessions.TryGetValue(sessionId, out var session))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var stream = session.Client.GetStream();
                                
                                // PostgreSQLメッセージ形式で返送
                                var message = new byte[1 + 4 + data.Length];
                                message[0] = messageType;
                                BitConverter.GetBytes(IPAddress.HostToNetworkOrder(4 + data.Length))
                                    .CopyTo(message, 1);
                                data.CopyTo(message, 5);

                                await stream.WriteAsync(message, _cts.Token);
                                await stream.FlushAsync(_cts.Token);

                                Log.Debug("Sent PostgreSQL response to client: SessionId={SessionId}, Type=0x{Type:X2}",
                                    sessionId, messageType);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Error sending PostgreSQL response: {SessionId}", sessionId);
                            }
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling Raw Ethernet packet");
        }
    }

    /// <summary>
    /// PostgreSQLメッセージを読み取る
    /// </summary>
    private bool TryReadPostgreSqlMessage(
        ref ReadOnlySequence<byte> buffer,
        out byte messageType,
        out ReadOnlySequence<byte> messageData)
    {
        messageType = 0;
        messageData = default;

        // スタートアップメッセージ（最初の接続時のみ、タイプバイトなし）
        if (buffer.Length >= 4)
        {
            var lengthBytes = buffer.Slice(0, 4).ToArray();
            var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBytes, 0));

            if (length > 0 && length <= buffer.Length)
            {
                // スタートアップメッセージか判定（プロトコルバージョンチェック）
                if (buffer.Length >= 8)
                {
                    var versionBytes = buffer.Slice(4, 4).ToArray();
                    var version = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(versionBytes, 0));

                    if (version == 196608) // PostgreSQL 3.0プロトコル
                    {
                        messageType = 0x00; // スタートアップメッセージ
                        messageData = buffer.Slice(0, length);
                        buffer = buffer.Slice(length);
                        return true;
                    }
                }
            }
        }

        // 通常のメッセージ（タイプ1バイト + 長さ4バイト + データ）
        if (buffer.Length >= 5)
        {
            messageType = buffer.First.Span[0];
            var lengthBytes = buffer.Slice(1, 4).ToArray();
            var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBytes, 0));

            if (length > 0 && 1 + length <= buffer.Length)
            {
                messageData = buffer.Slice(5, length - 4);
                buffer = buffer.Slice(1 + length);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// SQLクエリを抽出
    /// </summary>
    private string ExtractSqlQuery(ReadOnlySequence<byte> messageData)
    {
        try
        {
            var data = messageData.ToArray();
            
            // Queryメッセージの場合、NULL終端文字列
            var nullIndex = Array.IndexOf(data, (byte)0);
            if (nullIndex > 0)
            {
                return Encoding.UTF8.GetString(data, 0, nullIndex);
            }

            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// SQLインジェクション検出
    /// </summary>
    private bool DetectSqlInjection(string sql)
    {
        var patterns = new[]
        {
            "' OR '1'='1",
            "' OR 1=1--",
            "'; DROP TABLE",
            "'; DELETE FROM",
            "UNION SELECT",
            "' UNION ALL SELECT",
            "' AND 1=0 UNION ALL SELECT",
            "'; EXEC",
            "'; EXECUTE",
            "' OR 'a'='a",
            "admin'--",
            "' OR ''='",
            "1' AND '1'='1",
            "' WAITFOR DELAY",
            "'; SHUTDOWN--"
        };

        var upperSql = sql.ToUpperInvariant();

        return patterns.Any(pattern => upperSql.Contains(pattern.ToUpperInvariant()));
    }

    /// <summary>
    /// 危険なSQL操作の検出
    /// </summary>
    private bool DetectDangerousSql(string sql)
    {
        var upperSql = sql.ToUpperInvariant();

        // WHERE句のないDELETE/UPDATE
        if ((upperSql.Contains("DELETE FROM") || upperSql.Contains("UPDATE ")) &&
            !upperSql.Contains("WHERE"))
        {
            return true;
        }

        // DROP/TRUNCATE文
        if (upperSql.Contains("DROP TABLE") || upperSql.Contains("DROP DATABASE") ||
            upperSql.Contains("TRUNCATE TABLE"))
        {
            return true;
        }

        // システムカタログへのアクセス
        if (upperSql.Contains("PG_SHADOW") || upperSql.Contains("PG_AUTHID"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// PostgreSQLエラーメッセージを送信
    /// </summary>
    private async Task SendPostgreSqlError(
        NetworkStream stream,
        string severity,
        string code,
        string message)
    {
        var errorFields = new List<byte>();

        // Severity
        errorFields.Add((byte)'S');
        errorFields.AddRange(Encoding.UTF8.GetBytes(severity));
        errorFields.Add(0);

        // Code
        errorFields.Add((byte)'C');
        errorFields.AddRange(Encoding.UTF8.GetBytes(code));
        errorFields.Add(0);

        // Message
        errorFields.Add((byte)'M');
        errorFields.AddRange(Encoding.UTF8.GetBytes(message));
        errorFields.Add(0);

        // Terminator
        errorFields.Add(0);

        var errorMessage = new byte[1 + 4 + errorFields.Count];
        errorMessage[0] = (byte)'E'; // ErrorResponse
        BitConverter.GetBytes(IPAddress.HostToNetworkOrder(4 + errorFields.Count))
            .CopyTo(errorMessage, 1);
        errorFields.CopyTo(errorMessage, 5);

        await stream.WriteAsync(errorMessage, _cts.Token);
        await stream.FlushAsync(_cts.Token);
    }

    /// <summary>
    /// プロトコルペイロードを構築
    /// </summary>
    private byte[] BuildProtocolPayload(
        byte protocolType,
        string sessionId,
        byte messageType,
        ReadOnlySequence<byte> messageData)
    {
        var data = messageData.ToArray();
        var payload = new byte[1 + 8 + 1 + data.Length];
        
        payload[0] = protocolType;
        Encoding.ASCII.GetBytes(sessionId).CopyTo(payload, 1);
        payload[9] = messageType;
        data.CopyTo(payload, 10);
        
        return payload;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();

        lock (_sessionLock)
        {
            foreach (var session in _sessions.Values)
            {
                session.Client.Close();
            }
            _sessions.Clear();
        }

        _isRunning = false;
        Log.Information("PostgreSqlProxy stopped");
    }

    /// <summary>
    /// PostgreSQLセッション情報
    /// </summary>
    private class PostgreSqlSession
    {
        public string SessionId { get; }
        public TcpClient Client { get; }
        public DateTime ConnectedAt { get; }

        public PostgreSqlSession(string sessionId, TcpClient client)
        {
            SessionId = sessionId;
            Client = client;
            ConnectedAt = DateTime.UtcNow;
        }
    }
}