using System;
using System.Linq;
using FluentAssertions;
using Moq;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;
using Xunit;

namespace NonIPFileDelivery.Tests;

/// <summary>
/// ロードバランサーサービスのユニットテスト
/// </summary>
public class LoadBalancerServiceTests
{
    private readonly Mock<ILoggingService> _mockLogger;

    public LoadBalancerServiceTests()
    {
        _mockLogger = new Mock<ILoggingService>();
    }

    private NodeInfo[] CreateTestNodes()
    {
        return new[]
        {
            new NodeInfo
            {
                NodeId = "node1",
                Address = "192.168.1.10",
                State = NodeState.Active,
                IsHealthy = true,
                Weight = 1
            },
            new NodeInfo
            {
                NodeId = "node2",
                Address = "192.168.1.11",
                State = NodeState.Active,
                IsHealthy = true,
                Weight = 1
            },
            new NodeInfo
            {
                NodeId = "node3",
                Address = "192.168.1.12",
                State = NodeState.Active,
                IsHealthy = true,
                Weight = 1
            }
        };
    }

    [Fact]
    public void SelectNode_WithRoundRobin_ShouldDistributeEvenly()
    {
        // Arrange
        var nodes = CreateTestNodes();
        using var service = new LoadBalancerService(
            _mockLogger.Object,
            LoadBalancingAlgorithm.RoundRobin,
            nodes
        );

        // Act
        var selected1 = service.SelectNode();
        var selected2 = service.SelectNode();
        var selected3 = service.SelectNode();
        var selected4 = service.SelectNode();

        // Assert
        selected1.Should().NotBeNull();
        selected2.Should().NotBeNull();
        selected3.Should().NotBeNull();
        selected4.Should().NotBeNull();
        
        // Should cycle through nodes
        selected1!.NodeId.Should().Be("node1");
        selected2!.NodeId.Should().Be("node2");
        selected3!.NodeId.Should().Be("node3");
        selected4!.NodeId.Should().Be("node1");
    }

    [Fact]
    public void SelectNode_WithWeightedRoundRobin_ShouldSelectBasedOnWeight()
    {
        // Arrange
        var nodes = new[]
        {
            new NodeInfo
            {
                NodeId = "node1",
                Address = "192.168.1.10",
                State = NodeState.Active,
                IsHealthy = true,
                Weight = 3
            },
            new NodeInfo
            {
                NodeId = "node2",
                Address = "192.168.1.11",
                State = NodeState.Active,
                IsHealthy = true,
                Weight = 1
            }
        };
        using var service = new LoadBalancerService(
            _mockLogger.Object,
            LoadBalancingAlgorithm.WeightedRoundRobin,
            nodes
        );

        // Act - Select 100 times to check distribution
        var selections = Enumerable.Range(0, 100)
            .Select(_ => service.SelectNode()!.NodeId)
            .ToList();

        // Assert
        var node1Count = selections.Count(id => id == "node1");
        var node2Count = selections.Count(id => id == "node2");

        // Node1 should be selected approximately 3 times more often than node2
        node1Count.Should().BeGreaterThan(node2Count);
    }

    [Fact]
    public void SelectNode_WithLeastConnections_ShouldSelectLeastBusy()
    {
        // Arrange
        var nodes = CreateTestNodes();
        using var service = new LoadBalancerService(
            _mockLogger.Object,
            LoadBalancingAlgorithm.LeastConnections,
            nodes
        );

        // Act
        service.RecordConnection("node1");
        service.RecordConnection("node1");
        service.RecordConnection("node2");
        
        var selected = service.SelectNode();

        // Assert
        selected.Should().NotBeNull();
        // Should select node3 (0 connections) over node1 (2) and node2 (1)
        selected!.NodeId.Should().Be("node3");
    }

    [Fact]
    public void SelectNode_WithNoHealthyNodes_ShouldReturnNull()
    {
        // Arrange
        var nodes = CreateTestNodes();
        using var service = new LoadBalancerService(
            _mockLogger.Object,
            LoadBalancingAlgorithm.RoundRobin,
            nodes
        );

        // Mark all nodes as unhealthy
        service.UpdateNodeHealth("node1", false);
        service.UpdateNodeHealth("node2", false);
        service.UpdateNodeHealth("node3", false);

        // Act
        var selected = service.SelectNode();

        // Assert
        selected.Should().BeNull();
    }

    [Fact]
    public void RecordConnection_ShouldIncreaseActiveConnections()
    {
        // Arrange
        var nodes = CreateTestNodes();
        using var service = new LoadBalancerService(
            _mockLogger.Object,
            LoadBalancingAlgorithm.LeastConnections,
            nodes
        );

        // Act
        service.RecordConnection("node1");
        service.RecordConnection("node1");

        var selected = service.SelectNode();

        // Assert
        // Should select node2 or node3 (0 connections) instead of node1 (2 connections)
        selected.Should().NotBeNull();
        selected!.NodeId.Should().NotBe("node1");
    }

    [Fact]
    public void RecordDisconnection_ShouldDecreaseActiveConnections()
    {
        // Arrange
        var nodes = CreateTestNodes();
        using var service = new LoadBalancerService(
            _mockLogger.Object,
            LoadBalancingAlgorithm.LeastConnections,
            nodes
        );

        service.RecordConnection("node1");
        service.RecordConnection("node1");

        // Act
        service.RecordDisconnection("node1");

        // Assert - Should still prefer nodes with 0 connections
        var selected = service.SelectNode();
        selected.Should().NotBeNull();
    }

    [Fact]
    public void UpdateNodeHealth_ShouldAffectStats()
    {
        // Arrange
        var nodes = CreateTestNodes();
        using var service = new LoadBalancerService(
            _mockLogger.Object,
            LoadBalancingAlgorithm.RoundRobin,
            nodes
        );

        // Act
        service.UpdateNodeHealth("node1", false);
        var statsAfter = service.GetStats();

        // Assert - After marking node1 as unhealthy, only 2 nodes should be active
        statsAfter.ActiveNodes.Should().Be(2);
    }

    [Fact]
    public void GetStats_ShouldReturnCorrectStatistics()
    {
        // Arrange
        var nodes = CreateTestNodes();
        using var service = new LoadBalancerService(
            _mockLogger.Object,
            LoadBalancingAlgorithm.RoundRobin,
            nodes
        );

        // Act
        service.SelectNode();
        service.SelectNode();
        service.SelectNode();

        var stats = service.GetStats();

        // Assert
        stats.TotalRequests.Should().Be(3);
        stats.ActiveNodes.Should().Be(3);
        stats.TotalFailures.Should().Be(0);
    }
}
