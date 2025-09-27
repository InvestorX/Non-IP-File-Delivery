using System;
using System.IO;

namespace NonIPFileDelivery.Services;

public class LoggingService : ILoggingService
{
    private LogLevel _currentLogLevel = LogLevel.Warning;
    private string? _logFilePath;
    private readonly object _lockObject = new();

    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warning(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);
    public void Error(string message, Exception exception) => Log(LogLevel.Error, $"{message}: {exception.Message}\n{exception.StackTrace}");

    public void SetLogLevel(LogLevel level)
    {
        _currentLogLevel = level;
    }

    public void SetLogToFile(string logFilePath)
    {
        _logFilePath = logFilePath;
        
        // Ensure log directory exists
        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private void Log(LogLevel level, string message)
    {
        if (level < _currentLogLevel)
            return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var levelString = level.ToString().ToUpper();
        var formattedMessage = $"[{timestamp}] [{levelString}] {message}";

        lock (_lockObject)
        {
            // Always log to console
            Console.WriteLine(formattedMessage);

            // Log to file if configured
            if (!string.IsNullOrEmpty(_logFilePath))
            {
                try
                {
                    File.AppendAllText(_logFilePath, formattedMessage + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{timestamp}] [ERROR] Failed to write to log file: {ex.Message}");
                }
            }
        }
    }
}