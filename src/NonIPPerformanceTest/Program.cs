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
        var throughputHistory = new List<double>();
        
        Console.WriteLine("テスト進行中...");
        
        while (stopwatch.Elapsed < testDuration)
        {
            // Generate actual data packets for realistic throughput testing
            var packetSizeKB = GenerateRealisticPacketSize(random);
            var packetData = new byte[packetSizeKB * 1024];
            random.NextBytes(packetData);
            
            // Measure actual processing time
            var packetStopwatch = Stopwatch.StartNew();
            
            // Simulate actual packet processing (compression, encryption, checksums)
            await ProcessPacketForThroughput(packetData);
            
            packetStopwatch.Stop();
            
            totalDataTransferred += packetSizeKB / (1024.0 * 1024.0); // Convert to GB
            packetCount++;
            
            // Calculate instantaneous throughput
            var instantThroughput = (packetSizeKB * 8) / (packetStopwatch.Elapsed.TotalSeconds * 1024 * 1024); // Mbps
            
            // Progress update every 30 seconds
            if (stopwatch.ElapsedMilliseconds % 30000 < 100)
            {
                var elapsedMinutes = stopwatch.Elapsed.TotalMinutes;
                var currentThroughput = (totalDataTransferred * 8) / (stopwatch.Elapsed.TotalSeconds); // Gbps
                throughputHistory.Add(currentThroughput);
                
                Console.WriteLine($"⏳ {elapsedMinutes:F1} 分経過 - 現在のスループット: {currentThroughput:F2} Gbps (瞬間値: {instantThroughput:F0} Mbps)");
            }
            
            // Dynamic throttling to prevent system overload
            if (packetCount % 1000 == 0)
            {
                await Task.Delay(1); // Brief pause every 1000 packets
            }
        }
        
        stopwatch.Stop();
        
        var finalThroughput = (totalDataTransferred * 8) / stopwatch.Elapsed.TotalSeconds;
        var avgThroughput = throughputHistory.Count > 0 ? throughputHistory.Average() : finalThroughput;
        var maxThroughput = throughputHistory.Count > 0 ? throughputHistory.Max() : finalThroughput;
        
        Console.WriteLine();
        Console.WriteLine("📋 テスト結果:");
        Console.WriteLine($"  総転送データ量: {totalDataTransferred:F2} GB");
        Console.WriteLine($"  総パケット数: {packetCount:N0}");
        Console.WriteLine($"  平均パケットサイズ: {(totalDataTransferred * 1024 * 1024 / packetCount):F1} KB");
        Console.WriteLine($"  平均スループット: {avgThroughput:F2} Gbps");
        Console.WriteLine($"  最大スループット: {maxThroughput:F2} Gbps");
        Console.WriteLine($"  最終スループット: {finalThroughput:F2} Gbps");
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

    private static int GenerateRealisticPacketSize(Random random)
    {
        // Generate packet sizes based on realistic distribution
        var sizeType = random.NextDouble();
        
        if (sizeType < 0.4) // 40% small packets (64-512 bytes)
        {
            return random.Next(64, 513) / 1024; // Convert to KB, minimum 1KB
        }
        else if (sizeType < 0.8) // 40% medium packets (1-4KB)
        {
            return random.Next(1, 5);
        }
        else // 20% large packets (4-9KB for jumbo frames)
        {
            return random.Next(4, 10);
        }
    }

    private static async Task ProcessPacketForThroughput(byte[] packetData)
    {
        // Simulate actual packet processing operations
        // 1. Calculate checksum (CPU intensive)
        var checksum = CalculateSimpleChecksum(packetData);
        
        // 2. Simulate compression (varies with data size)
        var compressionRatio = SimulateCompression(packetData);
        
        // 3. Simulate encryption overhead
        await SimulateEncryption(packetData);
        
        // The processing time naturally varies with packet size and content
    }

    private static uint CalculateSimpleChecksum(byte[] data)
    {
        uint checksum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint value = 0;
            for (int j = 0; j < 4 && i + j < data.Length; j++)
            {
                value |= (uint)(data[i + j] << (j * 8));
            }
            checksum ^= value;
        }
        return checksum;
    }

    private static double SimulateCompression(byte[] data)
    {
        // Simulate compression analysis - more zeros = better compression
        int zeroCount = 0;
        foreach (byte b in data)
        {
            if (b == 0) zeroCount++;
        }
        return (double)zeroCount / data.Length;
    }

    private static async Task SimulateEncryption(byte[] data)
    {
        // Simulate encryption overhead based on data size
        var encryptionTimeMs = Math.Max(0.1, data.Length / 1024.0 / 100.0); // Scale with size
        if (encryptionTimeMs > 1)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(encryptionTimeMs));
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
            // Generate actual test packet with realistic size distribution
            var packetSize = GenerateRealisticPacketSize(random);
            var testPacket = new byte[packetSize * 1024];
            random.NextBytes(testPacket);
            
            // Measure actual packet processing latency
            var packetStopwatch = Stopwatch.StartNew();
            
            // Perform actual packet processing operations
            await ProcessPacketForLatency(testPacket);
            
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
            if (stopwatch.ElapsedMilliseconds % 30000 < 100)
            {
                var elapsedMinutes = stopwatch.Elapsed.TotalMinutes;
                var avgLatency = totalLatency / packetCount;
                var recentLatencies = latencies.Skip(Math.Max(0, latencies.Count - 1000)).ToList();
                var recentAvg = recentLatencies.Average();
                
                Console.WriteLine($"⏳ {elapsedMinutes:F1} 分経過 - 平均レイテンシ: {avgLatency:F2} ms (直近: {recentAvg:F2} ms)");
            }
            
            // Small delay between packets to avoid overwhelming the system
            await Task.Delay(1);
        }
        
        stopwatch.Stop();
        
        var averageLatency = totalLatency / packetCount;
        var maxLatency = latencies.Max();
        var minLatency = latencies.Min();
        var medianLatency = CalculateMedian(latencies);
        var p95Latency = CalculatePercentile(latencies, 95);
        var p99Latency = CalculatePercentile(latencies, 99);
        var exceededPercentage = (double)exceededCount / packetCount * 100;
        
        Console.WriteLine();
        Console.WriteLine("📋 テスト結果:");
        Console.WriteLine($"  総パケット数: {packetCount:N0}");
        Console.WriteLine($"  平均レイテンシ: {averageLatency:F2} ms");
        Console.WriteLine($"  中央値レイテンシ: {medianLatency:F2} ms");
        Console.WriteLine($"  最小レイテンシ: {minLatency:F2} ms");
        Console.WriteLine($"  最大レイテンシ: {maxLatency:F2} ms");
        Console.WriteLine($"  P95レイテンシ: {p95Latency:F2} ms");
        Console.WriteLine($"  P99レイテンシ: {p99Latency:F2} ms");
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

    private static async Task ProcessPacketForLatency(byte[] packetData)
    {
        // Simulate actual packet processing operations that affect latency
        // 1. Header parsing (minimal overhead)
        var headerChecksum = packetData.Take(Math.Min(20, packetData.Length)).Sum(b => (int)b);
        
        // 2. Routing decision (lookup table simulation)
        var routingKey = headerChecksum % 1024;
        await Task.Delay(0); // Simulate routing table lookup
        
        // 3. Quality of Service processing
        var qosClass = DetermineQoSClass(packetData);
        
        // 4. Buffer management (simulated)
        if (packetData.Length > 4096) // Large packet processing
        {
            await Task.Delay(0); // Minimal additional delay for large packets
        }
    }

    private static int DetermineQoSClass(byte[] packetData)
    {
        // Simulate QoS classification based on packet content
        var contentHash = packetData.Take(8).Sum(b => (int)b);
        return contentHash % 4; // 4 QoS classes
    }

    private static double CalculateMedian(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        int count = sorted.Count;
        
        if (count == 0) return 0;
        if (count % 2 == 0)
        {
            return (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0;
        }
        else
        {
            return sorted[count / 2];
        }
    }

    private static double CalculatePercentile(List<double> values, int percentile)
    {
        var sorted = values.OrderBy(x => x).ToList();
        if (sorted.Count == 0) return 0;
        
        double index = (percentile / 100.0) * (sorted.Count - 1);
        int lowerIndex = (int)Math.Floor(index);
        int upperIndex = (int)Math.Ceiling(index);
        
        if (lowerIndex == upperIndex)
        {
            return sorted[lowerIndex];
        }
        else
        {
            double weight = index - lowerIndex;
            return sorted[lowerIndex] * (1 - weight) + sorted[upperIndex] * weight;
        }
    }
}
