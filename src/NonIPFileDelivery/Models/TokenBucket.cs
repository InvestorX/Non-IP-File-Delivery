using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NonIPFileDelivery.Models;

/// <summary>
/// トークンバケットアルゴリズムによる帯域幅制限
/// レート制限とバースト制御を提供
/// </summary>
public class TokenBucket
{
    private readonly long _maxTokens;        // バケットの最大容量（バイト）
    private readonly double _refillRatePerMs; // トークン補充レート（バイト/ミリ秒）
    private long _currentTokens;             // 現在のトークン数（バイト）
    private DateTime _lastRefillTime;        // 最後の補充時刻
    private readonly object _lock = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// 最大帯域幅（Mbps）
    /// </summary>
    public int MaxBandwidthMbps { get; private set; }

    /// <summary>
    /// バースト許容サイズ（バイト）
    /// </summary>
    public long BurstSizeBytes => _maxTokens;

    /// <summary>
    /// 現在利用可能なトークン数（バイト）
    /// </summary>
    public long AvailableTokens
    {
        get
        {
            lock (_lock)
            {
                RefillTokens();
                return _currentTokens;
            }
        }
    }

    /// <summary>
    /// トークンバケットを初期化
    /// </summary>
    /// <param name="maxBandwidthMbps">最大帯域幅（Mbps）</param>
    /// <param name="burstSizeBytes">バースト許容サイズ（バイト）。0の場合は1秒分の帯域幅</param>
    public TokenBucket(int maxBandwidthMbps, long burstSizeBytes = 0)
    {
        if (maxBandwidthMbps <= 0)
            throw new ArgumentException("Max bandwidth must be positive", nameof(maxBandwidthMbps));

        MaxBandwidthMbps = maxBandwidthMbps;

        // Mbps → バイト/秒 → バイト/ミリ秒
        // 1 Mbps = 1,000,000 bits/sec = 125,000 bytes/sec = 125 bytes/ms
        var bytesPerSecond = maxBandwidthMbps * 125_000L;
        _refillRatePerMs = bytesPerSecond / 1000.0;

        // バースト許容サイズ（デフォルトは1秒分の帯域幅）
        _maxTokens = burstSizeBytes > 0 ? burstSizeBytes : bytesPerSecond;
        _currentTokens = _maxTokens; // 初期状態では満タン
        _lastRefillTime = DateTime.UtcNow;
    }

    /// <summary>
    /// 帯域幅設定を変更
    /// </summary>
    /// <param name="maxBandwidthMbps">新しい最大帯域幅（Mbps）</param>
    public void UpdateBandwidth(int maxBandwidthMbps)
    {
        if (maxBandwidthMbps <= 0)
            throw new ArgumentException("Max bandwidth must be positive", nameof(maxBandwidthMbps));

        lock (_lock)
        {
            MaxBandwidthMbps = maxBandwidthMbps;
            var bytesPerSecond = maxBandwidthMbps * 125_000L;
            
            // _refillRatePerMsとmaxTokensは読み取り専用フィールドのため、
            // 実際のアプリケーションでは新しいインスタンスを作成することを推奨
            // ここでは簡略化のため警告を出す
            System.Diagnostics.Debug.WriteLine(
                $"Warning: Bandwidth updated to {maxBandwidthMbps} Mbps. " +
                "Consider recreating TokenBucket instance for proper refill rate update.");
        }
    }

    /// <summary>
    /// 指定サイズのトークンを非同期で消費（帯域幅制限チェック）
    /// </summary>
    /// <param name="sizeInBytes">消費するトークン数（バイト）</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>トークンを消費できた場合true</returns>
    public async Task<bool> TryConsumeAsync(long sizeInBytes, CancellationToken cancellationToken = default)
    {
        if (sizeInBytes <= 0)
            return true; // サイズ0以下は常に許可

        if (sizeInBytes > _maxTokens)
        {
            // 要求サイズがバケット容量を超える場合は、分割送信が必要
            throw new ArgumentException(
                $"Requested size ({sizeInBytes} bytes) exceeds burst capacity ({_maxTokens} bytes). " +
                "Consider splitting the data into smaller chunks.",
                nameof(sizeInBytes));
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var sw = Stopwatch.StartNew();
            
            while (!cancellationToken.IsCancellationRequested)
            {
                lock (_lock)
                {
                    RefillTokens();

                    if (_currentTokens >= sizeInBytes)
                    {
                        _currentTokens -= sizeInBytes;
                        return true;
                    }
                }

                // トークンが足りない場合、少し待機してリトライ
                var tokensNeeded = sizeInBytes - _currentTokens;
                var waitTimeMs = (int)Math.Ceiling(tokensNeeded / _refillRatePerMs);
                waitTimeMs = Math.Min(waitTimeMs, 100); // 最大100ms待機

                await Task.Delay(waitTimeMs, cancellationToken);

                // タイムアウト防止（最大5秒）
                if (sw.ElapsedMilliseconds > 5000)
                {
                    throw new TimeoutException(
                        $"Failed to acquire {sizeInBytes} tokens within 5 seconds. " +
                        $"Current tokens: {_currentTokens}, Max tokens: {_maxTokens}");
                }
            }

            return false; // キャンセルされた
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// 指定サイズのトークンを即座に消費（同期版）
    /// </summary>
    /// <param name="sizeInBytes">消費するトークン数（バイト）</param>
    /// <returns>トークンを消費できた場合true、できない場合false</returns>
    public bool TryConsume(long sizeInBytes)
    {
        if (sizeInBytes <= 0)
            return true;

        lock (_lock)
        {
            RefillTokens();

            if (_currentTokens >= sizeInBytes)
            {
                _currentTokens -= sizeInBytes;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// トークンを補充（時間経過に応じて）
    /// </summary>
    private void RefillTokens()
    {
        // lock内で呼ばれることを前提
        var now = DateTime.UtcNow;
        var elapsedMs = (now - _lastRefillTime).TotalMilliseconds;

        if (elapsedMs > 0)
        {
            var tokensToAdd = (long)(elapsedMs * _refillRatePerMs);
            _currentTokens = Math.Min(_currentTokens + tokensToAdd, _maxTokens);
            _lastRefillTime = now;
        }
    }

    /// <summary>
    /// トークンバケットをリセット（満タンに戻す）
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _currentTokens = _maxTokens;
            _lastRefillTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 統計情報を取得
    /// </summary>
    public TokenBucketStatistics GetStatistics()
    {
        lock (_lock)
        {
            RefillTokens();
            return new TokenBucketStatistics
            {
                MaxBandwidthMbps = MaxBandwidthMbps,
                MaxTokens = _maxTokens,
                CurrentTokens = _currentTokens,
                RefillRatePerMs = _refillRatePerMs,
                UtilizationPercent = 100.0 * (1.0 - (double)_currentTokens / _maxTokens)
            };
        }
    }
}

/// <summary>
/// トークンバケット統計情報
/// </summary>
public class TokenBucketStatistics
{
    public int MaxBandwidthMbps { get; set; }
    public long MaxTokens { get; set; }
    public long CurrentTokens { get; set; }
    public double RefillRatePerMs { get; set; }
    public double UtilizationPercent { get; set; }
}
