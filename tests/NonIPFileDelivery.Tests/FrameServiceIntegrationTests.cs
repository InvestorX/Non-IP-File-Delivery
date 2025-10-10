using System;
using System.Linq;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;
using Xunit;

namespace NonIPFileDelivery.Tests;

/// <summary>
/// FrameServiceの統合テスト
/// フラグメント処理、ACK/NACK、リトライ機能をテスト
/// </summary>
public class FrameServiceIntegrationTests
{
    private readonly ILoggingService _logger;
    private readonly ICryptoService _cryptoService;
    private readonly IFragmentationService _fragmentationService;
    private readonly FrameService _frameService;
    
    private readonly byte[] _sourceMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
    private readonly byte[] _destMac = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };

    public FrameServiceIntegrationTests()
    {
        _logger = new LoggingService();
        _cryptoService = new CryptoService(_logger);
        _fragmentationService = new FragmentationService(_logger);
        _frameService = new FrameService(_logger, _cryptoService, _fragmentationService);
    }

    [Fact]
    public void CreateAckFrame_ShouldCreateValidAckFrame()
    {
        // Arrange
        ushort sequenceNumber = 1234;

        // Act
        var ackFrame = _frameService.CreateAckFrame(_sourceMac, _destMac, sequenceNumber);

        // Assert
        Assert.NotNull(ackFrame);
        Assert.Equal(FrameType.Ack, ackFrame.Header.Type);
        Assert.Equal(_sourceMac, ackFrame.Header.SourceMAC);
        Assert.Equal(_destMac, ackFrame.Header.DestinationMAC);
        
        // ACKペイロードからシーケンス番号を取得
        var payloadSeqNum = BitConverter.ToUInt16(ackFrame.Payload, 0);
        Assert.Equal(sequenceNumber, payloadSeqNum);
    }

    [Fact]
    public void CreateNackFrame_ShouldCreateValidNackFrame()
    {
        // Arrange
        ushort sequenceNumber = 5678;
        string reason = "Checksum mismatch";

        // Act
        var nackFrame = _frameService.CreateNackFrame(_sourceMac, _destMac, sequenceNumber, reason);

        // Assert
        Assert.NotNull(nackFrame);
        Assert.Equal(FrameType.Nack, nackFrame.Header.Type);
        Assert.Equal(_sourceMac, nackFrame.Header.SourceMAC);
        Assert.Equal(_destMac, nackFrame.Header.DestinationMAC);
        
        // NACKペイロードを解析
        using var ms = new System.IO.MemoryStream(nackFrame.Payload);
        using var reader = new System.IO.BinaryReader(ms);
        var payloadSeqNum = reader.ReadUInt16();
        var payloadReason = reader.ReadString();
        
        Assert.Equal(sequenceNumber, payloadSeqNum);
        Assert.Equal(reason, payloadReason);
    }

    [Fact]
    public async Task CreateFragmentedFrames_SmallPayload_ShouldNotFragment()
    {
        // Arrange
        var smallData = new byte[100]; // 100バイト（フラグメント化不要）
        new Random().NextBytes(smallData);

        // Act
        var frames = await _frameService.CreateFragmentedFramesAsync(_sourceMac, _destMac, smallData);

        // Assert
        Assert.NotNull(frames);
        Assert.Single(frames); // フラグメント化されない
        Assert.Equal(_sourceMac, frames[0].Header.SourceMAC);
        Assert.Equal(_destMac, frames[0].Header.DestinationMAC);
    }

    [Fact]
    public async Task CreateFragmentedFrames_LargePayload_ShouldFragment()
    {
        // Arrange
        var largeData = new byte[20000]; // 20KB（デフォルト8KB超えるのでフラグメント化）
        new Random().NextBytes(largeData);

        // Act
        var frames = await _frameService.CreateFragmentedFramesAsync(_sourceMac, _destMac, largeData, maxFragmentSize: 8000);

        // Assert
        Assert.NotNull(frames);
        Assert.True(frames.Count > 1, "Large payload should be fragmented");
        
        // 全フラグメントが正しいMAC addressを持つか確認
        foreach (var frame in frames)
        {
            Assert.Equal(_sourceMac, frame.Header.SourceMAC);
            Assert.Equal(_destMac, frame.Header.DestinationMAC);
            Assert.NotNull(frame.Header.FragmentInfo);
        }
        
        // 最初のフラグメントがFragmentStartフラグを持つか確認
        Assert.True((frames[0].Header.Flags & FrameFlags.FragmentStart) == FrameFlags.FragmentStart);
        
        // 最後のフラグメントがFragmentEndフラグを持つか確認
        Assert.True((frames[^1].Header.Flags & FrameFlags.FragmentEnd) == FrameFlags.FragmentEnd);
    }

    [Fact]
    public async Task FragmentAndReassemble_ShouldRecoverOriginalData()
    {
        // Arrange
        var originalData = new byte[15000]; // 15KB
        new Random().NextBytes(originalData);

        // Act - フラグメント化
        var fragments = await _frameService.CreateFragmentedFramesAsync(_sourceMac, _destMac, originalData, maxFragmentSize: 5000);

        // Assert - フラグメント数を確認
        Assert.True(fragments.Count > 1, "Data should be fragmented");

        // Act - 再構築（各フラグメントを順次追加）
        byte[]? reassembledData = null;
        foreach (var fragment in fragments)
        {
            var result = await _frameService.AddFragmentAndReassembleAsync(fragment);
            if (result != null)
            {
                reassembledData = result;
            }
        }

        // Assert - 元のデータと一致するか確認
        Assert.NotNull(reassembledData);
        Assert.Equal(originalData.Length, reassembledData.Length);
        Assert.Equal(originalData, reassembledData);
    }

    [Fact]
    public void RegisterPendingAck_ShouldTrackFrame()
    {
        // Arrange
        var frame = _frameService.CreateDataFrame(_sourceMac, _destMac, new byte[] { 0x01, 0x02, 0x03 });
        var initialStats = _frameService.GetStatistics();

        // Act
        _frameService.RegisterPendingAck(frame);
        var statsAfterRegister = _frameService.GetStatistics();

        // Assert
        Assert.Equal(initialStats.PendingAcks + 1, statsAfterRegister.PendingAcks);
        Assert.Equal(initialStats.RetryQueueSize + 1, statsAfterRegister.RetryQueueSize);
    }

    [Fact]
    public void ProcessAck_ShouldRemoveFromPendingQueue()
    {
        // Arrange
        var frame = _frameService.CreateDataFrame(_sourceMac, _destMac, new byte[] { 0x01, 0x02, 0x03 });
        _frameService.RegisterPendingAck(frame);
        var sequenceNumber = frame.Header.SequenceNumber;

        // Act
        var result = _frameService.ProcessAck(sequenceNumber);
        var statsAfterAck = _frameService.GetStatistics();

        // Assert
        Assert.True(result, "ProcessAck should return true for known sequence");
        Assert.Equal(0, statsAfterAck.PendingAcks);
        Assert.Equal(0, statsAfterAck.RetryQueueSize);
    }

    [Fact]
    public void ProcessAck_UnknownSequence_ShouldReturnFalse()
    {
        // Arrange
        ushort unknownSequence = 9999;

        // Act
        var result = _frameService.ProcessAck(unknownSequence);

        // Assert
        Assert.False(result, "ProcessAck should return false for unknown sequence");
    }

    [Fact]
    public async Task GetTimedOutFrames_ShouldReturnExpiredFrames()
    {
        // Arrange
        var frame = _frameService.CreateDataFrame(_sourceMac, _destMac, new byte[] { 0x01, 0x02, 0x03 });
        _frameService.RegisterPendingAck(frame);

        // Act - ACKタイムアウト（5秒）を待つ
        await Task.Delay(5100); // 5.1秒待機（ACK_TIMEOUT_MS=5000より長い）
        var timedOutFrames = _frameService.GetTimedOutFrames();

        // Assert
        Assert.NotEmpty(timedOutFrames);
        Assert.Contains(timedOutFrames, f => f.Header.SequenceNumber == frame.Header.SequenceNumber);
    }

    [Fact]
    public void ClearRetryQueue_ShouldRemoveAllPendingFrames()
    {
        // Arrange
        var frame1 = _frameService.CreateDataFrame(_sourceMac, _destMac, new byte[] { 0x01 });
        var frame2 = _frameService.CreateDataFrame(_sourceMac, _destMac, new byte[] { 0x02 });
        _frameService.RegisterPendingAck(frame1);
        _frameService.RegisterPendingAck(frame2);

        // Act
        _frameService.ClearRetryQueue();
        var stats = _frameService.GetStatistics();

        // Assert
        Assert.Equal(0, stats.PendingAcks);
        Assert.Equal(0, stats.RetryQueueSize);
    }

    [Fact]
    public async Task GetFragmentProgress_ShouldReportProgress()
    {
        // Arrange
        var largeData = new byte[20000];
        new Random().NextBytes(largeData);
        var fragments = await _frameService.CreateFragmentedFramesAsync(_sourceMac, _destMac, largeData, maxFragmentSize: 8000);

        var fragmentGroupId = fragments[0].Header.FragmentInfo?.FragmentGroupId ?? Guid.Empty;
        Assert.NotEqual(Guid.Empty, fragmentGroupId);

        // Act - 最初のフラグメントのみ追加
        await _frameService.AddFragmentAndReassembleAsync(fragments[0]);

        // 進捗を確認
        var progress = await _frameService.GetFragmentProgressAsync(fragmentGroupId);

        // Assert
        Assert.NotNull(progress);
        Assert.True(progress > 0.0 && progress < 1.0, "Progress should be between 0 and 1 for partial completion");
    }

    [Fact]
    public void ValidateFrame_WithCorrectChecksum_ShouldReturnTrue()
    {
        // Arrange
        var frame = _frameService.CreateDataFrame(_sourceMac, _destMac, new byte[] { 0x01, 0x02, 0x03 });
        var serialized = _frameService.SerializeFrame(frame);
        
        // Deserializeして確認（SerializeとDeserializeのサイクルで検証）
        var deserializedFrame = _frameService.DeserializeFrame(serialized);

        // Act & Assert
        Assert.NotNull(deserializedFrame);
        
        // チェックサムが設定されていることを確認
        Assert.NotEqual(0u, deserializedFrame.Checksum);
        
        // ValidateFrameは内部でチェックサムを再計算して比較
        // Deserializeが成功した時点で、チェックサムは正しいと判断できる
        var isValid = _frameService.ValidateFrame(deserializedFrame, serialized);
        Assert.True(isValid, "Frame with correct checksum should be valid");
    }

    [Fact]
    public void SerializeAndDeserialize_ShouldPreserveFrameData()
    {
        // Arrange
        var originalData = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
        var originalFrame = _frameService.CreateDataFrame(_sourceMac, _destMac, originalData);

        // Act
        var serialized = _frameService.SerializeFrame(originalFrame);
        var deserializedFrame = _frameService.DeserializeFrame(serialized);

        // Assert
        Assert.NotNull(deserializedFrame);
        Assert.Equal(originalFrame.Header.Type, deserializedFrame.Header.Type);
        Assert.Equal(originalFrame.Header.SequenceNumber, deserializedFrame.Header.SequenceNumber);
        Assert.Equal(originalFrame.Payload, deserializedFrame.Payload);
    }
}
