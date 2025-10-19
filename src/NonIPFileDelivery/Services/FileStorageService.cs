using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace NonIPFileDelivery.Services;

/// <summary>
/// ファイルチャンクの保存・組み立て・検証サービス
/// </summary>
public class FileStorageService : IFileStorageService
{
    private readonly ILoggingService _logger;
    private readonly string _tempDirectory;
    private readonly ConcurrentDictionary<string, ChunkTracker> _chunkTrackers;

    public FileStorageService(ILoggingService logger, string? tempDirectory = null)
    {
        _logger = logger;
        _tempDirectory = tempDirectory ?? Path.Combine(Path.GetTempPath(), "NonIPFileDelivery", "chunks");
        _chunkTrackers = new ConcurrentDictionary<string, ChunkTracker>();

        // 一時ディレクトリを作成
        if (!Directory.Exists(_tempDirectory))
        {
            Directory.CreateDirectory(_tempDirectory);
            _logger.Info($"Created temp directory: {_tempDirectory}");
        }
    }

    /// <summary>
    /// ファイルチャンクを一時保存
    /// </summary>
    public async Task<bool> SaveChunkAsync(Guid sessionId, string fileName, int chunkIndex, int totalChunks, byte[] chunkData)
    {
        try
        {
            if (chunkData == null || chunkData.Length == 0)
            {
                _logger.Warning($"Empty chunk data: SessionId={sessionId}, File={fileName}, ChunkIndex={chunkIndex}");
                return false;
            }

            // セッション用のディレクトリを作成
            var sessionDir = Path.Combine(_tempDirectory, sessionId.ToString());
            if (!Directory.Exists(sessionDir))
            {
                Directory.CreateDirectory(sessionDir);
            }

            // ファイル名をサニタイズ
            var safeFileName = SanitizeFileName(fileName);
            var chunkFilePath = Path.Combine(sessionDir, $"{safeFileName}.chunk_{chunkIndex:D6}");

            // チャンクをファイルに保存
            await File.WriteAllBytesAsync(chunkFilePath, chunkData);

            // チャンク追跡情報を更新
            var trackerKey = $"{sessionId}_{safeFileName}";
            var tracker = _chunkTrackers.GetOrAdd(trackerKey, _ => new ChunkTracker
            {
                SessionId = sessionId,
                FileName = safeFileName,
                TotalChunks = totalChunks,
                ReceivedChunks = new ConcurrentDictionary<int, bool>(),
                LastUpdateTime = DateTime.UtcNow
            });

            tracker.ReceivedChunks[chunkIndex] = true;
            tracker.LastUpdateTime = DateTime.UtcNow;

            _logger.Debug($"Chunk saved: SessionId={sessionId}, File={fileName}, Chunk={chunkIndex}/{totalChunks}, Size={chunkData.Length} bytes");

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to save chunk: SessionId={sessionId}, File={fileName}, ChunkIndex={chunkIndex}", ex);
            return false;
        }
    }

