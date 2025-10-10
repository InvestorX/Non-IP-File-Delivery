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
        if (string.IsNullOrEmpty(sessionId) || client == null)
        {
            throw new ArgumentNullException(sessionId == null ? nameof(sessionId) : nameof(client));
        }

        lock (_lock)
        {
            _sessionToClient[sessionId] = client;
            _clientToSession[client] = sessionId;
            _sessionLastActivity[sessionId] = DateTime.UtcNow;
        }

        Log.Debug("Session registered: SessionId={SessionId}, RemoteEndPoint={RemoteEndPoint}",
            sessionId, client.Client.RemoteEndPoint);
    }

    /// <summary>
    /// セッションIDからTCP接続を取得
    /// </summary>
    /// <param name="sessionId">セッションID</param>
    /// <returns>対応するTcpClient、存在しない場合はnull</returns>
    public TcpClient? GetClientBySession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        if (_sessionToClient.TryGetValue(sessionId, out var client))
        {
            // 最終アクティビティ時刻を更新
            _sessionLastActivity[sessionId] = DateTime.UtcNow;
            return client;
        }

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
            return null;
        }

        if (_clientToSession.TryGetValue(client, out var sessionId))
        {
            // 最終アクティビティ時刻を更新
            _sessionLastActivity[sessionId] = DateTime.UtcNow;
            return sessionId;
        }

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
            return;
        }

        lock (_lock)
        {
            if (_sessionToClient.TryRemove(sessionId, out var client))
            {
                _clientToSession.TryRemove(client, out _);
                _sessionLastActivity.TryRemove(sessionId, out _);

                // TCP接続をクローズ
                try
                {
                    client?.Close();
                    client?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error closing TCP client for session {SessionId}", sessionId);
                }

                Log.Debug("Session removed: SessionId={SessionId}", sessionId);
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
            return;
        }

        var sessionId = GetSessionByClient(client);
        if (sessionId != null)
        {
            RemoveSession(sessionId);
        }
    }

    /// <summary>
    /// タイムアウトしたセッションをクリーンアップ
    /// </summary>
    public void CleanupExpiredSessions()
    {
        var now = DateTime.UtcNow;
        var expiredSessions = new List<string>();

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
            Log.Information("Session expired: SessionId={SessionId}, LastActivity={LastActivity}",
                sessionId, _sessionLastActivity[sessionId]);
            RemoveSession(sessionId);
        }

        if (expiredSessions.Count > 0)
        {
            Log.Debug("Cleaned up {Count} expired sessions", expiredSessions.Count);
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
