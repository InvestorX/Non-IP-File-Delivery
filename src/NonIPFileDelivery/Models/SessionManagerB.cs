using System.Collections.Concurrent;
using System.Net.Sockets;
using Serilog;

namespace NonIpFileDelivery.Models;

/// <summary>
/// B側用セッション管理クラス
/// Raw Ethernetのセッション⇔TCP接続のマッピングを管理
/// </summary>
public class SessionManagerB : IDisposable
{
    // セッションID → TCP接続のマッピング
    private readonly ConcurrentDictionary<string, TcpClient> _sessionToClient = new();

    // TCP接続 → セッションIDの逆引き
    private readonly ConcurrentDictionary<TcpClient, string> _clientToSession = new();

    // セッションの最終アクティビティ時刻
    private readonly ConcurrentDictionary<string, DateTime> _sessionLastActivity = new();

    // セッションタイムアウト（デフォルト: 5分）
    private readonly TimeSpan _sessionTimeout;

    // クリーンアップタイマー
    private readonly Timer _cleanupTimer;

    private readonly object _lock = new();

    /// <summary>
    /// SessionManagerBを初期化
    /// </summary>
    /// <param name="sessionTimeoutMinutes">セッションタイムアウト（分）</param>
    /// <param name="cleanupIntervalSeconds">クリーンアップ間隔（秒）</param>
    public SessionManagerB(int sessionTimeoutMinutes = 5, int cleanupIntervalSeconds = 60)
    {
        _sessionTimeout = TimeSpan.FromMinutes(sessionTimeoutMinutes);

        // 定期的にタイムアウトしたセッションをクリーンアップ
        _cleanupTimer = new Timer(
            _ => CleanupExpiredSessions(),
            null,
            TimeSpan.FromSeconds(cleanupIntervalSeconds),
            TimeSpan.FromSeconds(cleanupIntervalSeconds)
        );

        Log.Debug("SessionManagerB initialized: Timeout={TimeoutMinutes}min, CleanupInterval={CleanupSeconds}s",
            sessionTimeoutMinutes, cleanupIntervalSeconds);
    }

