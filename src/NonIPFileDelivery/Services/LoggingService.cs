using System;
using System.IO;
using Serilog;
using Serilog.Events;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// 構造化ロギングサービス（Serilog統合版）
/// 既存のILoggingServiceインターフェースを維持しつつ、Serilogの機能を活用
/// </summary>
public class LoggingService : ILoggingService, IDisposable
{
    private ILogger _logger;
    private LogEventLevel _currentLevel = LogEventLevel.Warning;

    public LoggingService()
    {
        // 初期設定（コンソール出力のみ）
        _logger = new LoggerConfiguration()
            .MinimumLevel.Is(_currentLevel)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    /// <summary>
    /// ログレベルを設定
    /// </summary>
    public void SetLogLevel(LogLevel level)
    {
        _currentLevel = level switch
        {
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Info => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            _ => LogEventLevel.Warning
        };

        // ロガーを再構成
        ReconfigureLogger();
    }

    /// <summary>
    /// ファイル出力を設定
    /// </summary>
    public void SetLogToFile(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // ロガーを再構成（ファイル出力追加）
        ReconfigureLogger(path);
    }

    /// <summary>
    /// Elasticsearchシンクを追加（新機能）
    /// </summary>
    public void SetElasticsearchSink(string[] nodes, string indexPrefix = "transceiver-logs")
    {
        if (nodes == null || nodes.Length == 0)
            return;

        var uris = Array.ConvertAll(nodes, node => new Uri(node));

        _logger = new LoggerConfiguration()
            .MinimumLevel.Is(_currentLevel)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
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
    }

    /// <summary>
    /// デバッグログ
    /// </summary>
    public void Debug(string message)
    {
        _logger.Debug(message);
    }

    /// <summary>
    /// 情報ログ
    /// </summary>
    public void Info(string message)
    {
        _logger.Information(message);
    }

    /// <summary>
    /// 警告ログ
    /// </summary>
    public void Warning(string message)
    {
        _logger.Warning(message);
    }

    /// <summary>
    /// エラーログ
    /// </summary>
    public void Error(string message, Exception? ex = null)
    {
        if (ex != null)
            _logger.Error(ex, message);
        else
            _logger.Error(message);
    }

    /// <summary>
    /// 構造化ログ（追加機能）
    /// プロパティを持つログを記録
    /// </summary>
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

        var enrichedLogger = _logger;
        foreach (var (key, value) in properties)
        {
            enrichedLogger = enrichedLogger.ForContext(key, value);
        }

        enrichedLogger.Write(logLevel, message);
    }

    /// <summary>
    /// パフォーマンス測定用のスコープを開始（追加機能）
    /// </summary>
    public IDisposable BeginPerformanceScope(string operationName, params (string Key, object Value)[] metadata)
    {
        return new PerformanceMeasurementScope(_logger, operationName, metadata);
    }

    /// <summary>
    /// ロガーを再構成
    /// </summary>
    private void ReconfigureLogger(string? filePath = null)
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Is(_currentLevel)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("Application", "NonIPFileDelivery");

        if (!string.IsNullOrEmpty(filePath))
        {
            config.WriteTo.File(
                path: filePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
        }

        _logger = config.CreateLogger();
    }

    public void Dispose()
    {
        if (_logger is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

/// <summary>
/// パフォーマンス測定用のスコープ
/// using文で使用し、処理時間を自動記録
/// </summary>
internal class PerformanceMeasurementScope : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _operationName;
    private readonly System.Diagnostics.Stopwatch _stopwatch;
    private readonly (string Key, object Value)[] _metadata;

    public PerformanceMeasurementScope(
        ILogger logger,
        string operationName,
        (string Key, object Value)[] metadata)
    {
        _logger = logger;
        _operationName = operationName;
        _metadata = metadata;
        _stopwatch = System.Diagnostics.Stopwatch.StartNew();
    }

    public void Dispose()
    {
        _stopwatch.Stop();

        var enrichedLogger = _logger;
        foreach (var (key, value) in _metadata)
        {
            enrichedLogger = enrichedLogger.ForContext(key, value);
        }

        enrichedLogger.Information(
            "Operation {Operation} completed in {Duration}ms",
            _operationName,
            _stopwatch.ElapsedMilliseconds);
    }
}
