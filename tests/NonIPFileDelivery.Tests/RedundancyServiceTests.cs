using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;
using Xunit;

namespace NonIPFileDelivery.Tests;

/// <summary>
/// 冗長化サービスのユニットテスト
/// </summary>
public class RedundancyServiceTests
{
    private readonly Mock<ILoggingService> _mockLogger;

    public RedundancyServiceTests()
    {
        _mockLogger = new Mock<ILoggingService>();
    }

    [Fact]
    public async Task StartAsync_ShouldInitializeHeartbeat()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            HeartbeatInterval = 1000,
            FailoverTimeout = 5000,
            PrimaryNode = "192.168.1.10",
            StandbyNode = "192.168.1.11"
        };
        using var service = new RedundancyService(_mockLogger.Object, config);

        // Act
        await service.StartAsync();

        // Assert
        service.GetCurrentState().Should().Be(NodeState.Active);
    }

    [Fact]
    public void GetAllNodes_WithPrimaryAndStandby_ShouldReturnTwoNodes()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10",
            StandbyNode = "192.168.1.11"
        };
        using var service = new RedundancyService(_mockLogger.Object, config);

        // Act
        var nodes = service.GetAllNodes();

        // Assert
        nodes.Should().HaveCount(2);
        nodes.Should().Contain(n => n.NodeId == "primary");
        nodes.Should().Contain(n => n.NodeId == "standby");
    }

    [Fact]
    public void GetAllNodes_WithLoadBalancingNodes_ShouldReturnAllNodes()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            Nodes = new[] { "192.168.1.10", "192.168.1.11", "192.168.1.12" }
        };
        using var service = new RedundancyService(_mockLogger.Object, config);

        // Act
        var nodes = service.GetAllNodes();

        // Assert
        nodes.Should().HaveCount(3);
        nodes.Should().Contain(n => n.NodeId == "node1");
        nodes.Should().Contain(n => n.NodeId == "node2");
        nodes.Should().Contain(n => n.NodeId == "node3");
    }

    [Fact]
    public void GetNodeInfo_WithValidNodeId_ShouldReturnNode()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10",
            StandbyNode = "192.168.1.11"
        };
        using var service = new RedundancyService(_mockLogger.Object, config);

        // Act
        var node = service.GetNodeInfo("primary");

        // Assert
        node.Should().NotBeNull();
        node!.NodeId.Should().Be("primary");
        node.Address.Should().Be("192.168.1.10");
        node.State.Should().Be(NodeState.Active);
    }

    [Fact]
    public void GetNodeInfo_WithInvalidNodeId_ShouldReturnNull()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10"
        };
        using var service = new RedundancyService(_mockLogger.Object, config);

        // Act
        var node = service.GetNodeInfo("nonexistent");

        // Assert
        node.Should().BeNull();
    }

    [Fact]
    public async Task PerformFailoverAsync_WithHealthyStandby_ShouldSucceed()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10",
            StandbyNode = "192.168.1.11"
        };
        using var service = new RedundancyService(_mockLogger.Object, config);

        // Act
        var result = await service.PerformFailoverAsync("Test failover");

        // Assert
        result.Should().BeTrue();
        var nodes = service.GetAllNodes();
        nodes.Should().Contain(n => n.NodeId == "standby" && n.State == NodeState.Active);
        nodes.Should().Contain(n => n.NodeId == "primary" && n.State == NodeState.Failed);
    }

    [Fact]
    public async Task StopAsync_ShouldStopHeartbeat()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10"
        };
        using var service = new RedundancyService(_mockLogger.Object, config);
        await service.StartAsync();

        // Act
        await service.StopAsync();

        // Assert - No exception should be thrown
        service.Should().NotBeNull();
    }

    [Fact]
    public async Task RecordHeartbeatAsync_WithNewNode_ShouldRegisterNode()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10"
        };
        using var service = new RedundancyService(_mockLogger.Object, config);
        var metadata = new Dictionary<string, object>
        {
            ["Priority"] = 100,
            ["Weight"] = 50,
            ["ActiveConnections"] = 5
        };

        // Act
        await service.RecordHeartbeatAsync("new-node", NodeState.Standby, metadata);

        // Assert
        var node = service.GetNodeInfo("new-node");
        node.Should().NotBeNull();
        node!.NodeId.Should().Be("new-node");
        node.State.Should().Be(NodeState.Standby);
        node.Priority.Should().Be(100);
        node.Weight.Should().Be(50);
        node.ActiveConnections.Should().Be(5);
    }

    [Fact]
    public async Task RecordHeartbeatAsync_WithExistingNode_ShouldUpdateHeartbeat()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10",
            StandbyNode = "192.168.1.11"
        };
        using var service = new RedundancyService(_mockLogger.Object, config);
        var node = service.GetNodeInfo("primary");
        var initialHeartbeat = node!.LastHeartbeat;

        // Wait to ensure timestamp changes
        await Task.Delay(10);

        // Act
        await service.RecordHeartbeatAsync("primary", NodeState.Active);

        // Assert
        var updatedNode = service.GetNodeInfo("primary");
        updatedNode!.LastHeartbeat.Should().BeAfter(initialHeartbeat);
    }

    [Fact]
    public async Task RecordHeartbeatAsync_WithMetadata_ShouldUpdateNodeProperties()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10"
        };
        using var service = new RedundancyService(_mockLogger.Object, config);
        var metadata = new Dictionary<string, object>
        {
            ["Priority"] = 200,
            ["Weight"] = 75,
            ["ActiveConnections"] = 10
        };

        // Act
        await service.RecordHeartbeatAsync("primary", NodeState.Active, metadata);

        // Assert
        var node = service.GetNodeInfo("primary");
        node!.Priority.Should().Be(200);
        node.Weight.Should().Be(75);
        node.ActiveConnections.Should().Be(10);
    }

    [Fact]
    public async Task RecordHeartbeatAsync_NodeRecovery_ShouldUpdateHealthStatus()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10",
            StandbyNode = "192.168.1.11"
        };
        using var service = new RedundancyService(_mockLogger.Object, config);

        // Mark primary as failed
        await service.PerformFailoverAsync("Test failure");
        var node = service.GetNodeInfo("primary");
        node!.IsHealthy.Should().BeFalse();
        node.State.Should().Be(NodeState.Failed);

        // Act - Record heartbeat to recover
        await service.RecordHeartbeatAsync("primary", NodeState.Active);

        // Assert
        var recoveredNode = service.GetNodeInfo("primary");
        recoveredNode!.IsHealthy.Should().BeTrue();
        recoveredNode.State.Should().Be(NodeState.Active);
    }

    [Fact]
    public async Task RecordHeartbeatAsync_WithNullMetadata_ShouldNotThrow()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10"
        };
        using var service = new RedundancyService(_mockLogger.Object, config);

        // Act & Assert
        var act = async () => await service.RecordHeartbeatAsync("primary", NodeState.Active, null);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecordHeartbeatAsync_WithInvalidMetadataTypes_ShouldIgnoreInvalidValues()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10"
        };
        using var service = new RedundancyService(_mockLogger.Object, config);
        var initialNode = service.GetNodeInfo("primary");
        var initialPriority = initialNode!.Priority;

        var metadata = new Dictionary<string, object>
        {
            ["Priority"] = "invalid string", // Invalid type
            ["Weight"] = 75,
            ["ActiveConnections"] = 10
        };

        // Act
        await service.RecordHeartbeatAsync("primary", NodeState.Active, metadata);

        // Assert
        var node = service.GetNodeInfo("primary");
        node!.Priority.Should().Be(initialPriority); // Should not change
        node.Weight.Should().Be(75);
        node.ActiveConnections.Should().Be(10);
    }

    [Fact]
    public async Task AutomaticFailover_Manual_ShouldWorkCorrectly()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10",
            StandbyNode = "192.168.1.11"
        };
        using var service = new RedundancyService(_mockLogger.Object, config);
        
        var primary = service.GetNodeInfo("primary");
        var standby = service.GetNodeInfo("standby");
        primary!.State.Should().Be(NodeState.Active);
        standby!.State.Should().Be(NodeState.Standby);

        // Act - Manually perform failover
        var result = await service.PerformFailoverAsync("Test failover");

        // Assert
        result.Should().BeTrue();
        var primaryAfter = service.GetNodeInfo("primary");
        var standbyAfter = service.GetNodeInfo("standby");
        primaryAfter!.State.Should().Be(NodeState.Failed);
        primaryAfter.IsHealthy.Should().BeFalse();
        standbyAfter!.State.Should().Be(NodeState.Active);
    }

    [Fact]
    public async Task AutomaticFailback_WhenConfigured_ShouldBeTriggeredByHeartbeat()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10",
            StandbyNode = "192.168.1.11",
            EnableAutoFailback = true,
            FailbackDelay = 100
        };
        using var service = new RedundancyService(_mockLogger.Object, config);
        
        // Trigger failover
        await service.PerformFailoverAsync("Test failover");
        var primaryFailed = service.GetNodeInfo("primary");
        primaryFailed!.State.Should().Be(NodeState.Failed);
        primaryFailed.IsHealthy.Should().BeFalse();

        // Act - Simulate primary recovery with heartbeat
        await service.RecordHeartbeatAsync("primary", NodeState.Standby);
        
        // Assert - Recovery time should be set
        var primaryRecovering = service.GetNodeInfo("primary");
        primaryRecovering!.RecoveryTime.Should().NotBeNull();
        primaryRecovering.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task AutomaticFailback_WhenDisabled_RecoveryTimeShouldStillBeSet()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10",
            StandbyNode = "192.168.1.11",
            EnableAutoFailback = false
        };
        using var service = new RedundancyService(_mockLogger.Object, config);
        
        // Trigger failover
        await service.PerformFailoverAsync("Test failover");

        // Act - Simulate primary recovery
        await service.RecordHeartbeatAsync("primary", NodeState.Standby);

        // Assert - Recovery time should be set even if auto-failback is disabled
        var primaryRecovered = service.GetNodeInfo("primary");
        primaryRecovered!.RecoveryTime.Should().NotBeNull();
        primaryRecovered.IsHealthy.Should().BeTrue();
        primaryRecovered.State.Should().Be(NodeState.Standby); // State set by RecordHeartbeatAsync
    }

    [Fact]
    public async Task NodeRecoveryTime_ShouldBeSetWhenNodeRecovers()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "192.168.1.10",
            StandbyNode = "192.168.1.11"
        };
        using var service = new RedundancyService(_mockLogger.Object, config);

        // Mark primary as failed
        await service.PerformFailoverAsync("Test failure");
        var primaryFailed = service.GetNodeInfo("primary");
        primaryFailed!.RecoveryTime.Should().BeNull();

        // Act - Simulate recovery
        await service.RecordHeartbeatAsync("primary", NodeState.Standby);

        // Assert
        var primaryRecovered = service.GetNodeInfo("primary");
        primaryRecovered!.RecoveryTime.Should().NotBeNull();
        primaryRecovered.RecoveryTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    #region ハートビートメッセージ通信テスト

    [Fact]
    public void HeartbeatMessage_Serialize_ShouldReturnValidJson()
    {
        // Arrange
        var message = new HeartbeatMessage
        {
            NodeId = "primary",
            SourceMac = "00:11:22:33:44:55",
            State = NodeState.Active,
            Priority = 100,
            ActiveConnections = 5,
            MemoryUsageMB = 512,
            CpuUsagePercent = 25.5
        };

        // Act
        var data = message.Serialize();

        // Assert
        data.Should().NotBeNull();
        data.Length.Should().BeGreaterThan(0);
        
        var json = System.Text.Encoding.UTF8.GetString(data);
        json.Should().Contain("\"Type\":\"HEARTBEAT\"");
        json.Should().Contain("\"NodeId\":\"primary\"");
        json.Should().Contain("\"State\":1"); // NodeState.Active = 1
    }

    [Fact]
    public void HeartbeatMessage_Deserialize_ShouldRestoreOriginalMessage()
    {
        // Arrange
        var original = new HeartbeatMessage
        {
            NodeId = "standby",
            SourceMac = "AA:BB:CC:DD:EE:FF",
            State = NodeState.Standby,
            Priority = 50,
            ActiveConnections = 3,
            MemoryUsageMB = 256,
            CpuUsagePercent = 15.2
        };
        var data = original.Serialize();

        // Act
        var deserialized = HeartbeatMessage.Deserialize(data);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.NodeId.Should().Be("standby");
        deserialized.SourceMac.Should().Be("AA:BB:CC:DD:EE:FF");
        deserialized.State.Should().Be(NodeState.Standby);
        deserialized.Priority.Should().Be(50);
        deserialized.ActiveConnections.Should().Be(3);
        deserialized.MemoryUsageMB.Should().Be(256);
        deserialized.CpuUsagePercent.Should().Be(15.2);
    }

    [Fact]
    public void HeartbeatMessage_IsHeartbeatMessage_WithValidMessage_ShouldReturnTrue()
    {
        // Arrange
        var message = new HeartbeatMessage
        {
            NodeId = "test",
            SourceMac = "00:00:00:00:00:00",
            State = NodeState.Active
        };
        var data = message.Serialize();

        // Act
        var result = HeartbeatMessage.IsHeartbeatMessage(data);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void HeartbeatMessage_IsHeartbeatMessage_WithInvalidData_ShouldReturnFalse()
    {
        // Arrange
        var invalidData = System.Text.Encoding.UTF8.GetBytes("not a heartbeat message");

        // Act
        var result = HeartbeatMessage.IsHeartbeatMessage(invalidData);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void HeartbeatMessage_IsHeartbeatMessage_WithWrongType_ShouldReturnFalse()
    {
        // Arrange
        var wrongMessage = System.Text.Encoding.UTF8.GetBytes("{\"Type\":\"DATA\",\"NodeId\":\"test\"}");

        // Act
        var result = HeartbeatMessage.IsHeartbeatMessage(wrongMessage);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RedundancyService_WithNetworkService_ShouldSubscribeToFrameReceived()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "00:11:22:33:44:55",
            StandbyNode = "AA:BB:CC:DD:EE:FF"
        };
        var mockNetworkService = new Mock<INetworkService>();
        mockNetworkService.Setup(x => x.IsInterfaceReady).Returns(true);

        // Act
        using var service = new RedundancyService(_mockLogger.Object, config, mockNetworkService.Object);
        await service.StartAsync();

        // Assert - FrameReceivedイベントハンドラが登録されているか確認
        mockNetworkService.VerifyAdd(x => x.FrameReceived += It.IsAny<EventHandler<FrameReceivedEventArgs>>(), Times.Once);
    }

    [Fact]
    public async Task SendHeartbeat_WhenNetworkServiceReady_ShouldSendToOtherNodes()
    {
        // Arrange
        var config = new RedundancyConfig
        {
            PrimaryNode = "00:11:22:33:44:55",
            StandbyNode = "AA:BB:CC:DD:EE:FF",
            HeartbeatInterval = 1000
        };
        var mockNetworkService = new Mock<INetworkService>();
        mockNetworkService.Setup(x => x.IsInterfaceReady).Returns(true);
        mockNetworkService.Setup(x => x.SendFrame(It.IsAny<byte[]>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        using var service = new RedundancyService(_mockLogger.Object, config, mockNetworkService.Object);
        await service.StartAsync();

        // Act - Wait for heartbeat timer to trigger (with a small delay)
        await Task.Delay(1500);

        // Assert - Verify SendFrame was called at least once
        mockNetworkService.Verify(
            x => x.SendFrame(It.IsAny<byte[]>(), It.IsAny<string>()), 
            Times.AtLeastOnce
        );
    }

    #endregion
}


