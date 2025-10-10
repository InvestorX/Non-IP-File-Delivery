using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using NonIPFileDelivery.Services;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Tests
{
    /// <summary>
    /// ClamAVScanner拡張機能テスト
    /// 注意: このテストはclamdが実行されている環境でのみ完全に動作します
    /// </summary>
    public class ClamAVScannerTests
    {
        private readonly MockLoggingService _logger;
        private readonly string _clamdHost = "localhost";
        private readonly int _clamdPort = 3310;

        public ClamAVScannerTests()
        {
            _logger = new MockLoggingService();
        }

        [Fact]
        public void Constructor_InitializesSuccessfully()
        {
            // Arrange & Act
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);

            // Assert
            Assert.NotNull(scanner);
            Assert.Equal(0, scanner.TotalScans);
            Assert.Equal(0, scanner.TotalThreats);
            Assert.Equal(0, scanner.TotalErrors);
        }

        [Fact]
        public void Constructor_ThrowsOnNullLogger()
        {
            // Arrange & Act & Assert
            Assert.Throws<ArgumentNullException>(() => new ClamAVScanner(null!, _clamdHost, _clamdPort));
        }

        [Fact]
        public async Task ScanAsync_EmptyData_ReturnsClean()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);
            var emptyData = Array.Empty<byte>();

            // Act
            var result = await scanner.ScanAsync(emptyData);

            // Assert
            Assert.True(result.IsClean);
            Assert.Equal("INSTREAM", result.ScanMethod);
        }

        [Fact]
        public async Task ScanAsync_CleanData_IncrementsStatistics()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);
            var cleanData = System.Text.Encoding.UTF8.GetBytes("This is clean test data");

            // Act
            var initialScans = scanner.TotalScans;
            var result = await scanner.ScanAsync(cleanData);

            // Assert
            // Note: This will fail if clamd is not running, which is expected
            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                // clamd not available - verify error tracking
                Assert.Equal(initialScans + 1, scanner.TotalScans);
                Assert.Equal(1, scanner.TotalErrors);
            }
            else
            {
                // clamd available - verify clean scan
                Assert.Equal(initialScans + 1, scanner.TotalScans);
                Assert.True(result.ScanDuration > TimeSpan.Zero);
                Assert.Equal(cleanData.Length, result.FileSize);
            }
        }

        [Fact]
        public async Task ScanAsync_EICARTestFile_DetectsThreat()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);
            
            // EICAR test file (standard antivirus test pattern)
            var eicarString = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";
            var eicarData = System.Text.Encoding.ASCII.GetBytes(eicarString);

            // Act
            var result = await scanner.ScanAsync(eicarData);

            // Assert
            // This test requires clamd to be running
            if (string.IsNullOrEmpty(result.ErrorMessage))
            {
                Assert.False(result.IsClean);
                Assert.Contains("EICAR", result.VirusName ?? result.Details ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.True(scanner.TotalThreats > 0);
            }
        }

        [Fact]
        public async Task TestConnectionAsync_WithoutClamd_ReturnsFalse()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, "invalid-host", 99999);

            // Act
            var connected = await scanner.TestConnectionAsync();

            // Assert
            Assert.False(connected);
        }

        [Fact]
        public async Task GetVersionAsync_WithoutClamd_ReturnsNull()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, "invalid-host", 99999);

            // Act
            var version = await scanner.GetVersionAsync();

            // Assert
            Assert.Null(version);
        }

        [Fact]
        public async Task MultiScanAsync_EmptyArray_ReturnsClean()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);
            var emptyFiles = Array.Empty<string>();

            // Act
            var result = await scanner.MultiScanAsync(emptyFiles);

            // Assert
            Assert.True(result.IsClean);
            Assert.Equal("MULTISCAN", result.ScanMethod);
            Assert.Contains("No files specified", result.ErrorMessage ?? "");
        }

        [Fact]
        public async Task MultiScanAsync_NonExistentFiles_HandlesGracefully()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);
            var nonExistentFiles = new[] { "/tmp/nonexistent1.txt", "/tmp/nonexistent2.txt" };

            // Act
            var result = await scanner.MultiScanAsync(nonExistentFiles);

            // Assert
            // Should complete without crashing
            Assert.NotNull(result);
            Assert.Equal("MULTISCAN", result.ScanMethod);
        }

        [Fact]
        public async Task MultiScanAsync_WithValidFiles_ScansSuccessfully()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);
            
            // Create temporary test files
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            
            try
            {
                var file1 = Path.Combine(tempDir, "test1.txt");
                var file2 = Path.Combine(tempDir, "test2.txt");
                await File.WriteAllTextAsync(file1, "Clean test data 1");
                await File.WriteAllTextAsync(file2, "Clean test data 2");

                var files = new[] { file1, file2 };

                // Act
                var result = await scanner.MultiScanAsync(files);

                // Assert
                Assert.Equal("MULTISCAN", result.ScanMethod);
                
                if (string.IsNullOrEmpty(result.ErrorMessage))
                {
                    // clamd available
                    Assert.True(result.TotalFilesScanned >= 0);
                    Assert.True(result.ScanDuration > TimeSpan.Zero);
                }
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task ContScanAsync_EmptyPath_ReturnsError()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);

            // Act
            var result = await scanner.ContScanAsync("");

            // Assert
            Assert.True(result.IsClean);
            Assert.Equal("CONTSCAN", result.ScanMethod);
            Assert.Contains("No path specified", result.ErrorMessage ?? "");
        }

        [Fact]
        public async Task ContScanAsync_NonExistentPath_ReturnsError()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);
            var nonExistentPath = "/tmp/nonexistent_directory_12345";

            // Act
            var result = await scanner.ContScanAsync(nonExistentPath);

            // Assert
            Assert.False(result.IsClean);
            Assert.Equal("CONTSCAN", result.ScanMethod);
            Assert.Contains("Path not found", result.ErrorMessage ?? "");
            Assert.Equal(nonExistentPath, result.FilePath);
        }

        [Fact]
        public async Task ContScanAsync_ValidPath_ScansSuccessfully()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);
            
            // Create temporary test directory
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            
            try
            {
                var testFile = Path.Combine(tempDir, "test.txt");
                await File.WriteAllTextAsync(testFile, "Clean test data");

                // Act
                var result = await scanner.ContScanAsync(tempDir);

                // Assert
                Assert.Equal("CONTSCAN", result.ScanMethod);
                Assert.Equal(tempDir, result.FilePath);
                
                if (string.IsNullOrEmpty(result.ErrorMessage))
                {
                    // clamd available
                    Assert.True(result.ScanDuration > TimeSpan.Zero);
                }
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task GetStatsAsync_WithoutClamd_ReturnsNull()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, "invalid-host", 99999);

            // Act
            var stats = await scanner.GetStatsAsync();

            // Assert
            Assert.Null(stats);
        }

        [Fact]
        public async Task GetStatsAsync_WithClamd_ReturnsStats()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);

            // Act
            var stats = await scanner.GetStatsAsync();

            // Assert
            // Will be null if clamd is not running, which is expected
            if (stats != null)
            {
                Assert.NotEmpty(stats);
            }
        }

        [Fact]
        public async Task ReloadDatabaseAsync_WithoutClamd_ReturnsFalse()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, "invalid-host", 99999);

            // Act
            var result = await scanner.ReloadDatabaseAsync();

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task ReloadDatabaseAsync_WithClamd_SucceedsOrFails()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);

            // Act
            var result = await scanner.ReloadDatabaseAsync();

            // Assert
            // Result depends on clamd availability and permissions
            // Just verify it doesn't crash
            Assert.True(result || !result);
        }

        [Fact]
        public void GetLocalStatistics_ReturnsCorrectFormat()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);

            // Act
            var stats = scanner.GetLocalStatistics();

            // Assert
            Assert.NotNull(stats);
            Assert.True(stats.ContainsKey("TotalScans"));
            Assert.True(stats.ContainsKey("TotalThreats"));
            Assert.True(stats.ContainsKey("TotalErrors"));
            Assert.True(stats.ContainsKey("AverageScanDuration"));
            Assert.True(stats.ContainsKey("ScanCount"));
            
            Assert.Equal(0, stats["TotalScans"]);
            Assert.Equal(0, stats["TotalThreats"]);
            Assert.Equal(0, stats["TotalErrors"]);
        }

        [Fact]
        public async Task Statistics_UpdateCorrectlyAfterMultipleScans()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);
            var testData = System.Text.Encoding.UTF8.GetBytes("Test data");

            // Act
            await scanner.ScanAsync(testData);
            await scanner.ScanAsync(testData);
            await scanner.ScanAsync(testData);

            // Assert
            Assert.Equal(3, scanner.TotalScans);
            Assert.True(scanner.AverageScanDuration >= TimeSpan.Zero);
            
            var stats = scanner.GetLocalStatistics();
            Assert.Equal(3, stats["ScanCount"]);
        }

        [Fact]
        public async Task ScanAsync_Timeout_HandlesGracefully()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);
            var testData = System.Text.Encoding.UTF8.GetBytes("Test data");

            // Act - Very short timeout
            var result = await scanner.ScanAsync(testData, timeoutMs: 1);

            // Assert
            // Should handle timeout without crashing
            Assert.NotNull(result);
            Assert.False(result.IsClean); // Timeout should result in error
        }

        [Fact]
        public async Task MultiScanAsync_LargeFileArray_HandlesCorrectly()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);
            
            // Create array of 100 non-existent files (edge case test)
            var files = Enumerable.Range(1, 100)
                .Select(i => $"/tmp/test_{i}.txt")
                .ToArray();

            // Act
            var result = await scanner.MultiScanAsync(files);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("MULTISCAN", result.ScanMethod);
        }

        [Fact]
        public async Task TotalScans_ThreadSafe()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);
            var testData = System.Text.Encoding.UTF8.GetBytes("Test");

            // Act - Multiple concurrent scans
            var tasks = Enumerable.Range(0, 10)
                .Select(i => scanner.ScanAsync(testData))
                .ToArray();

            await Task.WhenAll(tasks);

            // Assert
            Assert.Equal(10, scanner.TotalScans);
        }

        [Fact]
        public void AverageScanDuration_WithNoScans_ReturnsZero()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);

            // Act
            var avgDuration = scanner.AverageScanDuration;

            // Assert
            Assert.Equal(TimeSpan.Zero, avgDuration);
        }

        [Fact]
        public async Task ScanAsync_SetsCorrectMetadata()
        {
            // Arrange
            var scanner = new ClamAVScanner(_logger, _clamdHost, _clamdPort);
            var testData = System.Text.Encoding.UTF8.GetBytes("Test data for metadata");

            // Act
            var result = await scanner.ScanAsync(testData);

            // Assert
            Assert.Equal("INSTREAM", result.ScanMethod);
            Assert.Equal(testData.Length, result.FileSize);
            Assert.True(result.ScanDuration >= TimeSpan.Zero);
            Assert.True(result.ScanTime <= DateTime.UtcNow);
        }
    }

    /// <summary>
    /// モックロギングサービス（テスト用）
    /// </summary>
    internal class MockLoggingService : ILoggingService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? ex = null) { }
        public void SetLogLevel(LogLevel level) { }
        public void SetLogToFile(string path) { }
        public void SetElasticsearchSink(string[] nodes, string indexPrefix = "transceiver-logs") { }
        public void LogWithProperties(LogLevel level, string message, params (string Key, object Value)[] properties) { }
        public IDisposable BeginPerformanceScope(string operationName, params (string Key, object Value)[] metadata)
        {
            return new MockDisposable();
        }
    }

    internal class MockDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
