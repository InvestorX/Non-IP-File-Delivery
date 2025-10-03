using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// セッション管理サービス実装
/// Phase 3: セッション管理機能
/// </summary>
public class SessionManager : ISessionManager
{
    private readonly ILoggingService _logger;
    private readonly ConcurrentDictionary<Guid, SessionInfo> _sessions;
    private readonly ConcurrentDictionary<ulong, Guid> _connectionIdToSessionId;
    private ulong _nextConnectionId = 1;
    private readonly object _connectionIdLock = new();

    public SessionManager(ILoggingService logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessions = new ConcurrentDictionary<Guid, SessionInfo>();
        _connectionIdToSessionId = new ConcurrentDictionary<ulong, Guid>();
    }

    /// <summary>
    /// 新しいセッションを作成します
    /// </summary>
    public Task<SessionInfo> CreateSessionAsync(byte[] sourceMAC, byte[] destinationMAC, int timeoutSeconds = 300)
    {
        if (sourceMAC == null || sourceMAC.Length != 6)
            throw new ArgumentException("Invalid source MAC address", nameof(sourceMAC));
        
        if (destinationMAC == null || destinationMAC.Length != 6)
            throw new ArgumentException("Invalid destination MAC address", nameof(destinationMAC));

        var sessionId = Guid.NewGuid();
        var connectionId = GetNextConnectionId();
        
        var session = new SessionInfo
        {
            SessionId = sessionId,
            ConnectionId = connectionId,
            SourceMAC = sourceMAC,
            DestinationMAC = destinationMAC,
            State = SessionState.Establishing,
            StartTime = DateTime.UtcNow,
            LastActiveTime = DateTime.UtcNow,
            TimeoutSeconds = timeoutSeconds
        };

        if (_sessions.TryAdd(sessionId, session))
        {
            _connectionIdToSessionId.TryAdd(connectionId, sessionId);
            _logger.Info($"Session created: SessionId={sessionId}, ConnectionId={connectionId}, " +
                        $"SourceMAC={BitConverter.ToString(sourceMAC)}, " +
                        $"DestinationMAC={BitConverter.ToString(destinationMAC)}");
            
            return Task.FromResult(session);
        }

        throw new InvalidOperationException("Failed to create session");
    }

