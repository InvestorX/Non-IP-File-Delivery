using Xunit;
using Moq;
using NonIPFileDelivery.Services;
using NonIPFileDelivery.Models;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace NonIPFileDelivery.Tests;

/// <summary>
/// QoSService統合テスト
/// </summary>
public class QoSServiceTests : IDisposable
{
    private readonly Mock<ILoggingService> _mockLogger;
    private readonly QoSService _service;

    public QoSServiceTests()
    {
        _mockLogger = new Mock<ILoggingService>();
        _service = new QoSService(_mockLogger.Object);
    }

    public void Dispose()
    {
        _service?.Dispose();
    }

    #region 基本機能テスト

    [Fact]
    public void Constructor_ShouldInitializeWithDefaultSettings()
    {
        // Assert
        Assert.False(_service.IsEnabled); // デフォルトは無効
        Assert.Equal(0, _service.MaxBandwidthMbps); // 未設定は0
    }

    [Fact]
    public void Enable_ShouldActivateQoS()
    {
        // Act
        _service.Enable();

        // Assert
        Assert.True(_service.IsEnabled);
    }

    [Fact]
    public void Disable_ShouldDeactivateQoS()
    {
        // Arrange
        _service.Enable();

        // Act
        _service.Disable();

        // Assert
        Assert.False(_service.IsEnabled);
    }

    [Fact]
    public void SetBandwidthLimit_ShouldUpdateMaxBandwidth()
    {
        // Arrange
        int expectedBandwidth = 1000;

        // Act
        _service.SetBandwidthLimit(expectedBandwidth);

        // Assert
        Assert.Equal(expectedBandwidth, _service.MaxBandwidthMbps);
    }

    [Fact]
    public void ConfigureWeights_ShouldNotThrowException()
    {
        // Act & Assert (メソッドが存在し、例外をスローしないことを確認)
        var exception = Record.Exception(() => _service.ConfigureWeights(70, 20, 10));
        Assert.Null(exception);
    }

    #endregion

    #region フレームのEnqueue/Dequeueテスト

