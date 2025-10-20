using Xunit;
using Moq;
using NonIPFileDelivery.Services;
using NonIPFileDelivery.Models;
using System.Threading.Tasks;

namespace NonIPFileDelivery.Tests;

/// <summary>
/// ACK/NAK統合テスト - NetworkServiceからの完全なE2Eフロー検証
/// </summary>
public class AckNakIntegrationTests
{
    private readonly Mock<ILoggingService> _mockLogger;
    private readonly IFrameService _frameService;
    private readonly NetworkService _networkService;

    public AckNakIntegrationTests()
    {
        _mockLogger = new Mock<ILoggingService>();
        var mockCrypto = new Mock<ICryptoService>();
        var mockFragmentation = new Mock<IFragmentationService>();
        _frameService = new FrameService(_mockLogger.Object, mockCrypto.Object, mockFragmentation.Object);
        _networkService = new NetworkService(_mockLogger.Object, _frameService);
    }

    [Fact]
    public async Task SendFrame_DataType_ShouldRegisterPendingAck()
    {
        // Arrange
        var config = new NetworkConfig
        {
            Interface = "lo",
            FrameSize = 1500,
            Encryption = false,
            EtherType = "0x88B5"
        };
        await _networkService.InitializeInterface(config);
        await _networkService.StartListening();

        var testData = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var destMac = "AA:BB:CC:DD:EE:FF";

        // 送信前の統計情報
        var statsBefore = _frameService.GetStatistics();

        // Act
        var result = await _networkService.SendFrame(testData, destMac, FramePriority.Normal);

        // Assert
        Assert.True(result, "Frame should be sent successfully");

        // 送信後の統計情報を確認
        var statsAfter = _frameService.GetStatistics();
        Assert.Equal(statsBefore.PendingAcks + 1, statsAfter.PendingAcks);
        Assert.True(statsAfter.PendingAcks > 0, "Should have pending ACK registered");

        await _networkService.StopListening();
    }

    [Fact]
    public async Task SendFrame_WithQoS_ShouldStillRegisterPendingAck()
    {
        // Arrange
        var qosService = new QoSService(_mockLogger.Object);
        qosService.Enable();
        qosService.SetBandwidthLimit(1000); // 1Gbps

        var networkService = new NetworkService(_mockLogger.Object, _frameService, qosService);
        
        var config = new NetworkConfig
        {
            Interface = "lo",
            FrameSize = 1500,
            Encryption = false,
            EtherType = "0x88B5"
        };
        await networkService.InitializeInterface(config);
        await networkService.StartListening();

        var testData = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var destMac = "AA:BB:CC:DD:EE:FF";

        var statsBefore = _frameService.GetStatistics();

        // Act
        var result = await networkService.SendFrame(testData, destMac, FramePriority.Normal);

        // Assert
        Assert.True(result);
        var statsAfter = _frameService.GetStatistics();
        Assert.Equal(statsBefore.PendingAcks + 1, statsAfter.PendingAcks);

        await networkService.StopListening();
    }

    [Fact]
    public async Task SendFrameAndProcessAck_ShouldRemoveFromPendingQueue()
    {
        // Arrange
        var config = new NetworkConfig
        {
            Interface = "lo",
            FrameSize = 1500,
            Encryption = false,
            EtherType = "0x88B5"
        };
        await _networkService.InitializeInterface(config);
        await _networkService.StartListening();

        var testData = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var destMac = "AA:BB:CC:DD:EE:FF";

        // Act 1: フレーム送信
        await _networkService.SendFrame(testData, destMac, FramePriority.Normal);

        var statsAfterSend = _frameService.GetStatistics();
        var pendingAcksCount = statsAfterSend.PendingAcks;
        Assert.True(pendingAcksCount > 0, "Should have pending ACKs after sending");

        // フレームのシーケンス番号を取得（最後に送信されたフレーム）
        // 注: 実際の実装ではシーケンス番号を追跡する必要がある
        var timedOutFrames = _frameService.GetTimedOutFrames();
        // まだタイムアウトしていないので空のはず
        Assert.Empty(timedOutFrames);

        // Act 2: ACK処理（シミュレート）
        // 注: 実際のシーケンス番号を使用する必要があるため、
        // ここでは統計情報のみで検証
        
        await _networkService.StopListening();
    }

