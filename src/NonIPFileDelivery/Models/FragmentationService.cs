using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// フラグメント処理サービス実装
/// Phase 3: 大きなペイロードの分割・再構築機能
/// </summary>
public class FragmentationService : IFragmentationService
{
    private readonly ILoggingService _logger;
    private readonly ConcurrentDictionary<Guid, FragmentGroup> _fragmentGroups;
    private long _totalFragmentsSent;
    private long _totalFragmentsReceived;
    private long _totalReassembledPayloads;

    public FragmentationService(ILoggingService logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fragmentGroups = new ConcurrentDictionary<Guid, FragmentGroup>();
    }

    /// <summary>
    /// 大きなペイロードをフラグメントに分割します
    /// </summary>
    public Task<List<NonIPFrame>> FragmentPayloadAsync(byte[] payload, int maxFragmentSize = 8000)
    {
        if (payload == null || payload.Length == 0)
            throw new ArgumentException("Payload cannot be null or empty", nameof(payload));

        if (maxFragmentSize <= 0)
            throw new ArgumentException("Max fragment size must be positive", nameof(maxFragmentSize));

        // ペイロードが分割不要な場合はそのまま返す
        if (payload.Length <= maxFragmentSize)
        {
            var singleFrame = new NonIPFrame
            {
                Header = new FrameHeader
                {
                    Type = FrameType.Data,
                    PayloadLength = (ushort)payload.Length,
                    Flags = FrameFlags.None
                },
                Payload = payload
            };

            return Task.FromResult(new List<NonIPFrame> { singleFrame });
        }

        // フラグメントグループID生成
        var fragmentGroupId = Guid.NewGuid();
        var totalFragments = (uint)Math.Ceiling((double)payload.Length / maxFragmentSize);
        var originalHash = ComputeSHA256Hash(payload);

        var fragments = new List<NonIPFrame>();

        _logger.Info($"Fragmenting payload: Size={payload.Length} bytes, " +
                    $"MaxFragmentSize={maxFragmentSize}, " +
                    $"TotalFragments={totalFragments}, " +
                    $"FragmentGroupId={fragmentGroupId}");

        for (uint i = 0; i < totalFragments; i++)
        {
            var offset = (int)(i * maxFragmentSize);
            var fragmentSize = Math.Min(maxFragmentSize, payload.Length - offset);
            var fragmentData = new byte[fragmentSize];
            Buffer.BlockCopy(payload, offset, fragmentData, 0, fragmentSize);

            var fragmentInfo = new FragmentInfo
            {
                FragmentGroupId = fragmentGroupId,
                FragmentIndex = i,
                TotalFragments = totalFragments,
                FragmentSize = (uint)fragmentSize,
                OriginalPayloadSize = payload.Length,
                OriginalPayloadHash = originalHash
            };

            var frame = new NonIPFrame
            {
                Header = new FrameHeader
                {
                    Type = FrameType.Fragment,
                    PayloadLength = (ushort)fragmentSize,
                    Flags = i == 0 ? FrameFlags.FragmentStart :
                           i == totalFragments - 1 ? FrameFlags.FragmentEnd :
                           FrameFlags.None,
                    FragmentInfo = fragmentInfo
                },
                Payload = fragmentData
            };

            fragments.Add(frame);
            System.Threading.Interlocked.Increment(ref _totalFragmentsSent);
        }

        _logger.Debug($"Payload fragmented: {fragments.Count} fragments created");

        return Task.FromResult(fragments);
    }

    /// <summary>
    /// フラグメントを追加し、全て揃ったら元のペイロードを再構築します
    /// </summary>
    public Task<ReassemblyResult?> AddFragmentAsync(NonIPFrame frame)
    {
        if (frame == null || frame.Header.FragmentInfo == null)
            throw new ArgumentException("Frame or FragmentInfo cannot be null", nameof(frame));

        var fragmentInfo = frame.Header.FragmentInfo;
        var fragmentGroupId = fragmentInfo.FragmentGroupId;

        System.Threading.Interlocked.Increment(ref _totalFragmentsReceived);

        // フラグメントグループを取得または作成
        var fragmentGroup = _fragmentGroups.GetOrAdd(fragmentGroupId, _ =>
        {
            _logger.Debug($"New fragment group created: FragmentGroupId={fragmentGroupId}, " +
                         $"TotalFragments={fragmentInfo.TotalFragments}");

            return new FragmentGroup
            {
                FragmentGroupId = fragmentGroupId,
                TotalFragments = fragmentInfo.TotalFragments,
                OriginalPayloadSize = fragmentInfo.OriginalPayloadSize,
                OriginalPayloadHash = fragmentInfo.OriginalPayloadHash,
                FirstFragmentReceivedAt = DateTime.UtcNow,
                LastFragmentReceivedAt = DateTime.UtcNow
            };
        });

        // フラグメントデータを追加
        if (fragmentGroup.ReceivedFragments.TryAdd(fragmentInfo.FragmentIndex, frame.Payload))
        {
            fragmentGroup.LastFragmentReceivedAt = DateTime.UtcNow;

            _logger.Debug($"Fragment received: FragmentGroupId={fragmentGroupId}, " +
                         $"FragmentIndex={fragmentInfo.FragmentIndex}, " +
                         $"Progress={fragmentGroup.GetProgress():P0}");
        }
        else
        {
            _logger.Warning($"Duplicate fragment received: FragmentGroupId={fragmentGroupId}, " +
                           $"FragmentIndex={fragmentInfo.FragmentIndex}");
        }

        // 全てのフラグメントが揃ったか確認
        if (!fragmentGroup.IsComplete())
        {
            return Task.FromResult<ReassemblyResult?>(null);
        }

        // 再構築処理
        var stopwatch = Stopwatch.StartNew();
        var result = ReassemblePayload(fragmentGroup);
        stopwatch.Stop();

        result.ReassemblyTimeMs = stopwatch.ElapsedMilliseconds;

        // フラグメントグループを削除
        _fragmentGroups.TryRemove(fragmentGroupId, out _);
        System.Threading.Interlocked.Increment(ref _totalReassembledPayloads);

        _logger.Info($"Payload reassembled: FragmentGroupId={fragmentGroupId}, " +
                    $"Size={result.ReassembledPayload.Length} bytes, " +
                    $"TimeMs={result.ReassemblyTimeMs}, " +
                    $"HashValid={result.IsHashValid}");

        return Task.FromResult<ReassemblyResult?>(result);
    }