    /// <summary>
    /// SessionIdでセッションを取得します
    /// </summary>
    public Task<SessionInfo?> GetSessionAsync(Guid sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    /// <summary>
    /// ConnectionIdでセッションを取得します
    /// </summary>
    public Task<SessionInfo?> GetSessionByConnectionIdAsync(ulong connectionId)
    {
        if (_connectionIdToSessionId.TryGetValue(connectionId, out var sessionId))
        {
            return GetSessionAsync(sessionId);
        }

        return Task.FromResult<SessionInfo?>(null);
    }

    /// <summary>
    /// セッションの最終アクティブ時刻を更新します
    /// </summary>
    public Task UpdateSessionActivityAsync(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.LastActiveTime = DateTime.UtcNow;
            
            // 状態がEstablishingの場合、Activeに遷移
            if (session.State == SessionState.Establishing)
            {
                session.State = SessionState.Active;
                _logger.Debug($"Session state changed: SessionId={sessionId}, State=Active");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// セッションを閉じます
    /// </summary>
    public Task CloseSessionAsync(Guid sessionId, string reason = "Normal closure")
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.State = SessionState.Closed;
            session.LastActiveTime = DateTime.UtcNow;
            
            _logger.Info($"Session closed: SessionId={sessionId}, Reason={reason}, " +
                        $"PacketsSent={session.PacketsSent}, PacketsReceived={session.PacketsReceived}, " +
                        $"BytesSent={session.BytesSent}, BytesReceived={session.BytesReceived}");
            
            // 統計情報をログに記録
            var duration = (session.LastActiveTime - session.StartTime).TotalSeconds;
            _logger.LogWithProperties(
                LogLevel.Info,
                "Session statistics",
                ("SessionId", sessionId),
                ("Duration", $"{duration:F2}s"),
                ("PacketsSent", session.PacketsSent),
                ("PacketsReceived", session.PacketsReceived),
                ("BytesSent", session.BytesSent),
                ("BytesReceived", session.BytesReceived));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// タイムアウトしたセッションをクリーンアップします
    /// </summary>
    public Task<int> CleanupTimedOutSessionsAsync()
    {
        var timedOutSessions = _sessions.Values
            .Where(s => s.IsTimedOut() && s.State != SessionState.Closed)
            .ToList();

        int cleanedCount = 0;

        foreach (var session in timedOutSessions)
        {
            session.State = SessionState.TimedOut;
            
            _logger.Warning($"Session timed out: SessionId={session.SessionId}, " +
                           $"LastActive={session.LastActiveTime:yyyy-MM-dd HH:mm:ss}");
            
            // セッションを削除
            if (_sessions.TryRemove(session.SessionId, out _))
            {
                _connectionIdToSessionId.TryRemove(session.ConnectionId, out _);
                cleanedCount++;
            }
        }

        if (cleanedCount > 0)
        {
            _logger.Info($"Cleaned up {cleanedCount} timed-out sessions");
        }

        return Task.FromResult(cleanedCount);
    }

    /// <summary>
    /// 全てのアクティブなセッション一覧を取得します
    /// </summary>
    public Task<IEnumerable<SessionInfo>> GetActiveSessionsAsync()
    {
        var activeSessions = _sessions.Values
            .Where(s => s.IsActive())
            .ToList();

        return Task.FromResult<IEnumerable<SessionInfo>>(activeSessions);
    }

    /// <summary>
    /// セッション統計情報を取得します
    /// </summary>
    public Task<Dictionary<string, object>> GetSessionStatisticsAsync()
    {
        var totalSessions = _sessions.Count;
        var activeSessions = _sessions.Values.Count(s => s.IsActive());
        var establishingSessions = _sessions.Values.Count(s => s.State == SessionState.Establishing);
        var closingSessions = _sessions.Values.Count(s => s.State == SessionState.Closing);
        var closedSessions = _sessions.Values.Count(s => s.State == SessionState.Closed);
        
        var totalPacketsSent = _sessions.Values.Sum(s => s.PacketsSent);
        var totalPacketsReceived = _sessions.Values.Sum(s => s.PacketsReceived);
        var totalBytesSent = _sessions.Values.Sum(s => s.BytesSent);
        var totalBytesReceived = _sessions.Values.Sum(s => s.BytesReceived);

        var stats = new Dictionary<string, object>
        {
            { "TotalSessions", totalSessions },
            { "ActiveSessions", activeSessions },
            { "EstablishingSessions", establishingSessions },
            { "ClosingSessions", closingSessions },
            { "ClosedSessions", closedSessions },
            { "TotalPacketsSent", totalPacketsSent },
            { "TotalPacketsReceived", totalPacketsReceived },
            { "TotalBytesSent", totalBytesSent },
            { "TotalBytesReceived", totalBytesReceived }
        };

        return Task.FromResult(stats);
    }

    /// <summary>
    /// 特定のMACアドレスペアのセッションを検索します
    /// </summary>
    public Task<SessionInfo?> FindSessionByMACPairAsync(byte[] sourceMAC, byte[] destinationMAC)
    {
        var session = _sessions.Values.FirstOrDefault(s =>
            s.SourceMAC.SequenceEqual(sourceMAC) &&
            s.DestinationMAC.SequenceEqual(destinationMAC) &&
            s.IsActive());

        return Task.FromResult(session);
    }

    /// <summary>
    /// 次のConnectionIDを生成します（スレッドセーフ）
    /// </summary>
    private ulong GetNextConnectionId()
    {
        lock (_connectionIdLock)
        {
            return _nextConnectionId++;
        }
    }
}
