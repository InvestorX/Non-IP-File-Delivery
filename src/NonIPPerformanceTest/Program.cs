using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace NonIPPerformanceTest;

class Program
{
    private static string _mode = "throughput";
    private static double _targetGbps = 2.0;
    private static int _maxLatencyMs = 10;
    private static int _durationMinutes = 5;

    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Non-IP Performance Test Tool v1.0.0");
        Console.WriteLine("⚡ パフォーマンステストツール");
        Console.WriteLine();

        try
        {
            ParseArguments(args);

            switch (_mode.ToLower())
            {
                case "throughput":
                    return await RunThroughputTest();
                case "latency":
                    return await RunLatencyTest();
                default:
                    Console.WriteLine($"❌ 不明なテストモード: {_mode}");
                    ShowHelp();
                    return 1;
            }
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
                case "--mode":
                    if (i + 1 < args.Length)
                    {
                        _mode = args[++i];
                    }
                    break;
                case "--target-gbps":
                    if (i + 1 < args.Length && double.TryParse(args[++i], out double gbps))
                    {
                        _targetGbps = gbps;
                    }
                    break;
                case "--max-latency-ms":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int latency))
                    {
                        _maxLatencyMs = latency;
                    }
                    break;
                case "--duration-minutes":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int duration))
                    {
                        _durationMinutes = duration;
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
        Console.WriteLine("  NonIPPerformanceTest.exe --mode=<mode> [オプション]");
        Console.WriteLine();
        Console.WriteLine("モード:");
        Console.WriteLine("  throughput    スループットテスト");
        Console.WriteLine("  latency       レイテンシテスト");
        Console.WriteLine();
        Console.WriteLine("オプション:");
        Console.WriteLine("  --target-gbps <value>      目標スループット (Gbps, デフォルト: 2.0)");
        Console.WriteLine("  --max-latency-ms <value>   最大許容レイテンシ (ms, デフォルト: 10)");
        Console.WriteLine("  --duration-minutes <value> テスト継続時間 (分, デフォルト: 5)");
        Console.WriteLine("  --help, -h                 このヘルプを表示");
        Console.WriteLine();
        Console.WriteLine("例:");
        Console.WriteLine("  NonIPPerformanceTest.exe --mode=throughput --target-gbps=2");
        Console.WriteLine("  NonIPPerformanceTest.exe --mode=latency --max-latency-ms=10");
    }

    private static async Task<int> RunThroughputTest()
    {
        Console.WriteLine($"📊 スループットテストを開始します");
        Console.WriteLine($"🎯 目標スループット: {_targetGbps} Gbps");
        Console.WriteLine($"⏱️  テスト時間: {_durationMinutes} 分");
        Console.WriteLine();

        var stopwatch = Stopwatch.StartNew();
        var testDuration = TimeSpan.FromMinutes(_durationMinutes);
        var random = new Random();
        
        double totalDataTransferred = 0; // GB
        int packetCount = 0;
        
        Console.WriteLine("テスト進行中...");
        
        while (stopwatch.Elapsed < testDuration)
        {
            // Simulate packet transmission
            await Task.Delay(1); // 1ms delay per packet
            
            // Simulate varying packet sizes (1KB to 9KB for jumbo frames)
            double packetSizeKB = random.NextDouble() * 8 + 1;
            totalDataTransferred += packetSizeKB / (1024 * 1024); // Convert to GB
            packetCount++;
            
            // Progress update every 30 seconds
            if (stopwatch.ElapsedMilliseconds % 30000 < 50)
            {
                var elapsedMinutes = stopwatch.Elapsed.TotalMinutes;
                var currentThroughput = (totalDataTransferred * 8) / (stopwatch.Elapsed.TotalSeconds); // Gbps
                Console.WriteLine($"⏳ {elapsedMinutes:F1} 分経過 - 現在のスループット: {currentThroughput:F2} Gbps");
            }
        }
        
        stopwatch.Stop();
        
        var finalThroughput = (totalDataTransferred * 8) / stopwatch.Elapsed.TotalSeconds;
        
        Console.WriteLine();
        Console.WriteLine("📋 テスト結果:");
        Console.WriteLine($"  総転送データ量: {totalDataTransferred:F2} GB");
        Console.WriteLine($"  総パケット数: {packetCount:N0}");
        Console.WriteLine($"  平均スループット: {finalThroughput:F2} Gbps");
        Console.WriteLine($"  テスト時間: {stopwatch.Elapsed.TotalMinutes:F1} 分");
        
        if (finalThroughput >= _targetGbps)
        {
            Console.WriteLine($"✅ テスト成功: 目標スループット {_targetGbps} Gbps を達成しました");
            return 0;
        }
        else
        {
            Console.WriteLine($"❌ テスト失敗: 目標スループット {_targetGbps} Gbps に達しませんでした");
            return 1;
        }
    }

    private static async Task<int> RunLatencyTest()
    {
        Console.WriteLine($"🕒 レイテンシテストを開始します");
        Console.WriteLine($"🎯 最大許容レイテンシ: {_maxLatencyMs} ms");
        Console.WriteLine($"⏱️  テスト時間: {_durationMinutes} 分");
        Console.WriteLine();

        var stopwatch = Stopwatch.StartNew();
        var testDuration = TimeSpan.FromMinutes(_durationMinutes);
        var random = new Random();
        
        var latencies = new List<double>();
        double totalLatency = 0;
        int packetCount = 0;
        int exceededCount = 0;
        
        Console.WriteLine("テスト進行中...");
        
        while (stopwatch.Elapsed < testDuration)
        {
            // Simulate network latency
            var packetStopwatch = Stopwatch.StartNew();
            
            // Simulate packet processing time (0.1ms to 15ms)
            var processingTimeMs = random.NextDouble() * 14.9 + 0.1;
            await Task.Delay(TimeSpan.FromMilliseconds(processingTimeMs));
            
            packetStopwatch.Stop();
            var latencyMs = packetStopwatch.Elapsed.TotalMilliseconds;
            
            latencies.Add(latencyMs);
            totalLatency += latencyMs;
            packetCount++;
            
            if (latencyMs > _maxLatencyMs)
            {
                exceededCount++;
            }
            
            // Progress update every 30 seconds
            if (stopwatch.ElapsedMilliseconds % 30000 < 50)
            {
                var elapsedMinutes = stopwatch.Elapsed.TotalMinutes;
                var avgLatency = totalLatency / packetCount;
                Console.WriteLine($"⏳ {elapsedMinutes:F1} 分経過 - 平均レイテンシ: {avgLatency:F2} ms");
            }
        }
        
        stopwatch.Stop();
        
        var averageLatency = totalLatency / packetCount;
        var maxLatency = latencies.Max();
        var minLatency = latencies.Min();
        var exceededPercentage = (double)exceededCount / packetCount * 100;
        
        Console.WriteLine();
        Console.WriteLine("📋 テスト結果:");
        Console.WriteLine($"  総パケット数: {packetCount:N0}");
        Console.WriteLine($"  平均レイテンシ: {averageLatency:F2} ms");
        Console.WriteLine($"  最小レイテンシ: {minLatency:F2} ms");
        Console.WriteLine($"  最大レイテンシ: {maxLatency:F2} ms");
        Console.WriteLine($"  目標超過率: {exceededPercentage:F1}%");
        Console.WriteLine($"  テスト時間: {stopwatch.Elapsed.TotalMinutes:F1} 分");
        
        if (averageLatency <= _maxLatencyMs && exceededPercentage < 5.0)
        {
            Console.WriteLine($"✅ テスト成功: 平均レイテンシが {_maxLatencyMs} ms 以下で、超過率が5%未満です");
            return 0;
        }
        else
        {
            Console.WriteLine($"❌ テスト失敗: レイテンシ要件を満たしていません");
            return 1;
        }
    }
}
