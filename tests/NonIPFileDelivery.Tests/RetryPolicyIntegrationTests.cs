using Xunit;
using NonIPFileDelivery.Resilience;
using NonIPFileDelivery.Services;
using NonIPFileDelivery.Exceptions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NonIPFileDelivery.Tests;

/// <summary>
/// RetryPolicyの統合テスト
/// </summary>
public class RetryPolicyIntegrationTests
{
    [Fact]
    public async Task RetryPolicy_ShouldSucceedOnThirdAttempt()
    {
        // Arrange
        var logger = new LoggingService();
        var retryPolicy = new RetryPolicy(logger, maxRetryAttempts: 3, initialDelayMs: 10, maxDelayMs: 100);
        int attemptCount = 0;

        // Act
        var result = await retryPolicy.ExecuteAsync(async () =>
        {
            attemptCount++;
            if (attemptCount < 3)
            {
                throw new TransceiverException("NET_SEND_ERROR", "Transient network error");
            }
            await Task.CompletedTask;
            return "Success";
        }, "TestOperation", CancellationToken.None);

        // Assert
        Assert.Equal("Success", result);
        Assert.Equal(3, attemptCount);
    }

    [Fact]
    public async Task RetryPolicy_ShouldFailAfterMaxRetries()
    {
        // Arrange
        var logger = new LoggingService();
        var retryPolicy = new RetryPolicy(logger, maxRetryAttempts: 3, initialDelayMs: 10, maxDelayMs: 100);
        int attemptCount = 0;

        // Act & Assert
        await Assert.ThrowsAsync<TransceiverException>(async () =>
        {
            await retryPolicy.ExecuteAsync(async () =>
            {
                attemptCount++;
                await Task.CompletedTask;
                throw new TransceiverException("NET_CONNECTION_FAILED", "Persistent network error");
            }, "TestOperation", CancellationToken.None);
        });

        Assert.Equal(3, attemptCount);
    }

    [Fact]
    public async Task RetryPolicy_ShouldSucceedOnFirstAttempt()
    {
        // Arrange
        var logger = new LoggingService();
        var retryPolicy = new RetryPolicy(logger, maxRetryAttempts: 3, initialDelayMs: 10, maxDelayMs: 100);
        int attemptCount = 0;

        // Act
        var result = await retryPolicy.ExecuteAsync(async () =>
        {
            attemptCount++;
            await Task.CompletedTask;
            return "Success";
        }, "TestOperation", CancellationToken.None);

        // Assert
        Assert.Equal("Success", result);
        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task RetryPolicy_ShouldApplyExponentialBackoff()
    {
        // Arrange
        var logger = new LoggingService();
        var retryPolicy = new RetryPolicy(logger, maxRetryAttempts: 3, initialDelayMs: 100, maxDelayMs: 5000);
        var startTime = DateTime.UtcNow;
        int attemptCount = 0;

        // Act
        try
        {
            await retryPolicy.ExecuteAsync(async () =>
            {
                attemptCount++;
                await Task.CompletedTask;
                throw new TransceiverException("NET_TIMEOUT", "Transient network timeout");
            }, "TestOperation", CancellationToken.None);
        }
        catch (TransceiverException)
        {
            // Expected
        }

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

        // Assert
        Assert.Equal(3, attemptCount);
        // 最小でも初回100ms + 2回目200ms = 300ms程度かかるはず（ジッターがあるので少し余裕を持たせる）
        Assert.True(elapsed >= 250, $"Elapsed time {elapsed}ms should be >= 250ms for exponential backoff");
    }
}
