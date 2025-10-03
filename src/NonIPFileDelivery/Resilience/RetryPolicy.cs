using NonIPFileDelivery.Exceptions;
using NonIPFileDelivery.Services;

namespace NonIPFileDelivery.Resilience;

/// <summary>
/// 指数バックオフ＋ジッターを用いたリトライポリシー
/// </summary>
public class RetryPolicy
{
    private readonly ILoggingService _logger;
    private readonly int _maxRetryAttempts;
    private readonly int _initialDelayMs;
    private readonly int _maxDelayMs;
    private readonly Random _random = new();

    public RetryPolicy(
        ILoggingService logger,
        int maxRetryAttempts = 3,
        int initialDelayMs = 100,
        int maxDelayMs = 5000)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxRetryAttempts = maxRetryAttempts;
        _initialDelayMs = initialDelayMs;
        _maxDelayMs = maxDelayMs;
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<Task<TResult>> operation,
        string operationName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        int attempt = 0;
        Exception? lastException = null;

        while (attempt < _maxRetryAttempts)
        {
            try
            {
                attempt++;
                if (attempt > 1)
                {
                    _logger.Debug($"Retry attempt {attempt}/{_maxRetryAttempts} for {operationName}");
                }
                return await operation();
            }
            catch (Exception ex) when (attempt < _maxRetryAttempts && IsTransientError(ex))
            {
                lastException = ex;

                // 指数バックオフ（2^(attempt-1)）＋±20%ジッター
                var baseDelay = Math.Min(_initialDelayMs * Math.Pow(2, attempt - 1), _maxDelayMs);
                var jitter = baseDelay * 0.2 * (_random.NextDouble() * 2 - 1);
                var delay = TimeSpan.FromMilliseconds(baseDelay + jitter);

                _logger.Warning($"{operationName} failed (attempt {attempt}/{_maxRetryAttempts}), retrying after {delay.TotalMilliseconds:F0}ms. Error: {ex.Message}");
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    _logger.Info($"{operationName} cancelled during retry delay");
                    throw;
                }
            }
            catch (Exception ex) when (!IsTransientError(ex))
            {
                _logger.Error($"{operationName} failed with non-transient error on attempt {attempt}", ex);
                throw;
            }
        }

        _logger.Error($"{operationName} failed after {_maxRetryAttempts} attempts", lastException);
        throw new InvalidOperationException(
            $"Operation '{operationName}' failed after {_maxRetryAttempts} retry attempts.",
            lastException);
    }

    public async Task ExecuteAsync(
        Func<Task> operation,
        string operationName,
        CancellationToken ct = default)
    {
        await ExecuteAsync(async () => { await operation(); return true; }, operationName, ct);
    }

    private static bool IsTransientError(Exception ex)
    {
        return ex is NetworkException
            || ex is TimeoutException
            || ex is IOException
            || ex is OperationCanceledException
            || (ex is TransceiverException te &&
                (te.ErrorCode.StartsWith("NET_", StringComparison.Ordinal) ||
                 te.ErrorCode == "TIMEOUT"));
    }
}
