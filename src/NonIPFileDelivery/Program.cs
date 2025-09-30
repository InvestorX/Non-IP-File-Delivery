using System;
using System.IO;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;

namespace NonIPFileDelivery;

class Program
{
    private static string _configPath = "config.ini";
    private static bool _debugMode = false;
    private static string _logLevel = "Warning";
    
    // サービスインスタンス
    private static ILoggingService? _logger;
    private static NonIPFileDeliveryService? _mainService;
    private static PacketProcessingPipeline? _pipeline; // 新規追加
    private static RetryPolicy? _retryPolicy; // 新規追加

    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Non-IP File Delivery v1.1.0");
        Console.WriteLine("🛡️ ハッカー・クラッカー・ランサムウェア対策のためのRaw Ethernet非IPファイル転送システム"); 
        Console.WriteLine("✨ CRC32チェックサム、構造化ログ、TPL Dataflowパイプライン対応");
        Console.WriteLine();

        try
        {
            ParseArguments(args);
            
            // ロギングサービス初期化（Serilog統合版）
            _logger = new LoggingService();
            SetupLogging();

            _logger.Info("Starting Non-IP File Delivery Service");
            
            // リトライポリシー初期化
            _retryPolicy = new RetryPolicy(_logger);
            
            // Load configuration
            var configService = new ConfigurationService();
            Configuration configuration;
            
            if (!File.Exists(_configPath))
            {
                _logger.Warning($"Configuration file not found: {_configPath}");
                _logger.Info("Creating default configuration file...");
                // 拡張子に応じてデフォルト設定を作成
                if (_configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    configService.CreateDefaultJsonConfiguration(_configPath);
                }
                else
                {
                    configService.CreateDefaultConfiguration(_configPath);
                }
            }

            try
            {
                // 設定読み込みにリトライポリシーを適用
                configuration = await _retryPolicy.ExecuteAsync(
                    async () => await Task.Run(() => configService.LoadConfiguration(_configPath)),
                    "LoadConfiguration");
                
                _logger.Info($"Configuration loaded from: {Path.GetFullPath(_configPath)}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load configuration: {ex.Message}");
                return 2;
            }
            
            // Override log level if specified in command line
            if (!string.IsNullOrEmpty(_logLevel))
            {
                configuration.General.LogLevel = _logLevel;
            }
            
            // Apply configuration to logging
            ApplyLoggingConfiguration(configuration.General);
            
            if (_debugMode)
            {
                _logger.Debug("Debug mode enabled");
                _logger.LogWithProperties(
                    LogLevel.Debug,
                    "Configuration loaded",
                    ("ConfigPath", Path.GetFullPath(_configPath)),
                    ("LogLevel", configuration.General.LogLevel),
                    ("FrameSize", configuration.Network.FrameSize),
                    ("Encryption", configuration.Network.Encryption));
            }

            // サービス初期化
            var frameService = new FrameService(_logger);
            var networkService = new NetworkService(_logger, frameService);
            var securityService = new SecurityService(_logger);
            
            // パイプライン初期化（新規）
            _pipeline = new PacketProcessingPipeline(_logger, frameService, securityService);
            _pipeline.Initialize();
            
            _mainService = new NonIPFileDeliveryService(
                _logger, 
                configService, 
                networkService, 
                securityService, 
                frameService);

            // サービス開始
            _logger.Info("Starting Non-IP File Delivery service...");
           
            if (!await _mainService.StartAsync(configuration))
            {
                _logger.Error("Failed to start service");
                return 1;
            }
            
            _logger.Info("Service started successfully");
            Console.WriteLine("✅ サービスが正常に開始されました");
            Console.WriteLine("📊 パイプライン処理が有効です（TPL Dataflow使用）");
            Console.WriteLine("終了するには Ctrl+C を押してください");
            
            // パイプライン統計を定期的に出力
            _ = Task.Run(async () =>
            {
                while (_pipeline != null)
                {
                    await Task.Delay(10000); // 10秒ごと
                    
                    var stats = _pipeline.GetStatistics();
                    _logger.LogWithProperties(
                        LogLevel.Info,
                        "Pipeline statistics",
                        ("Processed", stats.TotalPacketsProcessed),
                        ("Dropped", stats.TotalPacketsDropped),
                        ("SecurityBlocks", stats.TotalSecurityBlocks),
                        ("DropRate", $"{stats.DropRate:F2}%"));
                }
            });
            
            // Keep running until Ctrl+C
            await WaitForShutdown();
            
            // Stop the service
            await _mainService.StopAsync();
                        
            // パイプライン完了
            if (_pipeline != null)
            {
                await _pipeline.CompleteAsync();
                _pipeline.Dispose();
            }
            
            return 0;
        }
       catch (Exception ex)
        {
            var errorMsg = $"Critical error: {ex.Message}";
            _logger?.Error(errorMsg, ex);
            Console.WriteLine($"❌ {errorMsg}");
            
            if (_debugMode)
            {
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
            return 1;
        }
        finally
        {
            // リソースクリーンアップ
            if (_logger is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static void ParseArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                case "--debug":
                    _debugMode = true;
                    break;
                case "--log-level":
                    if (i + 1 < args.Length)
                    {
                        _logLevel = args[++i];
                    }
                    break;
                case "--config":
                    if (i + 1 < args.Length)
                    {
                        _configPath = args[++i];
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
        Console.WriteLine("  NonIPFileDelivery.exe [オプション]");
        Console.WriteLine();
        Console.WriteLine("オプション:");
        Console.WriteLine("  --debug                デバッグモードで実行");
        Console.WriteLine("  --log-level <level>    ログレベルを指定 (Debug, Info, Warning, Error)");
        Console.WriteLine("  --config <path>        設定ファイルのパスを指定");
        Console.WriteLine("  --help, -h             このヘルプを表示");
    }

    private static void SetupLogging()
    {
        if (_logger == null) return;
        
        // Set up file logging
        var logDirectory = "logs";
        var logFileName = $"NonIP-{DateTime.Now:yyyy-MM-dd}.log";
        var logPath = Path.Combine(logDirectory, logFileName);
        
        _logger.SetLogToFile(logPath);
        
        // Set initial log level
        if (Enum.TryParse<LogLevel>(_logLevel, true, out var level))
        {
            _logger.SetLogLevel(level);
        }
    }

    private static void ApplyLoggingConfiguration(GeneralConfig config)
    {
        if (_logger == null) return;
        
        if (Enum.TryParse<LogLevel>(config.LogLevel, true, out var level))
        {
            _logger.SetLogLevel(level);
        }
    }

    private static async Task WaitForShutdown()
    {
        var tcs = new TaskCompletionSource<bool>();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("🛑 シャットダウン中...");
            tcs.SetResult(true);
        };
        
        await tcs.Task;
    }
}

