using Serilog;
using Serilog.Events;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// Serilogベースの構造化ロギング実装
/// </summary>
public class LoggingService : ILoggingService, IDisposable
{
    private ILogger _logger;
    private LogEventLevel _currentLevel = LogEventLevel.Warning;
    private string? _currentFilePath;
    private readonly object _reconfigureLock = new();
    private bool _disposed;

    public LoggingService()
    {
        _logger = new LoggerConfiguration()
            .MinimumLevel.Is(_currentLevel)
            .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("Application", "NonIPFileDelivery")
            .CreateLogger();
    }

    public void SetLogLevel(LogLevel level)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LoggingService));

        var newLevel = level switch
        {
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Info => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            _ => LogEventLevel.Warning
        };

        if (_currentLevel == newLevel) return;

        lock (_reconfigureLock)
        {
            _currentLevel = newLevel;
            ReconfigureLogger(_currentFilePath);
        }
    }

    public void SetLogToFile(string path)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LoggingService));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        lock (_reconfigureLock)
        {
            _currentFilePath = path;
            ReconfigureLogger(path);
        }
    }

    public void SetElasticsearchSink(string[] nodes, string indexPrefix = "transceiver-logs")
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LoggingService));
        if (nodes is null || nodes.Length == 0) return;

        var uris = nodes.Select(n => new Uri(n)).ToArray();

        lock (_reconfigureLock)
        {
            var old = _logger;
            _logger = new LoggerConfiguration()
                .MinimumLevel.Is(_currentLevel)
                .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Elasticsearch(new Serilog.Sinks.Elasticsearch.ElasticsearchSinkOptions(uris)
                {
                    IndexFormat = $"{indexPrefix}-{{0:yyyy.MM.dd}}",
                    AutoRegisterTemplate = true,
                    NumberOfShards = 2,
                    NumberOfReplicas = 1,
                    MinimumLogEventLevel = LogEventLevel.Information
                })
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithThreadId()
                .Enrich.WithProperty("Application", "NonIPFileDelivery")
                .CreateLogger();

            (old as IDisposable)?.Dispose();
        }
    }

    public void Debug(string message) => _logger.Debug(message);
    public void Info(string message) => _logger.Information(message);
    public void Warning(string message) => _logger.Warning(message);

    public void Error(string message, Exception? ex = null)
    {
        if (ex != null) _logger.Error(ex, message);
        else _logger.Error(message);
    }

    public void LogWithProperties(LogLevel level, string message, params (string Key, object Value)[] properties)
    {
        var logLevel = level switch
        {
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Info => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            _ => LogEventLevel.Information
        };

        var enriched = _logger;
        foreach (var (k, v) in properties)
        {
            enriched = enriched.ForContext(k, v);
        }
        enriched.Write(logLevel, message);
    }

    public IDisposable BeginPerformanceScope(string operationName, params (string Key, object Value)[] metadata)
        => new PerformanceMeasurementScope(_logger, operationName, metadata);

    private void ReconfigureLogger(string? filePath = null)
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Is(_currentLevel)
            .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("Application", "NonIPFileDelivery");

        if (!string.IsNullOrEmpty(filePath))
        {
            config = config.WriteTo.File(
                path: filePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
        }

        var old = _logger;
        _logger = config.CreateLogger();
        (old as IDisposable)?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_reconfigureLock)
        {
            _disposed = true;
            ( _logger as IDisposable )?.Dispose();
        }
    }
}

internal sealed class PerformanceMeasurementScope : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _operationName;
    private readonly (string Key, object Value)[] _metadata;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

    public PerformanceMeasurementScope(ILogger logger, string operationName, (string Key, object Value)[] metadata)
    {
        _logger = logger;
        _operationName = operationName;
        _metadata = metadata;
    }

    public void Dispose()
    {
        _sw.Stop();
        var enriched = _logger;
        foreach (var (k, v) in _metadata)
        {
            enriched = enriched.ForContext(k, v);
        }
        enriched.Information("Operation {Operation} completed in {Duration}ms", _operationName, _sw.ElapsedMilliseconds);
    }
}