    /// <summary>
    /// セッションを登録
    /// </summary>
    /// <param name="sessionId">セッションID（8文字の英数字）</param>
    /// <param name="client">TCP接続</param>
    public void RegisterSession(string sessionId, TcpClient client)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            Log.Error("Cannot register session: SessionId is null or empty");
            throw new ArgumentNullException(nameof(sessionId), "SessionId cannot be null or empty");
        }

        if (client == null)
        {
            Log.Error("Cannot register session {SessionId}: TcpClient is null", sessionId);
            throw new ArgumentNullException(nameof(client), "TcpClient cannot be null");
        }

        if (!client.Connected)
        {
            Log.Warning("Registering session {SessionId} with disconnected TcpClient", sessionId);
        }

        lock (_lock)
        {
            // 既存セッションの上書き警告
            if (_sessionToClient.ContainsKey(sessionId))
            {
                Log.Warning("Overwriting existing session: SessionId={SessionId}", sessionId);
                // 古い接続をクリーンアップ
                if (_sessionToClient.TryRemove(sessionId, out var oldClient))
                {
                    _clientToSession.TryRemove(oldClient, out _);
                    try
                    {
                        oldClient?.Close();
                        oldClient?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Error closing old TCP client for session {SessionId}", sessionId);
                    }
                }
            }

            _sessionToClient[sessionId] = client;
            _clientToSession[client] = sessionId;
            _sessionLastActivity[sessionId] = DateTime.UtcNow;
        }

        Log.Information("Session registered: SessionId={SessionId}, RemoteEndPoint={RemoteEndPoint}, TotalSessions={TotalSessions}",
            sessionId, client.Client.RemoteEndPoint, _sessionToClient.Count);
    }

    /// <summary>
    /// セッションIDから対応するTCP接続を取得
    /// </summary>
    /// <param name="sessionId">セッションID</param>
    /// <returns>対応するTcpClient、存在しない場合はnull</returns>
    public TcpClient? GetClientBySession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            Log.Debug("GetClientBySession called with null or empty SessionId");
            return null;
        }

        if (_sessionToClient.TryGetValue(sessionId, out var client))
        {
            // 最終アクティビティ時刻を更新
            _sessionLastActivity[sessionId] = DateTime.UtcNow;
            var remoteEndPoint = client.Client?.RemoteEndPoint?.ToString() ?? "Unknown";
            Log.Debug("Retrieved client for session: SessionId={SessionId}, RemoteEndPoint={RemoteEndPoint}, Connected={Connected}",
                sessionId, remoteEndPoint, client.Connected);
            return client;
        }

        Log.Debug("Session not found: SessionId={SessionId}", sessionId);
        return null;
    }

    /// <summary>
    /// TCP接続からセッションIDを取得
    /// </summary>
    /// <param name="client">TcpClient</param>
    /// <returns>対応するセッションID、存在しない場合はnull</returns>
    public string? GetSessionByClient(TcpClient client)
    {
        if (client == null)
        {
            Log.Debug("GetSessionByClient called with null client");
            return null;
        }

        if (_clientToSession.TryGetValue(client, out var sessionId))
        {
            // 最終アクティビティ時刻を更新
            _sessionLastActivity[sessionId] = DateTime.UtcNow;
            var remoteEndPoint = client.Client?.RemoteEndPoint?.ToString() ?? "Unknown";
            Log.Debug("Retrieved session for client: SessionId={SessionId}, RemoteEndPoint={RemoteEndPoint}, Connected={Connected}",
                sessionId, remoteEndPoint, client.Connected);
            return sessionId;
        }

        var clientEndPoint = client.Client?.RemoteEndPoint?.ToString() ?? "Unknown";
        Log.Debug("Client not associated with any session: RemoteEndPoint={RemoteEndPoint}", clientEndPoint);
        return null;
    }

    /// <summary>
    /// セッションを削除
    /// </summary>
    /// <param name="sessionId">セッションID</param>
    public void RemoveSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            Log.Debug("RemoveSession called with null or empty SessionId");
            return;
        }

        lock (_lock)
        {
            if (_sessionToClient.TryRemove(sessionId, out var client))
            {
                _clientToSession.TryRemove(client, out _);
                _sessionLastActivity.TryRemove(sessionId, out var lastActivity);

                // TCP接続をクローズ
                try
                {
                    if (client != null)
                    {
                        if (client.Connected)
                        {
                            client.Close();
                        }
                        client.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error closing TCP client for session {SessionId}", sessionId);
                }

                Log.Information("Session removed: SessionId={SessionId}, LastActivity={LastActivity}, RemainingActiveSessions={RemainingActiveSessions}",
                    sessionId, lastActivity, _sessionToClient.Count);
            }
            else
            {
                Log.Debug("Attempted to remove non-existent session: SessionId={SessionId}", sessionId);
            }
        }
    }

    /// <summary>
    /// TCP接続からセッションを削除
    /// </summary>
    /// <param name="client">TcpClient</param>
    public void RemoveSessionByClient(TcpClient client)
    {
        if (client == null)
        {
            Log.Debug("RemoveSessionByClient called with null client");
            return;
        }

        var sessionId = GetSessionByClient(client);
        if (sessionId != null)
        {
            var remoteEndPoint = client.Client?.RemoteEndPoint?.ToString() ?? "Unknown";
            Log.Debug("Removing session by client: SessionId={SessionId}, RemoteEndPoint={RemoteEndPoint}",
                sessionId, remoteEndPoint);
            RemoveSession(sessionId);
        }
        else
        {
            var remoteEndPoint = client.Client?.RemoteEndPoint?.ToString() ?? "Unknown";
            Log.Debug("No session found for client: RemoteEndPoint={RemoteEndPoint}", remoteEndPoint);
        }
    }

    /// <summary>
    /// タイムアウトしたセッションをクリーンアップ
    /// </summary>
    public void CleanupExpiredSessions()
    {
        var now = DateTime.UtcNow;
        var expiredSessions = new List<string>();

        try
        {
            // タイムアウトしたセッションを特定
            foreach (var kvp in _sessionLastActivity)
            {
                if (now - kvp.Value > _sessionTimeout)
                {
                    expiredSessions.Add(kvp.Key);
                }
            }

            // 削除
            foreach (var sessionId in expiredSessions)
            {
                var lastActivity = _sessionLastActivity.TryGetValue(sessionId, out var la) ? la : DateTime.MinValue;
                Log.Information("Session expired due to timeout: SessionId={SessionId}, LastActivity={LastActivity}, IdleTime={IdleTime}",
                    sessionId, lastActivity, now - lastActivity);
                RemoveSession(sessionId);
            }

            if (expiredSessions.Count > 0)
            {
                Log.Information("Cleanup completed: {Count} expired sessions removed, {ActiveCount} active sessions remaining",
                    expiredSessions.Count, _sessionToClient.Count);
            }
            else
            {
                Log.Debug("Cleanup completed: No expired sessions found, {ActiveCount} active sessions",
                    _sessionToClient.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during session cleanup");
        }
    }

    /// <summary>
    /// 現在のセッション数を取得
    /// </summary>
    public int ActiveSessionCount => _sessionToClient.Count;

    /// <summary>
    /// すべてのアクティブなセッションIDを取得
    /// </summary>
    public IEnumerable<string> GetActiveSessionIds()
    {
        return _sessionToClient.Keys.ToList();
    }

    /// <summary>
    /// セッションの最終アクティビティ時刻を取得
    /// </summary>
    public DateTime? GetLastActivity(string sessionId)
    {
        if (_sessionLastActivity.TryGetValue(sessionId, out var lastActivity))
        {
            return lastActivity;
        }
        return null;
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();

        // すべてのセッションをクローズ
        lock (_lock)
        {
            foreach (var client in _sessionToClient.Values)
            {
                try
                {
                    client?.Close();
                    client?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error disposing TCP client");
                }
            }

            _sessionToClient.Clear();
            _clientToSession.Clear();
            _sessionLastActivity.Clear();
        }

        Log.Debug("SessionManagerB disposed");
    }
}
