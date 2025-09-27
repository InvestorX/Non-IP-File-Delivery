using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NonIPLoadTest;

class Program
{
    private static int _concurrentConnections = 100;
    private static int _durationMinutes = 30;
    private static int _fileSizeKB = 1024; // 1MB default

    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Non-IP Load Test Tool v1.0.0");
        Console.WriteLine("🔥 負荷テストツール");
        Console.WriteLine();

        try
        {
            ParseArguments(args);

            Console.WriteLine($"📊 負荷テストを開始します");
            Console.WriteLine($"🔗 同時接続数: {_concurrentConnections}");
            Console.WriteLine($"⏱️  テスト時間: {_durationMinutes} 分");
            Console.WriteLine($"📁 ファイルサイズ: {_fileSizeKB} KB");
            Console.WriteLine();

            return await RunLoadTest();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ エラーが発生しました: {ex.Message}");
            return 1;
        }
    }

    private static void ParseArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                case "--concurrent-connections":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int connections))
                    {
                        _concurrentConnections = connections;
                    }
                    break;
                case "--duration-minutes":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int duration))
                    {
                        _durationMinutes = duration;
                    }
                    break;
                case "--file-size-kb":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int fileSize))
                    {
                        _fileSizeKB = fileSize;
                    }
                    break;
                case "--help":
                case "-h":
                    ShowHelp();
                    Environment.Exit(0);
                    break;
            }
        }
    }

    private static void ShowHelp()
    {
        Console.WriteLine("使用方法:");
        Console.WriteLine("  NonIPLoadTest.exe [オプション]");
        Console.WriteLine();
        Console.WriteLine("オプション:");
        Console.WriteLine("  --concurrent-connections <num>  同時接続数 (デフォルト: 100)");
        Console.WriteLine("  --duration-minutes <num>        テスト継続時間 (分, デフォルト: 30)");
        Console.WriteLine("  --file-size-kb <num>            ファイルサイズ (KB, デフォルト: 1024)");
        Console.WriteLine("  --help, -h                      このヘルプを表示");
        Console.WriteLine();
        Console.WriteLine("例:");
        Console.WriteLine("  NonIPLoadTest.exe --concurrent-connections=100 --duration-minutes=30");
        Console.WriteLine("  NonIPLoadTest.exe --concurrent-connections=50 --file-size-kb=2048");
    }

    private static async Task<int> RunLoadTest()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(_durationMinutes));
        var stats = new LoadTestStats();
        var tasks = new List<Task>();

        Console.WriteLine("🚀 負荷テストを開始...");
        Console.WriteLine();

        // Start monitoring task
        var monitorTask = MonitorProgress(stats, cts.Token);
        tasks.Add(monitorTask);

        // Start connection simulation tasks
        for (int i = 0; i < _concurrentConnections; i++)
        {
            var connectionId = i + 1;
            var task = SimulateConnection(connectionId, stats, cts.Token);
            tasks.Add(task);
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Expected when test duration expires
        }

        // Final results
        ShowFinalResults(stats);

        return stats.ErrorCount > stats.TotalRequests * 0.05 ? 1 : 0; // Fail if error rate > 5%
    }

    private static async Task SimulateConnection(int connectionId, LoadTestStats stats, CancellationToken cancellationToken)
    {
        var random = new Random(connectionId);
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var requestStopwatch = Stopwatch.StartNew();
                
                // Simulate file transfer
                await SimulateFileTransfer(random, cancellationToken);
                
                requestStopwatch.Stop();
                
                stats.RecordSuccess(requestStopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                stats.RecordError();
            }
            
            // Small delay between requests
            await Task.Delay(random.Next(100, 1000), cancellationToken);
        }
    }

    private static async Task SimulateFileTransfer(Random random, CancellationToken cancellationToken)
    {
        // Simulate network delay and processing time
        var baseDelay = 50; // Base 50ms
        var variableDelay = random.Next(0, 200); // 0-200ms variable
        var sizeDelay = _fileSizeKB / 100; // Larger files take longer
        
        var totalDelay = baseDelay + variableDelay + sizeDelay;
        
        // Simulate potential errors (5% error rate)
        if (random.NextDouble() < 0.05)
        {
            throw new InvalidOperationException("Simulated network error");
        }
        
        await Task.Delay(totalDelay, cancellationToken);
    }

    private static async Task MonitorProgress(LoadTestStats stats, CancellationToken cancellationToken)
    {
        var lastTotal = 0L;
        var startTime = DateTime.Now;
        
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(10000, cancellationToken); // Update every 10 seconds
            
            var currentTotal = stats.TotalRequests;
            var requestsPerSecond = (currentTotal - lastTotal) / 10.0;
            var elapsed = DateTime.Now - startTime;
            var errorRate = stats.TotalRequests > 0 ? (double)stats.ErrorCount / stats.TotalRequests * 100 : 0;
            
            Console.WriteLine($"⏳ {elapsed.TotalMinutes:F1} 分経過 | " +
                            $"RPS: {requestsPerSecond:F1} | " +
                            $"総リクエスト: {currentTotal:N0} | " +
                            $"エラー率: {errorRate:F1}% | " +
                            $"平均レスポンス: {stats.AverageResponseTime:F0} ms");
            
            lastTotal = currentTotal;
        }
    }

    private static void ShowFinalResults(LoadTestStats stats)
    {
        var errorRate = stats.TotalRequests > 0 ? (double)stats.ErrorCount / stats.TotalRequests * 100 : 0;
        
        Console.WriteLine();
        Console.WriteLine("📋 最終テスト結果:");
        Console.WriteLine($"  総リクエスト数: {stats.TotalRequests:N0}");
        Console.WriteLine($"  成功数: {stats.SuccessCount:N0}");
        Console.WriteLine($"  エラー数: {stats.ErrorCount:N0}");
        Console.WriteLine($"  エラー率: {errorRate:F2}%");
        Console.WriteLine($"  平均レスポンス時間: {stats.AverageResponseTime:F2} ms");
        Console.WriteLine($"  最小レスポンス時間: {stats.MinResponseTime} ms");
        Console.WriteLine($"  最大レスポンス時間: {stats.MaxResponseTime} ms");
        Console.WriteLine();
        
        if (errorRate <= 5.0)
        {
            Console.WriteLine("✅ 負荷テスト成功: エラー率が5%以下です");
        }
        else
        {
            Console.WriteLine("❌ 負荷テスト失敗: エラー率が5%を超えています");
        }
    }
}

public class LoadTestStats
{
    private readonly object _lock = new object();
    private long _totalRequests = 0;
    private long _successCount = 0;
    private long _errorCount = 0;
    private long _totalResponseTime = 0;
    private long _minResponseTime = long.MaxValue;
    private long _maxResponseTime = 0;

    public long TotalRequests => _totalRequests;
    public long SuccessCount => _successCount;
    public long ErrorCount => _errorCount;
    public double AverageResponseTime => _successCount > 0 ? (double)_totalResponseTime / _successCount : 0;
    public long MinResponseTime => _minResponseTime == long.MaxValue ? 0 : _minResponseTime;
    public long MaxResponseTime => _maxResponseTime;

    public void RecordSuccess(long responseTimeMs)
    {
        lock (_lock)
        {
            _totalRequests++;
            _successCount++;
            _totalResponseTime += responseTimeMs;
            
            if (responseTimeMs < _minResponseTime)
                _minResponseTime = responseTimeMs;
            
            if (responseTimeMs > _maxResponseTime)
                _maxResponseTime = responseTimeMs;
        }
    }

    public void RecordError()
    {
        lock (_lock)
        {
            _totalRequests++;
            _errorCount++;
        }
    }
}
