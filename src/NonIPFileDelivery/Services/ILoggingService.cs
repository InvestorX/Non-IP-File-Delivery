using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// ロギングサービスの抽象
/// </summary>
public interface ILoggingService
{
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? ex = null);
    void SetLogLevel(LogLevel level);
    void SetLogToFile(string path);

    // 追加機能
    void SetElasticsearchSink(string[] nodes, string indexPrefix = "transceiver-logs");
    void LogWithProperties(LogLevel level, string message, params (string Key, object Value)[] properties);
    IDisposable BeginPerformanceScope(string operationName, params (string Key, object Value)[] metadata);
}