    [Fact]
    public async Task EnqueueFrameAsync_WhenEnabled_ShouldReturnTrue()
    {
        // Arrange
        _service.Enable();
        var frame = CreateTestFrame();

        // Act
        var result = await _service.EnqueueFrameAsync(frame);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task EnqueueFrameAsync_WhenDisabled_ShouldStillAcceptFrames()
    {
        // Arrange
        var frame = CreateTestFrame();
        // サービスはデフォルトで無効

        // Act
        var result = await _service.EnqueueFrameAsync(frame);

        // Assert - QoSが無効でもフレームは受け入れる
        Assert.True(result);
    }

    [Fact]
    public async Task DequeueFrameAsync_ShouldReturnEnqueuedFrame()
    {
        // Arrange
        _service.Enable();
        var frame = CreateTestFrame();
        await _service.EnqueueFrameAsync(frame);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var dequeuedFrame = await _service.DequeueFrameAsync(cts.Token);

        // Assert
        Assert.NotNull(dequeuedFrame);
        Assert.Equal(frame.Header.SequenceNumber, dequeuedFrame.Header.SequenceNumber);
    }

    [Fact]
    public async Task DequeueFrameAsync_WithMultiplePriorities_ShouldRespectPriorityOrder()
    {
        // Arrange
        _service.Enable();
        var lowFrame = CreateTestFrame(FrameType.Error); // Low priority
        var normalFrame = CreateTestFrame(FrameType.Data); // Normal priority
        var highFrame = CreateTestFrame(FrameType.Heartbeat); // High priority

        // 逆順にエンキュー（Low → Normal → High）
        await _service.EnqueueFrameAsync(lowFrame);
        await _service.EnqueueFrameAsync(normalFrame);
        await _service.EnqueueFrameAsync(highFrame);

        // Act - 優先度順にデキュー
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var first = await _service.DequeueFrameAsync(cts.Token);
        var second = await _service.DequeueFrameAsync(cts.Token);
        var third = await _service.DequeueFrameAsync(cts.Token);

        // Assert - High → Normal → Low の順
        Assert.Equal(FrameType.Heartbeat, first!.Header.Type); // High
        Assert.Equal(FrameType.Data, second!.Header.Type); // Normal
        Assert.Equal(FrameType.Error, third!.Header.Type); // Low
    }

    #endregion

    #region 帯域幅制限テスト

    [Fact]
    public async Task TryConsumeAsync_WithSufficientTokens_ShouldReturnTrue()
    {
        // Arrange
        _service.SetBandwidthLimit(1000); // 1Gbps = 125MB/s
        _service.Enable();
        long consumeBytes = 1000; // 1KB

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await _service.TryConsumeAsync(consumeBytes, cts.Token);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task TryConsumeAsync_MultipleSmallRequests_ShouldSucceed()
    {
        // Arrange
        _service.SetBandwidthLimit(1000); // 1Gbps
        _service.Enable();

        // Act - 小さなリクエストを複数回
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result1 = await _service.TryConsumeAsync(100, cts.Token);
        var result2 = await _service.TryConsumeAsync(200, cts.Token);
        var result3 = await _service.TryConsumeAsync(300, cts.Token);

        // Assert
        Assert.True(result1);
        Assert.True(result2);
        Assert.True(result3);
    }

    [Fact]
    public async Task TryConsumeAsync_WhenDisabled_ShouldAlwaysReturnTrue()
    {
        // Arrange
        _service.SetBandwidthLimit(100); // 低い帯域幅
        _service.Disable(); // QoS無効
        long largeBytes = 1_000_000_000; // 1GB

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await _service.TryConsumeAsync(largeBytes, cts.Token);

        // Assert
        Assert.True(result); // QoS無効時は常に許可
    }

    #endregion

    #region 統計情報テスト

    [Fact]
    public void GetStatistics_ShouldReturnAccurateMetrics()
    {
        // Act
        var stats = _service.GetStatistics();

        // Assert
        Assert.NotNull(stats);
        Assert.Equal(0, stats.TotalEnqueued); // 初期状態
        Assert.Equal(0, stats.TotalDequeued);
    }

    [Fact]
    public async Task GetStatistics_AfterEnqueueDequeue_ShouldUpdateCounters()
    {
        // Arrange
        _service.Enable();
        var frame = CreateTestFrame();
        await _service.EnqueueFrameAsync(frame);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await _service.DequeueFrameAsync(cts.Token);

        // Act
        var stats = _service.GetStatistics();

        // Assert
        Assert.Equal(1, stats.TotalEnqueued);
        Assert.Equal(1, stats.TotalDequeued);
    }

    [Fact]
    public void LogStatistics_ShouldNotThrowException()
    {
        // Act & Assert
        var exception = Record.Exception(() => _service.LogStatistics());
        Assert.Null(exception);
    }

    #endregion

    #region キュー管理テスト

    [Fact]
    public async Task ClearQueue_ShouldRemoveAllFrames()
    {
        // Arrange
        _service.Enable();
        await _service.EnqueueFrameAsync(CreateTestFrame());
        await _service.EnqueueFrameAsync(CreateTestFrame());
        await _service.EnqueueFrameAsync(CreateTestFrame());

        // Act
        _service.ClearQueue();

        // Assert
        var stats = _service.GetStatistics();
        Assert.Equal(0, stats.CurrentQueueSize);
    }

    #endregion

    #region エッジケーステスト

    [Fact]
    public async Task EnqueueFrameAsync_WithNullFrame_ShouldThrowException()
    {
        // Arrange
        _service.Enable();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await _service.EnqueueFrameAsync(null!));
    }

    [Fact]
    public async Task TryConsumeAsync_WithZeroSize_ShouldReturnTrue()
    {
        // Arrange
        _service.SetBandwidthLimit(1000);
        _service.Enable();

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await _service.TryConsumeAsync(0, cts.Token);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task TryConsumeAsync_WithNegativeSize_ShouldStillSucceed()
    {
        // Arrange
        _service.SetBandwidthLimit(1000);
        _service.Enable();

        // Act - TokenBucketは負のサイズでもtrueを返す可能性がある
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await _service.TryConsumeAsync(-100, cts.Token);

        // Assert - 実装依存の動作
        Assert.True(result);
    }

    [Fact]
    public async Task EnqueueFrameAsync_ConcurrentCalls_ShouldBeSafe()
    {
        // Arrange
        _service.Enable();
        var tasks = new List<Task<bool>>();

        // Act - 10個の並行エンキュー
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(_service.EnqueueFrameAsync(CreateTestFrame()));
        }
        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.All(results, result => Assert.True(result));
        var stats = _service.GetStatistics();
        Assert.Equal(10, stats.TotalEnqueued);
    }

    [Fact]
    public async Task DequeueFrameAsync_WhenQueueEmpty_ShouldReturnNull()
    {
        // Arrange
        _service.Enable();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Act
        var result = await _service.DequeueFrameAsync(cts.Token);

        // Assert - キャンセル時はnullを返す
        Assert.Null(result);
    }

    [Fact]
    public void MultipleEnable_ShouldBeIdempotent()
    {
        // Act
        _service.Enable();
        _service.Enable();
        _service.Enable();

        // Assert
        Assert.True(_service.IsEnabled);
    }

    #endregion

    #region ヘルパーメソッド

    private NonIPFrame CreateTestFrame(FrameType frameType = FrameType.Data)
    {
        return new NonIPFrame
        {
            Header = new FrameHeader
            {
                SourceMAC = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 },
                DestinationMAC = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF },
                Type = frameType,
                SequenceNumber = 1,
                Timestamp = DateTime.UtcNow
            },
            Payload = new byte[] { 0x01, 0x02, 0x03, 0x04 }
        };
    }

    #endregion
}
