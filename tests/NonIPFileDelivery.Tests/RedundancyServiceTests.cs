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
}

