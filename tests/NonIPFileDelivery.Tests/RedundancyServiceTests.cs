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
}