    /// <summary>
    /// 全てのチャンクが揃っているかチェック
    /// </summary>
    public Task<bool> AreAllChunksReceivedAsync(Guid sessionId, string fileName, int totalChunks)
    {
        try
        {
            var safeFileName = SanitizeFileName(fileName);
            var trackerKey = $"{sessionId}_{safeFileName}";

            if (!_chunkTrackers.TryGetValue(trackerKey, out var tracker))
            {
                _logger.Debug($"No chunks received yet: SessionId={sessionId}, File={fileName}");
                return Task.FromResult(false);
            }

            // 0からtotalChunks-1まで全て受信済みかチェック
            for (int i = 0; i < totalChunks; i++)
            {
                if (!tracker.ReceivedChunks.ContainsKey(i))
                {
                    _logger.Debug($"Missing chunk {i}/{totalChunks}: SessionId={sessionId}, File={fileName}");
                    return Task.FromResult(false);
                }
            }

            _logger.Info($"All chunks received: SessionId={sessionId}, File={fileName}, TotalChunks={totalChunks}");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error($"Error checking chunks: SessionId={sessionId}, File={fileName}", ex);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// チャンクを組み立てて最終ファイルを生成
    /// </summary>
    public async Task<string> AssembleFileAsync(Guid sessionId, string fileName, int totalChunks, string destinationPath)
    {
        try
        {
            var safeFileName = SanitizeFileName(fileName);
            var sessionDir = Path.Combine(_tempDirectory, sessionId.ToString());

            if (!Directory.Exists(sessionDir))
            {
                throw new DirectoryNotFoundException($"Session directory not found: {sessionDir}");
            }

            // 最終ファイルのディレクトリを作成
            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            _logger.Info($"Assembling file: SessionId={sessionId}, File={fileName}, TotalChunks={totalChunks}");

            // チャンクを順番に読み込んで結合
            using (var outputStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
            {
                for (int i = 0; i < totalChunks; i++)
                {
                    var chunkFilePath = Path.Combine(sessionDir, $"{safeFileName}.chunk_{i:D6}");

                    if (!File.Exists(chunkFilePath))
                    {
                        throw new FileNotFoundException($"Chunk file not found: {chunkFilePath}");
                    }

                    var chunkData = await File.ReadAllBytesAsync(chunkFilePath);
                    await outputStream.WriteAsync(chunkData, 0, chunkData.Length);

                    _logger.Debug($"Chunk {i}/{totalChunks} written to output file, size: {chunkData.Length} bytes");
                }
            }

            var fileInfo = new FileInfo(destinationPath);
            _logger.Info($"File assembled successfully: {destinationPath}, Size: {fileInfo.Length} bytes");

            // チャンク追跡情報をクリーンアップ
            var trackerKey = $"{sessionId}_{safeFileName}";
            _chunkTrackers.TryRemove(trackerKey, out _);

            return destinationPath;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to assemble file: SessionId={sessionId}, File={fileName}", ex);
            throw;
        }
    }

    /// <summary>
    /// ファイルハッシュを検証
    /// </summary>
    public async Task<bool> ValidateFileHashAsync(string filePath, string expectedHash)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.Warning($"File not found for hash validation: {filePath}");
                return false;
            }

            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                _logger.Warning($"No expected hash provided for: {filePath}");
                return true; // ハッシュが提供されていない場合は検証スキップ
            }

            _logger.Debug($"Validating file hash: {filePath}");

            string actualHash;
            using (var sha256 = SHA256.Create())
            {
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
                var hashBytes = await Task.Run(() => sha256.ComputeHash(fileStream));
                actualHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }

            var normalizedExpectedHash = expectedHash.Replace("-", "").ToLowerInvariant();
            var isValid = actualHash.Equals(normalizedExpectedHash, StringComparison.OrdinalIgnoreCase);

            if (isValid)
            {
                _logger.Info($"File hash validation passed: {filePath}");
            }
            else
            {
                _logger.Warning($"File hash validation failed: {filePath}, Expected={normalizedExpectedHash}, Actual={actualHash}");
            }

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error validating file hash: {filePath}", ex);
            return false;
        }
    }

    /// <summary>
    /// セッションの一時ファイルをクリーンアップ
    /// </summary>
    public async Task CleanupSessionAsync(Guid sessionId)
    {
        try
        {
            var sessionDir = Path.Combine(_tempDirectory, sessionId.ToString());

            if (Directory.Exists(sessionDir))
            {
                await Task.Run(() => Directory.Delete(sessionDir, recursive: true));
                _logger.Info($"Session temp files cleaned up: SessionId={sessionId}");
            }

            // チャンク追跡情報をクリーンアップ
            var keysToRemove = _chunkTrackers.Keys.Where(k => k.StartsWith(sessionId.ToString())).ToList();
            foreach (var key in keysToRemove)
            {
                _chunkTrackers.TryRemove(key, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error cleaning up session: SessionId={sessionId}", ex);
        }
    }

    /// <summary>
    /// 古い一時ファイルをクリーンアップ
    /// </summary>
    public async Task CleanupOldFilesAsync(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(_tempDirectory))
            {
                return;
            }

            var cutoffTime = DateTime.UtcNow - olderThan;
            var deletedCount = 0;

            await Task.Run(() =>
            {
                var sessionDirs = Directory.GetDirectories(_tempDirectory);

                foreach (var sessionDir in sessionDirs)
                {
                    var dirInfo = new DirectoryInfo(sessionDir);

                    if (dirInfo.LastWriteTimeUtc < cutoffTime)
                    {
                        try
                        {
                            dirInfo.Delete(recursive: true);
                            deletedCount++;
                            _logger.Debug($"Deleted old session directory: {sessionDir}");
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning($"Failed to delete old session directory: {sessionDir}: {ex.Message}");
                        }
                    }
                }
            });

            // 古いチャンク追跡情報をクリーンアップ
            var keysToRemove = _chunkTrackers
                .Where(kvp => kvp.Value.LastUpdateTime < cutoffTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _chunkTrackers.TryRemove(key, out _);
            }

            if (deletedCount > 0)
            {
                _logger.Info($"Cleaned up {deletedCount} old session directories");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error cleaning up old files", ex);
        }
    }

    /// <summary>
    /// ファイル名をサニタイズ（安全なファイル名に変換）
    /// </summary>
    private string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "unnamed_file";
        }

        // 無効な文字を置換
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder(fileName);

        foreach (var c in invalidChars)
        {
            sanitized.Replace(c, '_');
        }

        // パス区切り文字も置換
        sanitized.Replace('/', '_');
        sanitized.Replace('\\', '_');

        return sanitized.ToString();
    }

    /// <summary>
    /// チャンク追跡情報
    /// </summary>
    private class ChunkTracker
    {
        public Guid SessionId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public int TotalChunks { get; set; }
        public ConcurrentDictionary<int, bool> ReceivedChunks { get; set; } = new();
        public DateTime LastUpdateTime { get; set; }
    }
}
