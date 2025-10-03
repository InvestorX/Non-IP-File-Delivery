using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;
using Xunit;

namespace NonIPFileDelivery.Tests;

/// <summary>
/// YARAスキャナーのユニットテスト
/// </summary>
public class YARAScannerTests
{
    private readonly Mock<ILoggingService> _mockLogger;
    private readonly string _testRulesPath;

    public YARAScannerTests()
    {
        _mockLogger = new Mock<ILoggingService>();
        _testRulesPath = Path.Combine(
            Path.GetDirectoryName(typeof(YARAScannerTests).Assembly.Location) ?? "",
            "..", "..", "..", "..", "..", "yara_rules", "malware.yar"
        );
    }

    [Fact(Skip = "Requires native YARA library to be installed")]
    public void Constructor_WithValidRulesFile_ShouldSucceed()
    {
        // Arrange & Act
        using var scanner = new YARAScanner(_mockLogger.Object, _testRulesPath);

        // Assert
        scanner.Should().NotBeNull();
    }

    [Fact(Skip = "Requires native YARA library to be installed")]
    public void Constructor_WithInvalidRulesFile_ShouldThrow()
    {
        // Arrange & Act & Assert
        Action act = () => new YARAScanner(_mockLogger.Object, "/nonexistent/path.yar");
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact(Skip = "Requires native YARA library to be installed")]
    public async Task ScanAsync_WithCleanData_ShouldReturnNoMatch()
    {
        // Arrange
        using var scanner = new YARAScanner(_mockLogger.Object, _testRulesPath);
        var cleanData = Encoding.UTF8.GetBytes("This is clean text data");

        // Act
        var result = await scanner.ScanAsync(cleanData);

        // Assert
        result.Should().NotBeNull();
        result.IsMatch.Should().BeFalse();
        result.ErrorMessage.Should().BeNullOrEmpty();
    }

    [Fact(Skip = "Requires native YARA library to be installed")]
    public async Task ScanAsync_WithEICARTestString_ShouldDetectThreat()
    {
        // Arrange
        using var scanner = new YARAScanner(_mockLogger.Object, _testRulesPath);
        var eicarData = Encoding.UTF8.GetBytes(
            "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"
        );

        // Act
        var result = await scanner.ScanAsync(eicarData);

        // Assert
        result.Should().NotBeNull();
        result.IsMatch.Should().BeTrue();
        result.RuleName.Should().Be("EICAR_Test_File");
    }

    [Fact(Skip = "Requires native YARA library to be installed")]
    public async Task ScanAsync_WithRansomwareIndicators_ShouldDetectThreat()
    {
        // Arrange
        using var scanner = new YARAScanner(_mockLogger.Object, _testRulesPath);
        var ransomwareData = Encoding.UTF8.GetBytes(
            "Your files are encrypted. Send bitcoin to decrypt. Pay ransom now."
        );

        // Act
        var result = await scanner.ScanAsync(ransomwareData);

        // Assert
        result.Should().NotBeNull();
        result.IsMatch.Should().BeTrue();
        result.RuleName.Should().Be("Ransomware_Indicators");
    }

    [Fact(Skip = "Requires native YARA library to be installed")]
    public async Task ScanAsync_WithSQLInjection_ShouldDetectThreat()
    {
        // Arrange
        using var scanner = new YARAScanner(_mockLogger.Object, _testRulesPath);
        var sqlInjectionData = Encoding.UTF8.GetBytes("' OR '1'='1");

        // Act
        var result = await scanner.ScanAsync(sqlInjectionData);

        // Assert
        result.Should().NotBeNull();
        result.IsMatch.Should().BeTrue();
        result.RuleName.Should().Be("SQL_Injection_Patterns");
    }

    [Fact(Skip = "Requires native YARA library to be installed")]
    public async Task ScanAsync_WithEmptyData_ShouldReturnNoMatch()
    {
        // Arrange
        using var scanner = new YARAScanner(_mockLogger.Object, _testRulesPath);
        var emptyData = Array.Empty<byte>();

        // Act
        var result = await scanner.ScanAsync(emptyData);

        // Assert
        result.Should().NotBeNull();
        result.IsMatch.Should().BeFalse();
    }

    [Fact(Skip = "Requires native YARA library to be installed")]
    public void ReloadRules_ShouldSucceed()
    {
        // Arrange
        using var scanner = new YARAScanner(_mockLogger.Object, _testRulesPath);

        // Act
        Action act = () => scanner.ReloadRules();

        // Assert
        act.Should().NotThrow();
    }
}
