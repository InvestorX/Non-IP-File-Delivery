using Xunit;
using NonIPFileDelivery.Models;
using NonIpFileDelivery.Core;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace NonIPFileDelivery.Tests;

/// <summary>
/// QoS (Quality of Service) 統合テスト
/// </summary>
public class QoSIntegrationTests
{
    [Fact]
    public void QoSFrameQueue_ShouldPrioritizeHighPriorityFrames()
    {
        // Arrange
        var queue = new QoSFrameQueue();

        var normalFrame = new SecureFrame
        {
            SessionId = Guid.NewGuid(),
            Protocol = SecureFrame.ProtocolType.FtpData,
            Payload = new byte[] { 1, 2, 3 },
            Flags = SecureFrame.FrameFlags.None
        };

        var highPriorityFrame = new SecureFrame
        {
            SessionId = Guid.NewGuid(),
            Protocol = SecureFrame.ProtocolType.FtpData,
            Payload = new byte[] { 4, 5, 6 },
            Flags = SecureFrame.FrameFlags.HighPriority
        };

        // Act
        queue.Enqueue(normalFrame);
        queue.Enqueue(highPriorityFrame);

        var firstFrame = queue.DequeueAsync(CancellationToken.None).Result;
        var secondFrame = queue.DequeueAsync(CancellationToken.None).Result;

        // Assert
        Assert.Equal(highPriorityFrame.SessionId, firstFrame.SessionId);
        Assert.Equal(normalFrame.SessionId, secondFrame.SessionId);
        Assert.Equal(2, queue.TotalDequeued);
    }

    [Fact]
    public void QoSFrameQueue_ShouldPrioritizeHeartbeatFrames()
    {
        // Arrange
        var queue = new QoSFrameQueue();

        var dataFrame = new SecureFrame
        {
            SessionId = Guid.NewGuid(),
            Protocol = SecureFrame.ProtocolType.FtpData,
            Payload = new byte[] { 1, 2, 3 },
            Flags = SecureFrame.FrameFlags.None
        };

        var heartbeatFrame = new SecureFrame
        {
            SessionId = Guid.NewGuid(),
            Protocol = SecureFrame.ProtocolType.Heartbeat,
            Payload = new byte[] { },
            Flags = SecureFrame.FrameFlags.None
        };

        // Act
        queue.Enqueue(dataFrame);
        queue.Enqueue(heartbeatFrame);

        var firstFrame = queue.DequeueAsync(CancellationToken.None).Result;
        var secondFrame = queue.DequeueAsync(CancellationToken.None).Result;

        // Assert
        Assert.Equal(heartbeatFrame.SessionId, firstFrame.SessionId);
        Assert.Equal(dataFrame.SessionId, secondFrame.SessionId);
    }

    [Fact]
    public void QoSFrameQueue_ShouldPrioritizeRequireAckFrames()
    {
        // Arrange
        var queue = new QoSFrameQueue();

        var normalFrame = new SecureFrame
        {
            SessionId = Guid.NewGuid(),
            Protocol = SecureFrame.ProtocolType.FtpData,
            Payload = new byte[] { 1, 2, 3 },
            Flags = SecureFrame.FrameFlags.None
        };

        var ackFrame = new SecureFrame
        {
            SessionId = Guid.NewGuid(),
            Protocol = SecureFrame.ProtocolType.FtpData,
            Payload = new byte[] { 4, 5, 6 },
            Flags = SecureFrame.FrameFlags.RequireAck
        };

        // Act
        queue.Enqueue(normalFrame);
        queue.Enqueue(ackFrame);

        var firstFrame = queue.DequeueAsync(CancellationToken.None).Result;
        var secondFrame = queue.DequeueAsync(CancellationToken.None).Result;

        // Assert
        Assert.Equal(ackFrame.SessionId, firstFrame.SessionId);
        Assert.Equal(normalFrame.SessionId, secondFrame.SessionId);
    }

    [Fact]
    public void QoSFrameQueue_ShouldHandleMultiplePriorityLevels()
    {
        // Arrange
        var queue = new QoSFrameQueue();

        var lowFrame = new SecureFrame
        {
            SessionId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Protocol = SecureFrame.ProtocolType.FtpControl,
            Payload = new byte[] { 1 },
            Flags = SecureFrame.FrameFlags.None
        };

        var normalFrame = new SecureFrame
        {
            SessionId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Protocol = SecureFrame.ProtocolType.FtpData,
            Payload = new byte[] { 2 },
            Flags = SecureFrame.FrameFlags.None
        };

        var highFrame = new SecureFrame
        {
            SessionId = Guid.Parse("00000000-0000-0000-0000-000000000003"),
            Protocol = SecureFrame.ProtocolType.Heartbeat,
            Payload = new byte[] { 3 },
            Flags = SecureFrame.FrameFlags.None
        };

        // Act - 逆順にエンキュー
        queue.Enqueue(lowFrame);
        queue.Enqueue(normalFrame);
        queue.Enqueue(highFrame);

        var frame1 = queue.DequeueAsync(CancellationToken.None).Result;
        var frame2 = queue.DequeueAsync(CancellationToken.None).Result;
        var frame3 = queue.DequeueAsync(CancellationToken.None).Result;

        // Assert - 優先度順にデキュー
        Assert.Equal(highFrame.SessionId, frame1.SessionId);
        Assert.Equal(normalFrame.SessionId, frame2.SessionId);
        Assert.Equal(lowFrame.SessionId, frame3.SessionId);

        Assert.Equal(1, queue.HighPriorityCount);
        Assert.Equal(1, queue.NormalPriorityCount);
        Assert.Equal(1, queue.LowPriorityCount);
    }

    [Fact]
    public void QoSFrameQueue_ShouldTrackStatistics()
    {
        // Arrange
        var queue = new QoSFrameQueue();

        // Act
        for (int i = 0; i < 10; i++)
        {
            var frame = new SecureFrame
            {
                SessionId = Guid.NewGuid(),
                Protocol = i % 2 == 0 ? SecureFrame.ProtocolType.Heartbeat : SecureFrame.ProtocolType.FtpData,
                Payload = new byte[] { (byte)i },
                Flags = SecureFrame.FrameFlags.None
            };
            queue.Enqueue(frame);
        }

        // Assert
        Assert.Equal(10, queue.TotalEnqueued);
        Assert.Equal(5, queue.HighPriorityCount); // Heartbeat frames
        Assert.Equal(5, queue.NormalPriorityCount); // FtpData frames
        Assert.Equal(10, queue.CurrentQueueSize);

        // Dequeue all
        for (int i = 0; i < 10; i++)
        {
            _ = queue.DequeueAsync(CancellationToken.None).Result;
        }

        Assert.Equal(10, queue.TotalDequeued);
        Assert.Equal(0, queue.CurrentQueueSize);
    }
}
