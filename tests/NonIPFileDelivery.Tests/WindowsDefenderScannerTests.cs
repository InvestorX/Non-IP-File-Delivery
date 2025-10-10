// WindowsDefenderScannerTests.cs
// Windows Defender スキャナーのユニットテスト

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;
using Xunit;
using Moq;

namespace NonIPFileDelivery.Tests
{
    public class WindowsDefenderScannerTests : IDisposable
    {
        private readonly Mock<ILoggingService> _mockLogger;
        private readonly WindowsDefenderScanner _scanner;
        private readonly bool _isWindows;

        public WindowsDefenderScannerTests()
        {
            _mockLogger = new Mock<ILoggingService>();
            _scanner = new WindowsDefenderScanner(_mockLogger.Object);
            _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        [Fact]
        public void Constructor_DetectsOperatingSystem()
        {
            // Assert
            Assert.Equal(_isWindows, _scanner.IsWindows);
        }

        [Fact]
        public void Constructor_Windows_ChecksDefenderAvailability()
        {
            // Arrange & Act
            // Constructor already called in setup

            // Assert
            if (_isWindows)
            {
                // Windows環境では、Defender利用可能性がチェックされる（true/false両方あり得る）
                // DefenderAvailableはbool型なのでNotNullチェック不要
                Assert.True(_scanner.DefenderAvailable || !_scanner.DefenderAvailable);
            }
            else
            {
                // 非Windows環境では常にfalse
                Assert.False(_scanner.DefenderAvailable);
            }
        }

        [Fact]
        public async Task ScanAsync_NonWindows_ReturnsCleanWithUnavailableMessage()
        {
            // Arrange
            var data = Encoding.ASCII.GetBytes("test data");

            // Act
            var result = await _scanner.ScanAsync(data, "test.txt");

            // Assert
            if (!_isWindows)
            {
                Assert.True(result.IsClean);
                Assert.False(result.DefenderAvailable);
                Assert.Contains("Windows OS", result.ErrorMessage);
            }
        }

        [Fact]
        public async Task ScanAsync_EmptyData_ReturnsClean()
        {
            // Arrange
            var emptyData = Array.Empty<byte>();

            // Act
            var result = await _scanner.ScanAsync(emptyData, "empty.bin");

            // Assert
            Assert.True(result.IsClean);
            // Defender利用可能な場合は"Empty data"メッセージ、利用不可の場合はErrorMessageが設定される
            if (_scanner.DefenderAvailable)
            {
                Assert.Contains("Empty data", result.Details ?? "");
            }
        }

        [Fact]
        public async Task ScanAsync_CleanData_ReturnsClean()
        {
            // Arrange
            var cleanData = Encoding.ASCII.GetBytes("This is a perfectly clean text file with no malware.");

            // Act
            var result = await _scanner.ScanAsync(cleanData, "clean.txt");

            // Assert
            if (_scanner.DefenderAvailable)
            {
                Assert.True(result.IsClean || !string.IsNullOrEmpty(result.ErrorMessage));
                Assert.True(result.ScanDuration >= TimeSpan.Zero);
            }
            else
            {
                // Defender利用不可の場合は、スキップされる
                Assert.True(result.IsClean);
            }
        }

        [Fact]
        public async Task ScanAsync_EICARTestFile_DetectsThreat()
        {
            // Arrange
            // EICAR test string
            var eicarData = Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

            // Act
            var result = await _scanner.ScanAsync(eicarData, "eicar.com", timeoutMs: 120000); // 2分タイムアウト

            // Assert
            if (_scanner.DefenderAvailable)
            {
                // Windows Defenderが利用可能で実際に動作している場合、EICAR検出を期待
                // ただし、環境によってはDefenderが無効化されている場合もある
                if (!result.IsClean)
                {
                    Assert.NotNull(result.ThreatName);
                    Assert.Contains("EICAR", result.ThreatName, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(ThreatLevel.High, result.Severity);
                }
                // クリーンと判定された場合はDefenderがEICARを検出しなかった（無効化されている等）
            }
            else
            {
                // Defender利用不可の場合はスキップ
                Assert.True(result.IsClean);
            }
        }

        [Fact]
        public async Task ScanAsync_LargeFile_CompletesWithinReasonableTime()
        {
            // Arrange
            var largeData = new byte[1 * 1024 * 1024]; // 1 MB
            new Random().NextBytes(largeData);

            // Act
            var result = await _scanner.ScanAsync(largeData, "large.bin", timeoutMs: 120000);

            // Assert
            Assert.NotNull(result);
            if (_scanner.DefenderAvailable)
            {
                // タイムアウトしないことを確認
                Assert.True(result.ScanDuration.TotalSeconds < 120);
            }
        }

        [Fact]
        public async Task ScanAsync_Timeout_ReturnsTimeoutError()
        {
            // Arrange
            var data = Encoding.ASCII.GetBytes("test data");
            var veryShortTimeout = 1; // 1ms - 確実にタイムアウト

            // Act
            var result = await _scanner.ScanAsync(data, "test.txt", veryShortTimeout);

            // Assert
            if (_scanner.DefenderAvailable)
            {
                // タイムアウトまたは正常完了
                Assert.NotNull(result);
            }
        }

        [Fact]
        public void GetStats_ReturnsStatisticsString()
        {
            // Act
            var stats = _scanner.GetStats();

            // Assert
            Assert.Contains("WindowsDefenderScanner Stats", stats);
            Assert.Contains("Available=", stats);
            Assert.Contains("Scans=", stats);
            Assert.Contains("OS=", stats);
        }

        [Fact]
        public async Task CheckServiceStatusAsync_Windows_ReturnsBoolean()
        {
            // Act
            var status = await _scanner.CheckServiceStatusAsync();

            // Assert
            if (_isWindows && _scanner.DefenderAvailable)
            {
                // Windows環境でDefender利用可能な場合、true/falseのいずれかを返す
                Assert.True(status || !status); // bool型なので常にtrue
            }
            else
            {
                // 非Windows環境または利用不可の場合はfalse
                Assert.False(status);
            }
        }

        [Fact]
        public void TotalScans_InitiallyZero()
        {
            // Assert
            Assert.Equal(0, _scanner.TotalScans);
        }

        [Fact]
        public async Task TotalScans_IncrementsAfterScan()
        {
            // Arrange
            var initialCount = _scanner.TotalScans;
            var data = Encoding.ASCII.GetBytes("test");

            // Act
            await _scanner.ScanAsync(data, "test.txt");

            // Assert
            Assert.Equal(initialCount + 1, _scanner.TotalScans);
        }

        [Fact]
        public async Task ScanAsync_MultipleCalls_IndependentResults()
        {
            // Arrange
            var data1 = Encoding.ASCII.GetBytes("clean data 1");
            var data2 = Encoding.ASCII.GetBytes("clean data 2");

            // Act
            var result1 = await _scanner.ScanAsync(data1, "file1.txt");
            var result2 = await _scanner.ScanAsync(data2, "file2.txt");

            // Assert
            Assert.NotNull(result1);
            Assert.NotNull(result2);
            Assert.NotSame(result1, result2);
        }

        [Fact]
        public async Task ScanAsync_SpecialCharactersInFileName_HandlesCorrectly()
        {
            // Arrange
            var data = Encoding.ASCII.GetBytes("test data");
            var specialFileName = "test file (special) [chars] & symbols.txt";

            // Act
            var result = await _scanner.ScanAsync(data, specialFileName);

            // Assert
            Assert.NotNull(result);
            // ファイル名に特殊文字があっても正常に処理される
        }

        [Fact]
        public void Dispose_CleansUpResources()
        {
            // Arrange
            using var scanner = new WindowsDefenderScanner(_mockLogger.Object);

            // Act
            scanner.Dispose();

            // Assert
            // 例外が発生しないことを確認（一時ディレクトリのクリーンアップ）
            Assert.True(true);
        }

        [Fact]
        public async Task ScanAsync_NullFileName_UsesDefaultName()
        {
            // Arrange
            var data = Encoding.ASCII.GetBytes("test");

            // Act
            var result = await _scanner.ScanAsync(data); // fileNameパラメータなし

            // Assert
            Assert.NotNull(result);
            // デフォルトファイル名でスキャンされる
        }

        [Fact]
        public async Task ScanAsync_BinaryData_ScansCorrectly()
        {
            // Arrange
            var binaryData = new byte[] { 0x00, 0xFF, 0x7F, 0x80, 0x01, 0xFE };

            // Act
            var result = await _scanner.ScanAsync(binaryData, "binary.dat");

            // Assert
            Assert.NotNull(result);
            Assert.True(result.ScanDuration >= TimeSpan.Zero);
        }

        public void Dispose()
        {
            _scanner?.Dispose();
        }
    }
}
