using NonIpFileDelivery.Core;
using NonIpFileDelivery.Security;
using NonIpFileDelivery.Models;
using Serilog;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NonIpFileDeliveryB.Protocols;

/// <summary>
/// PostgreSQLプロキシB側（受信側）
/// Raw Ethernetから受信 → PostgreSQLサーバへTCP転送
/// </summary>
public class PostgreSqlProxyB : IDisposable
{
    private readonly SecureEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly string _targetPgHost;
    private readonly int _targetPgPort;
    private readonly SessionManagerB _sessionManager;
    private readonly CancellationTokenSource _cts;
    private bool _isRunning;

    private const byte PROTOCOL_PG_STARTUP = 0x30;
    private const byte PROTOCOL_PG_QUERY = 0x31;
    private const byte PROTOCOL_PG_DATA = 0x32;

    public PostgreSqlProxyB(
        SecureEthernetTransceiver transceiver,
        SecurityInspector inspector,
        string targetPgHost = "192.168.2.102",
        int targetPgPort = 5432)
    {
        _transceiver = transceiver ?? throw new ArgumentNullException(nameof(transceiver));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _targetPgHost = targetPgHost;
        _targetPgPort = targetPgPort;
        _sessionManager = new SessionManagerB(sessionTimeoutMinutes: 10);
        _cts = new CancellationTokenSource();

        _transceiver.FrameReceived += OnFrameReceived;

        Log.Information("PostgreSqlProxyB initialized: Target={Host}:{Port}", targetPgHost, targetPgPort);
    }

    public async Task StartAsync()
    {
        if (_isRunning) return;

        _isRunning = true;
        Log.Information("PostgreSqlProxyB started");

        await Task.CompletedTask;
    }

    private void OnFrameReceived(object? sender, SecureFrame frame)
    {
        if (frame.Protocol != SecureFrame.ProtocolType.PostgreSql)
        {
            return;
        }

        // Fire-and-Forgetパターン: 例外を適切にログ記録
        _ = Task.Run(async () =>
        {
            try
            {
                await HandlePostgreSqlFrameAsync(frame);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling PostgreSQL frame: Protocol={Protocol}, SessionId={SessionId}", 
                    frame.Protocol, frame.SessionId);
            }
        });
    }

    private async Task HandlePostgreSqlFrameAsync(SecureFrame frame)
    {
        try
        {
            var payload = frame.Payload;
            if (payload.Length < 10) return;

            var protocolType = payload[0];
            var sessionIdBytes = payload[1..9];
            var sessionId = Encoding.ASCII.GetString(sessionIdBytes);
            var data = payload[9..];

            Log.Debug("Handling PostgreSQL frame: Protocol={Protocol}, SessionId={SessionId}, DataLen={DataLen}",
                protocolType, sessionId, data.Length);

            // SQLインジェクション検出（クエリの場合）
            if (protocolType == PROTOCOL_PG_QUERY)
            {
                var query = Encoding.UTF8.GetString(data);
                if (IsSqlInjection(query))
                {
                    Log.Warning("SQL injection detected: SessionId={SessionId}, Query={Query}",
                        sessionId, query);
                    return;
                }
            }

            // セキュリティ検閲
            if (_inspector.ScanData(data, $"PostgreSQL-{sessionId}"))
            {
                Log.Warning("Blocked malicious PostgreSQL data: SessionId={SessionId}, Size={Size}",
                    sessionId, data.Length);
                return;
            }

            // セッションに対応するTCP接続を取得（なければ作成）
            var client = _sessionManager.GetClientBySession(sessionId);
            if (client == null)
            {
                client = new TcpClient();
                await client.ConnectAsync(_targetPgHost, _targetPgPort, _cts.Token);
                _sessionManager.RegisterSession(sessionId, client);

                Log.Information("New PostgreSQL connection established: SessionId={SessionId}, Server={Host}:{Port}",
                    sessionId, _targetPgHost, _targetPgPort);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await MonitorPostgreSqlServerResponseAsync(sessionId, client);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error monitoring PostgreSQL server response: SessionId={SessionId}", sessionId);
                    }
                });
            }

            // PostgreSQLサーバにデータを転送
            var stream = client.GetStream();
            await stream.WriteAsync(data, _cts.Token);
            await stream.FlushAsync(_cts.Token);

            Log.Debug("PostgreSQL data forwarded to server: SessionId={SessionId}, DataLen={DataLen}",
                sessionId, data.Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling PostgreSQL frame");
        }
    }

    private async Task MonitorPostgreSqlServerResponseAsync(string sessionId, TcpClient client)
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

                Log.Debug("PostgreSQL response from server: SessionId={SessionId}, DataLen={DataLen}",
                    sessionId, bytesRead);

                // 機密データ検出（ダウンロード方向）
                if (ContainsSensitiveData(responseData))
                {
                    Log.Warning("Sensitive data detected in PostgreSQL response: SessionId={SessionId}",
                        sessionId);
                    // 実際には、データをマスキングするか、アラートを発行する
                }

                // セキュリティ検閲
                if (_inspector.ScanData(responseData, $"PostgreSQL-RESPONSE-{sessionId}"))
                {
                    Log.Warning("Blocked malicious PostgreSQL response: SessionId={SessionId}, Size={Size}",
                        sessionId, bytesRead);
                    _sessionManager.RemoveSession(sessionId);
                    return;
                }

                var payload = BuildProtocolPayload(PROTOCOL_PG_DATA, sessionId, responseData);

                var frame = new SecureFrame
                {
                    SessionId = Guid.Parse(sessionId.PadRight(36, '0')),
                    Protocol = SecureFrame.ProtocolType.PostgreSql,
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
            Log.Error(ex, "Error monitoring PostgreSQL server response: SessionId={SessionId}", sessionId);
        }
        finally
        {
            _sessionManager.RemoveSession(sessionId);
        }
    }

    private bool IsSqlInjection(string query)
    {
        // 簡易的なSQLインジェクション検出
        var lowerQuery = query.ToLowerInvariant();
        var dangerousPatterns = new[]
        {
            "drop table", "delete from", "truncate", "exec(", "execute(",
            "union select", "--", "/*", "xp_", "sp_",
            "'; drop", "\"; drop", "1=1", "1' or '1'='1"
        };

        return dangerousPatterns.Any(pattern => lowerQuery.Contains(pattern));
    }

    private bool ContainsSensitiveData(byte[] data)
    {
        // 簡易的な機密データ検出
        var text = Encoding.UTF8.GetString(data).ToLowerInvariant();
        var sensitivePatterns = new[]
        {
            "password", "secret", "token", "credit_card", "ssn",
            "api_key", "private_key", "confidential"
        };

        return sensitivePatterns.Any(pattern => text.Contains(pattern));
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

        Log.Information("PostgreSqlProxyB stopped");
    }
}
