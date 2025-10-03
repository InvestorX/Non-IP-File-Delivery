using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// セッション管理サービスのインターフェース
/// Phase 3: セッション管理機能
/// </summary>
public interface ISessionManager
{
    /// <summary>
    /// 新しいセッションを作成します
    /// </summary>
    /// <param name="sourceMAC">送信元MACアドレス</param>
    /// <param name="destinationMAC">宛先MACアドレス</param>
    /// <param name="timeoutSeconds">セッションタイムアウト時間（秒）</param>
    /// <returns>作成されたセッション情報</returns>
    Task<SessionInfo> CreateSessionAsync(byte[] sourceMAC, byte[] destinationMAC, int timeoutSeconds = 300);
    
    /// <summary>
    /// SessionIdでセッションを取得します
    /// </summary>
    /// <param name="sessionId">セッションID</param>
    /// <returns>セッション情報（存在しない場合null）</returns>
    Task<SessionInfo?> GetSessionAsync(Guid sessionId);
    
    /// <summary>
    /// ConnectionIdでセッションを取得します
    /// </summary>
    /// <param name="connectionId">接続ID</param>
    /// <returns>セッション情報（存在しない場合null）</returns>
    Task<SessionInfo?> GetSessionByConnectionIdAsync(ulong connectionId);
    
    /// <summary>
    /// セッションの最終アクティブ時刻を更新します
    /// </summary>
    /// <param name="sessionId">セッションID</param>
    Task UpdateSessionActivityAsync(Guid sessionId);
    
    /// <summary>
    /// セッションを閉じます
    /// </summary>
    /// <param name="sessionId">セッションID</param>
    /// <param name="reason">終了理由</param>
    Task CloseSessionAsync(Guid sessionId, string reason = "Normal closure");
    
    /// <summary>
    /// タイムアウトしたセッションをクリーンアップします
    /// </summary>
    /// <returns>クリーンアップされたセッション数</returns>
    Task<int> CleanupTimedOutSessionsAsync();
    
    /// <summary>
    /// 全てのアクティブなセッション一覧を取得します
    /// </summary>
    /// <returns>アクティブなセッション情報のリスト</returns>
    Task<IEnumerable<SessionInfo>> GetActiveSessionsAsync();
    
    /// <summary>
    /// セッション統計情報を取得します
    /// </summary>
    /// <returns>統計情報の辞書</returns>
    Task<Dictionary<string, object>> GetSessionStatisticsAsync();
    
    /// <summary>
    /// 特定のMACアドレスペアのセッションを検索します
    /// </summary>
    /// <param name="sourceMAC">送信元MACアドレス</param>
    /// <param name="destinationMAC">宛先MACアドレス</param>
    /// <returns>該当するセッション情報（存在しない場合null）</returns>
    Task<SessionInfo?> FindSessionByMACPairAsync(byte[] sourceMAC, byte[] destinationMAC);
}
