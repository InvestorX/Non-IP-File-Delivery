using System;
using System.Threading;
using System.Threading.Tasks;
using NonIPFileDelivery.Exceptions;

namespace NonIPFileDelivery.Resilience;

/// <summary>
/// 指数バックオフを使用したリトライポリシー
/// 一時的なエラーに対して自動的にリトライを実行
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

    /// <summary>
    /// リトライポリシーを適用して操作を実行
    /// </summary>
    /// <typeparam name="TResult">操作の戻り値の型</typeparam>
    /// <param name="operation">実行する操作</param>
    /// <param name="operationName">操作名（ログ記録用）</param>
    /// <param name="ct">キャンセルトークン</param>
    /// <returns>操作の結果</returns>
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<Task<TResult>> operation,
        string operationName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        int attempt = 0;
        TimeSpan delay = TimeSpan.FromMilliseconds(InitialDelayMs);

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
                
                // 指数バックオフ + ジッター（ランダム性）
                var baseDelay = Math.Min(
                    _initialDelayMs * Math.Pow(2, attempt - 1),
                    _maxDelayMs);
                
                // ±20%のランダムなジッターを追加（サンダリングハード問題を回避）
                var jitter = baseDelay * 0.2 * (_random.NextDouble() * 2 - 1);
                var delay = TimeSpan.FromMilliseconds(baseDelay + jitter);
                
                _logger.Warning(
                     $"{operationName} failed (attempt {attempt}/{_maxRetryAttempts}), " +
                    $"retrying after {delay.TotalMilliseconds:F0}ms. Error: {ex.Message}");

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
                // 非一時的エラーは即座に失敗
                _logger.Error($"{operationName} failed with non-transient error on attempt {attempt}", ex);
                throw;
            }
        }

        // すべてのリトライが失敗
        _logger.Error($"{operationName} failed after {_maxRetryAttempts} attempts", lastException);
        
        throw new InvalidOperationException(
            $"Operation '{operationName}' failed after {_maxRetryAttempts} retry attempts. " +
            $"See inner exception for details.",
            lastException);
        
    }

    /// <summary>
    /// リトライポリシーを適用して操作を実行（戻り値なし）
    /// </summary>
    public async Task ExecuteAsync(
        Func<Task> operation,
        string operationName,
        CancellationToken ct = default)
    {
        await ExecuteAsync(async () =>
        {
            await operation();
            return true; // ダミーの戻り値
        }, operationName, ct);
    }

    /// <summary>
    /// エラーが一時的なものかどうかを判定
    /// </summary>
    /// <param name="ex">例外オブジェクト</param>
    /// <returns>一時的なエラーの場合true</returns>
    private bool IsTransientError(Exception ex)
    {        
        // 一時的なエラーのパターン
        return ex is NetworkException ||
               ex is TimeoutException ||
               ex is IOException ||
               ex is OperationCanceledException ||
               (ex is TransceiverException te && 
                (te.ErrorCode.StartsWith("NET_") || 
                 te.ErrorCode == "TIMEOUT"));
    }
}
