using System;
using System.Threading.Tasks;

namespace NonIPFileDelivery.Services;

/// <summary>
/// ファイルチャンクの保存・組み立て・検証を行うサービス
/// </summary>
public interface IFileStorageService
{
    /// <summary>
    /// ファイルチャンクを一時保存
    /// </summary>
    /// <param name="sessionId">セッションID</param>
    /// <param name="fileName">ファイル名</param>
    /// <param name="chunkIndex">チャンクインデックス</param>
    /// <param name="totalChunks">総チャンク数</param>
    /// <param name="chunkData">チャンクデータ</param>
    /// <returns>保存成功時はtrue</returns>
    Task<bool> SaveChunkAsync(Guid sessionId, string fileName, int chunkIndex, int totalChunks, byte[] chunkData);

    /// <summary>
    /// 全てのチャンクが揃っているかチェック
    /// </summary>
    /// <param name="sessionId">セッションID</param>
    /// <param name="fileName">ファイル名</param>
    /// <param name="totalChunks">総チャンク数</param>
    /// <returns>全て揃っている場合はtrue</returns>
    Task<bool> AreAllChunksReceivedAsync(Guid sessionId, string fileName, int totalChunks);

    /// <summary>
    /// チャンクを組み立てて最終ファイルを生成
    /// </summary>
    /// <param name="sessionId">セッションID</param>
    /// <param name="fileName">ファイル名</param>
    /// <param name="totalChunks">総チャンク数</param>
    /// <param name="destinationPath">最終ファイルの保存先パス</param>
    /// <returns>組み立てられたファイルのパス</returns>
    Task<string> AssembleFileAsync(Guid sessionId, string fileName, int totalChunks, string destinationPath);

    /// <summary>
    /// ファイルハッシュを検証
    /// </summary>
    /// <param name="filePath">ファイルパス</param>
    /// <param name="expectedHash">期待されるハッシュ値</param>
    /// <returns>ハッシュが一致する場合はtrue</returns>
    Task<bool> ValidateFileHashAsync(string filePath, string expectedHash);

    /// <summary>
    /// セッションの一時ファイルをクリーンアップ
    /// </summary>
    /// <param name="sessionId">セッションID</param>
    Task CleanupSessionAsync(Guid sessionId);

    /// <summary>
    /// 古い一時ファイルをクリーンアップ（指定時間以上経過したもの）
    /// </summary>
    /// <param name="olderThan">この期間以上古いファイルを削除</param>
    Task CleanupOldFilesAsync(TimeSpan olderThan);
}