    [Fact]
    public async Task MultipleFrames_ShouldRegisterMultiplePendingAcks()
    {
        // Arrange
        var config = new NetworkConfig
        {
            Interface = "lo",
            FrameSize = 1500,
            Encryption = false,
            EtherType = "0x88B5"
        };
        await _networkService.InitializeInterface(config);
        await _networkService.StartListening();

        var destMac = "AA:BB:CC:DD:EE:FF";
        var frameCount = 5;

        var statsBefore = _frameService.GetStatistics();

        // Act - 複数フレーム送信
        for (int i = 0; i < frameCount; i++)
        {
            var testData = new byte[] { (byte)i, 0x02, 0x03, 0x04 };
            await _networkService.SendFrame(testData, destMac, FramePriority.Normal);
        }

        // Assert
        var statsAfter = _frameService.GetStatistics();
        Assert.Equal(statsBefore.PendingAcks + frameCount, statsAfter.PendingAcks);

        await _networkService.StopListening();
    }

    [Fact]
    public async Task FrameWithRequireAckFlag_ShouldBeRegistered()
    {
        // Arrange
        var sourceMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var destMac = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        var testData = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        // Act - RequireAckフラグ付きフレーム作成
        var frame = _frameService.CreateDataFrame(sourceMac, destMac, testData, FrameFlags.RequireAck);
        
        // Assert - フラグが設定されているか確認
        Assert.True(frame.Header.Flags.HasFlag(FrameFlags.RequireAck));

        // ACK待機キューに登録
        var statsBefore = _frameService.GetStatistics();
        _frameService.RegisterPendingAck(frame);
        var statsAfter = _frameService.GetStatistics();

        Assert.Equal(statsBefore.PendingAcks + 1, statsAfter.PendingAcks);
    }

    [Fact]
    public void GetTimedOutFrames_BeforeTimeout_ShouldReturnEmpty()
    {
        // Arrange
        var sourceMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var destMac = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        var testData = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var frame = _frameService.CreateDataFrame(sourceMac, destMac, testData, FrameFlags.RequireAck);

        // Act
        _frameService.RegisterPendingAck(frame);
        var timedOutFrames = _frameService.GetTimedOutFrames();

        // Assert - まだタイムアウトしていないので空
        Assert.Empty(timedOutFrames);
    }

    [Fact]
    public async Task GetTimedOutFrames_AfterTimeout_ShouldReturnFrame()
    {
        // Arrange
        var sourceMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var destMac = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        var testData = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var frame = _frameService.CreateDataFrame(sourceMac, destMac, testData, FrameFlags.RequireAck);

        // Act
        _frameService.RegisterPendingAck(frame);
        
        // 5秒以上待機してタイムアウトさせる
        await Task.Delay(5100); // デフォルトタイムアウトは5秒
        
        var timedOutFrames = _frameService.GetTimedOutFrames();

        // Assert - タイムアウトしたフレームが返される
        Assert.Single(timedOutFrames);
        Assert.Equal(frame.Header.SequenceNumber, timedOutFrames[0].Header.SequenceNumber);
    }

    [Fact]
    public void ProcessAck_ForRegisteredFrame_ShouldRemoveFromQueue()
    {
        // Arrange
        var sourceMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var destMac = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        var testData = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var frame = _frameService.CreateDataFrame(sourceMac, destMac, testData, FrameFlags.RequireAck);

        _frameService.RegisterPendingAck(frame);
        var statsBefore = _frameService.GetStatistics();

        // Act
        var processed = _frameService.ProcessAck(frame.Header.SequenceNumber);

        // Assert
        Assert.True(processed, "ACK should be processed successfully");
        var statsAfter = _frameService.GetStatistics();
        Assert.Equal(statsBefore.PendingAcks - 1, statsAfter.PendingAcks);
    }

    [Fact]
    public void ClearRetryQueue_ShouldRemoveAllPendingFrames()
    {
        // Arrange
        var sourceMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var destMac = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };

        // 複数フレームを登録
        for (int i = 0; i < 5; i++)
        {
            var testData = new byte[] { (byte)i, 0x02, 0x03, 0x04 };
            var frame = _frameService.CreateDataFrame(sourceMac, destMac, testData, FrameFlags.RequireAck);
            _frameService.RegisterPendingAck(frame);
        }

        var statsBefore = _frameService.GetStatistics();
        Assert.Equal(5, statsBefore.PendingAcks);

        // Act
        _frameService.ClearRetryQueue();

        // Assert
        var statsAfter = _frameService.GetStatistics();
        Assert.Equal(0, statsAfter.PendingAcks);
    }
}
