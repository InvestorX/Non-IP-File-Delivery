using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;
using NonIPFileDelivery.Resilience;

namespace NonIPFileDelivery;

class Program
{
    private static string _configPath = "config.ini";
    private static bool _debugMode = false;
    private static string _logLevel = "Warning";

    private static ILoggingService? _logger;
    private static NonIPFileDeliveryService? _mainService;
    private static PacketProcessingPipeline? _pipeline;
    private static RetryPolicy? _retryPolicy;
    private static volatile bool _disposed;

    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Non-IP File Delivery v1.1.0");
        Console.WriteLine("🛡️ Raw Ethernet 非IPファイル転送システム");
        Console.WriteLine("✨ CRC32 / 構造化ログ / TPL Dataflow 対応");
        Console.WriteLine();

        try
        {
            ParseArguments(args);

            _logger = new LoggingService();
            SetupLogging();

            _logger.Info("Starting Non-IP File Delivery Service v1.1.0");
            _logger.LogWithProperties(
                LogLevel.Info,
                "System environment",
                ("OS", Environment.OSVersion.ToString()),
                ("ProcessorCount", Environment.ProcessorCount),
                ("WorkingSet", Environment.WorkingSet),
                (".NET Version", Environment.Version.ToString()));

            _retryPolicy = new RetryPolicy(_logger);

            var configService = new ConfigurationService();
            Configuration configuration;

            if (!File.Exists(_configPath))
            {
                _logger.Warning($"Configuration file not found: {_configPath}");
                _logger.Info("Creating default configuration file...");
                await configService.CreateDefaultConfigurationAsync(_configPath);
                Console.WriteLine($"✅ デフォルト設定ファイルを作成しました: {_configPath}");
            }

            try
            {
                configuration = await _retryPolicy.ExecuteAsync(
                    async () => await configService.LoadConfigurationAsync(_configPath),
                    "LoadConfiguration");

                var ext = Path.GetExtension(_configPath).ToLowerInvariant();
                _logger.Info($"Configuration loaded from: {Path.GetFullPath(_configPath)} (format: {ext})");

                _logger.LogWithProperties(
                    LogLevel.Info,
                    "Configuration summary",
                    ("Mode", configuration.General.Mode),
                    ("Interface", configuration.Network.Interface),
                    ("FrameSize", configuration.Network.FrameSize),
                    ("Encryption", configuration.Network.Encryption),
                    ("VirusScan", configuration.Security.EnableVirusScan),
                    ("MaxMemoryMB", configuration.Performance.MaxMemoryMB));
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load configuration: {ex.Message}", ex);
                Console.WriteLine($"❌ 設定ファイルの読み込みに失敗しました: {ex.Message}");
                Console.WriteLine("💡 対応形式: .ini, .json");
                return 2;
            }

            if (!string.IsNullOrEmpty(_logLevel))
            {
                configuration.General.LogLevel = _logLevel;
            }
            ApplyLoggingConfiguration(configuration.General);

            if (_debugMode)
            {
                _logger.LogWithProperties(
                    LogLevel.Debug,
                    "Debug mode configuration",
                    ("ConfigPath", Path.GetFullPath(_configPath)),
                    ("LogLevel", configuration.General.LogLevel),
                    ("FrameSize", configuration.Network.FrameSize),
                    ("Encryption", configuration.Network.Encryption));
            }

            var cryptoService = new CryptoService(_logger);
            var frameService = new FrameService(_logger, cryptoService);
            var networkService = new NetworkService(_logger, frameService);
            var securityService = new SecurityService(_logger);

            _mainService = new NonIPFileDeliveryService(_logger, configService, networkService, securityService, frameService);

            _logger.Info("Starting Non-IP File Delivery service...");
            if (!await _mainService.StartAsync(configuration))
            {
                _logger.Error("Failed to start service");
                return 1;
            }

            _logger.Info("Service started successfully");
            Console.WriteLine("✅ サービス起動完了（Ctrl+C で終了）");

            _ = Task.Run(async () =>
            {
                while (!_disposed && _pipeline != null)
                {
                    await Task.Delay(10_000);
                    if (_pipeline == null || _disposed) break;

                    var stats = _pipeline.GetStatistics();
                    _logger.LogWithProperties(
                        LogLevel.Info,
                        "Pipeline statistics",
                        ("Processed", stats.TotalPacketsProcessed),
                        ("Dropped", stats.TotalPacketsDropped),
                        ("SecurityBlocks", stats.TotalSecurityBlocks),
                        ("DropRate", $"{stats.DropRate:F2}%"),
                        ("Throughput", $"{stats.ThroughputMbps:F2} Mbps"),
                        ("PacketsPerSec", $"{stats.PacketsPerSecond:F2}"),
                        ("Uptime", stats.Uptime.ToString(@"hh\:mm\:ss")));

                    Console.WriteLine($"📊 処理:{stats.TotalPacketsProcessed} 破棄:{stats.TotalPacketsDropped} スループット:{stats.ThroughputMbps:F2}Mbps 稼働:{stats.Uptime:hh\\:mm\\:ss}");
                }
            });

            await WaitForShutdown();

            await _mainService.StopAsync();

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
            if (_debugMode) Console.WriteLine(ex);
            return 1;
        }
        finally
        {
            _disposed = true;
            ( _logger as IDisposable )?.Dispose();
        }
    }

    private static void ParseArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--debug":
                    _debugMode = true;
                    break;
                case "--log-level":
                    if (i + 1 < args.Length) _logLevel = args[++i];
                    break;
                case "--config":
                    if (i + 1 < args.Length) _configPath = args[++i];
                    break;
                case "--convert-to-json":
                    if (i + 2 < args.Length)
                    {
                        var iniPath = args[++i];
                        var jsonPath = args[++i];
                        ConvertConfigurationAndExit(iniPath, jsonPath).Wait();
                    }
                    else
                    {
                        Console.WriteLine("❌ エラー: --convert-to-json には2つの引数が必要です");
                        Console.WriteLine("   例: --convert-to-json config.ini config.json");
                        Environment.Exit(1);
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
        Console.WriteLine("  --debug                         デバッグモードで実行");
        Console.WriteLine("  --log-level <level>             ログレベル (Debug, Info, Warning, Error)");
        Console.WriteLine("  --config <path>                 設定ファイル (.ini または .json)");
        Console.WriteLine("  --convert-to-json <ini> <json>  INI設定をJSONへ変換");
        Console.WriteLine("  --help, -h                      このヘルプを表示");
        Console.WriteLine();
    }

    private static async Task ConvertConfigurationAndExit(string iniPath, string jsonPath)
    {
        try
        {
            Console.WriteLine($"🔄 設定変換: {iniPath} → {jsonPath}");
            var configService = new ConfigurationService();
            await configService.ConvertIniToJsonAsync(iniPath, jsonPath);
            Console.WriteLine($"✅ 変換完了: {jsonPath}");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ 変換失敗: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static void SetupLogging()
    {
        if (_logger == null) return;

        var logDirectory = "logs";
        var logFileName = $"NonIP-{DateTime.Now:yyyy-MM-dd}.log";
        var logPath = Path.Combine(logDirectory, logFileName);

        _logger.SetLogToFile(logPath);

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
            tcs.TrySetResult(true);
        };
        await tcs.Task;
    }
}