    /// <summary>
    /// 指定されたFragmentGroupIdの受信進捗を取得します
    /// </summary>
    public Task<double?> GetFragmentProgressAsync(Guid fragmentGroupId)
    {
        if (_fragmentGroups.TryGetValue(fragmentGroupId, out var fragmentGroup))
        {
            return Task.FromResult<double?>(fragmentGroup.GetProgress());
        }

        return Task.FromResult<double?>(null);
    }

    /// <summary>
    /// タイムアウトしたフラグメントグループをクリーンアップします
    /// </summary>
    public Task<int> CleanupTimedOutFragmentsAsync()
    {
        var timedOutGroups = _fragmentGroups.Values
            .Where(g => g.IsTimedOut())
            .ToList();

        int cleanedCount = 0;

        foreach (var group in timedOutGroups)
        {
            _logger.Warning($"Fragment group timed out: FragmentGroupId={group.FragmentGroupId}, " +
                           $"ReceivedFragments={group.ReceivedFragments.Count}/{group.TotalFragments}");

            if (_fragmentGroups.TryRemove(group.FragmentGroupId, out _))
            {
                cleanedCount++;
            }
        }

        if (cleanedCount > 0)
        {
            _logger.Info($"Cleaned up {cleanedCount} timed-out fragment groups");
        }

        return Task.FromResult(cleanedCount);
    }

    /// <summary>
    /// フラグメント統計情報を取得します
    /// </summary>
    public Task<Dictionary<string, object>> GetFragmentStatisticsAsync()
    {
        var pendingGroups = _fragmentGroups.Count;
        var totalFragmentsSent = System.Threading.Interlocked.Read(ref _totalFragmentsSent);
        var totalFragmentsReceived = System.Threading.Interlocked.Read(ref _totalFragmentsReceived);
        var totalReassembledPayloads = System.Threading.Interlocked.Read(ref _totalReassembledPayloads);

        var stats = new Dictionary<string, object>
        {
            { "PendingFragmentGroups", pendingGroups },
            { "TotalFragmentsSent", totalFragmentsSent },
            { "TotalFragmentsReceived", totalFragmentsReceived },
            { "TotalReassembledPayloads", totalReassembledPayloads }
        };

        return Task.FromResult(stats);
    }

    /// <summary>
    /// 指定されたFragmentGroupIdのフラグメントグループを削除します
    /// </summary>
    public Task RemoveFragmentGroupAsync(Guid fragmentGroupId)
    {
        if (_fragmentGroups.TryRemove(fragmentGroupId, out _))
        {
            _logger.Debug($"Fragment group removed: FragmentGroupId={fragmentGroupId}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// フラグメントグループから元のペイロードを再構築します
    /// </summary>
    private ReassemblyResult ReassemblePayload(FragmentGroup fragmentGroup)
    {
        try
        {
            // フラグメントをインデックス順にソート
            var sortedFragments = fragmentGroup.ReceivedFragments
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => kvp.Value)
                .ToList();

            // ペイロードサイズを計算
            var totalSize = sortedFragments.Sum(f => f.Length);

            // 元のペイロードを再構築
            var reassembledPayload = new byte[totalSize];
            var offset = 0;

            foreach (var fragment in sortedFragments)
            {
                Buffer.BlockCopy(fragment, 0, reassembledPayload, offset, fragment.Length);
                offset += fragment.Length;
            }

            // ハッシュ検証
            var computedHash = ComputeSHA256Hash(reassembledPayload);
            var isHashValid = computedHash == fragmentGroup.OriginalPayloadHash;

            if (!isHashValid)
            {
                _logger.Error($"Hash mismatch: FragmentGroupId={fragmentGroup.FragmentGroupId}, " +
                             $"Expected={fragmentGroup.OriginalPayloadHash}, " +
                             $"Actual={computedHash}");
            }

            return new ReassemblyResult
            {
                FragmentGroupId = fragmentGroup.FragmentGroupId,
                ReassembledPayload = reassembledPayload,
                IsSuccess = true,
                IsHashValid = isHashValid,
                ReceivedFragmentCount = fragmentGroup.ReceivedFragments.Count,
                TotalFragmentCount = (int)fragmentGroup.TotalFragments
            };
        }
        catch (Exception ex)
        {
            _logger.Error($"Reassembly failed: FragmentGroupId={fragmentGroup.FragmentGroupId}", ex);

            return new ReassemblyResult
            {
                FragmentGroupId = fragmentGroup.FragmentGroupId,
                IsSuccess = false,
                ErrorMessage = ex.Message,
                ReceivedFragmentCount = fragmentGroup.ReceivedFragments.Count,
                TotalFragmentCount = (int)fragmentGroup.TotalFragments
            };
        }
    }

    /// <summary>
    /// SHA256ハッシュを計算します
    /// </summary>
    private string ComputeSHA256Hash(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(data);
        return Convert.ToHexString(hashBytes);
    }
}
