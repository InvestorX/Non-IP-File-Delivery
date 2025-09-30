using System;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// ロギングサービスのインターフェース
/// </summary>
public interface ILoggingService
{
    // 既存メソッド（互換性維持）
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? ex = null);
    void SetLogLevel(LogLevel level);
    void SetLogToFile(string path);

    // 新規メソッド（Serilog機能）
    void SetElasticsearchSink(string[] nodes, string indexPrefix = "transceiver-logs");
    void LogWithProperties(LogLevel level, string message, params (string Key, object Value)[] properties);
    IDisposable BeginPerformanceScope(string operationName, params (string Key, object Value)[] metadata);
}
