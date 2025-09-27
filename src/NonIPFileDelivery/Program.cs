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
    
    // Service instances
    private static ILoggingService? _logger;
    private static NonIPFileDeliveryService? _mainService;

    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Non-IP File Delivery v1.0.0");
        Console.WriteLine("🛡️ ハッカー・クラッカー・ランサムウェア対策のためのRaw Ethernet非IPファイル転送システム");
        Console.WriteLine();

        try
        {
            ParseArguments(args);
            
            // Initialize logging service
            _logger = new LoggingService();
            SetupLogging();

            _logger.Info("Starting Non-IP File Delivery Service");
            
            // Load configuration
            var configService = new ConfigurationService();
            Configuration configuration;
            
            if (!File.Exists(_configPath))
            {
                _logger.Warning($"Configuration file not found: {_configPath}");
                _logger.Info("Creating default configuration file...");
                configService.CreateDefaultConfiguration(_configPath);
            }

            try
            {
                configuration = configService.LoadConfiguration(_configPath);
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
                _logger.Debug($"Configuration file: {Path.GetFullPath(_configPath)}");
                _logger.Debug($"Log level: {configuration.General.LogLevel}");
            }

            // Initialize services
            var frameService = new FrameService(_logger);
            var networkService = new NetworkService(_logger, frameService);
            var securityService = new SecurityService(_logger);
            _mainService = new NonIPFileDeliveryService(_logger, configService, networkService, securityService, frameService);

            // Start the main service
            _logger.Info("Starting Non-IP File Delivery service...");
            
            if (!await _mainService.StartAsync(configuration))
            {
                _logger.Error("Failed to start service");
                return 1;
            }
            
            _logger.Info("Service started successfully");
            Console.WriteLine("✅ サービスが正常に開始されました");
            Console.WriteLine("終了するには Ctrl+C を押してください");
            
            // Keep running until Ctrl+C
            await WaitForShutdown();
            
            // Stop the service
            await _mainService.StopAsync();
            
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
