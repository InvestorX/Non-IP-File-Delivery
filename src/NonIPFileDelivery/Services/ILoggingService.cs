using System;

namespace NonIPFileDelivery.Services;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

public interface ILoggingService
{
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message);
    void Error(string message, Exception exception);
    void SetLogLevel(LogLevel level);
    void SetLogToFile(string logFilePath);
}