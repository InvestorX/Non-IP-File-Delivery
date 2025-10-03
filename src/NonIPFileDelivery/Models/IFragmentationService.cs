using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// フラグメント処理サービスのインターフェース
/// Phase 3: 大きなペイロードの分割・再構築機能
/// </summary>
public interface IFragmentationService
{
    /// <summary>
    /// 大きなペイロードをフラグメントに分割します
    /// </summary>
    /// <param name="payload">元のペイロード</param>
    /// <param name="maxFragmentSize">1フラグメントの最大サイズ（バイト）</param>
    /// <returns>フラグメントリスト</returns>
    Task<List<NonIPFrame>> FragmentPayloadAsync(byte[] payload, int maxFragmentSize = 8000);
    
    /// <summary>
    /// フラグメントを追加し、全て揃ったら元のペイロードを再構築します
    /// </summary>
    /// <param name="frame">フラグメントフレーム</param>
    /// <returns>再構築結果（完了していない場合null）</returns>
    Task<ReassemblyResult?> AddFragmentAsync(NonIPFrame frame);
    
    /// <summary>
    /// 指定されたFragmentGroupIdの受信進捗を取得します
    /// </summary>
    /// <param name="fragmentGroupId">フラグメントグループID</param>
    /// <returns>進捗率（0.0 - 1.0）、存在しない場合null</returns>
    Task<double?> GetFragmentProgressAsync(Guid fragmentGroupId);
    
    /// <summary>
    /// タイムアウトしたフラグメントグループをクリーンアップします
    /// </summary>
    /// <returns>クリーンアップされたグループ数</returns>
    Task<int> CleanupTimedOutFragmentsAsync();
    
    /// <summary>
    /// フラグメント統計情報を取得します
    /// </summary>
    /// <returns>統計情報の辞書</returns>
    Task<Dictionary<string, object>> GetFragmentStatisticsAsync();
    
    /// <summary>
    /// 指定されたFragmentGroupIdのフラグメントグループを削除します
    /// </summary>
    /// <param name="fragmentGroupId">フラグメントグループID</param>
    Task RemoveFragmentGroupAsync(Guid fragmentGroupId);
}

/// <summary>
/// フラグメント再構築結果
/// </summary>
public class ReassemblyResult
{
    /// <summary>
    /// フラグメントグループID
    /// </summary>
    public Guid FragmentGroupId { get; set; }
    
    /// <summary>
    /// 再構築された元のペイロード
    /// </summary>
    public byte[] ReassembledPayload { get; set; } = Array.Empty<byte>();
    
    /// <summary>
    /// 再構築が成功したか
    /// </summary>
    public bool IsSuccess { get; set; }
    
    /// <summary>
    /// ハッシュ検証が成功したか
    /// </summary>
    public bool IsHashValid { get; set; }
    
    /// <summary>
    /// 受信したフラグメント数
    /// </summary>
    public int ReceivedFragmentCount { get; set; }
    
    /// <summary>
    /// 総フラグメント数
    /// </summary>
    public int TotalFragmentCount { get; set; }
    
    /// <summary>
    /// 最初のフラグメント受信から再構築完了までの時間（ミリ秒）
    /// </summary>
    public long ReassemblyTimeMs { get; set; }
    
    /// <summary>
    /// エラーメッセージ（失敗時）
    /// </summary>
    public string? ErrorMessage { get; set; }
}
