using NonIpFileDelivery.Security;
using FluentAssertions;
using Xunit;

namespace NonIPFileDelivery.Tests;

/// <summary>
/// セキュリティインスペクターのユニットテスト
/// </summary>
public class SecurityInspectorTests
{
    [Fact]
    public void ScanData_WithCleanData_ShouldReturnFalse()
    {
        // Arrange
        var inspector = new SecurityInspector();
        var cleanData = "This is normal text content"u8.ToArray();

        // Act
        var isThreat = inspector.ScanData(cleanData, "test.txt");

        // Assert
        isThreat.Should().BeFalse();
    }

    [Fact]
    public void ScanData_WithSuspiciousPattern_ShouldReturnTrue()
    {
        // Arrange
        var inspector = new SecurityInspector();
        var maliciousData = "eval(some_dangerous_code)"u8.ToArray();

        // Act
        var isThreat = inspector.ScanData(maliciousData, "suspicious.txt");

        // Assert
        isThreat.Should().BeTrue();
    }

    [Fact]
    public void ScanData_WithPathTraversal_ShouldReturnTrue()
    {
        // Arrange
        var inspector = new SecurityInspector();
        var maliciousData = "../../../etc/passwd"u8.ToArray();

        // Act
        var isThreat = inspector.ScanData(maliciousData, "traversal.txt");

        // Assert
        isThreat.Should().BeTrue();
    }

    [Fact]
    public void ScanData_WithEmptyData_ShouldReturnFalse()
    {
        // Arrange
        var inspector = new SecurityInspector();
        var emptyData = Array.Empty<byte>();

        // Act
        var isThreat = inspector.ScanData(emptyData, "empty.txt");

        // Assert
        isThreat.Should().BeFalse();
    }

    [Fact]
    public void ValidateFtpCommand_WithValidCommand_ShouldReturnFalse()
    {
        // Arrange
        var inspector = new SecurityInspector();

        // Act
        var isInvalid = inspector.ValidateFtpCommand("USER anonymous");

        // Assert
        isInvalid.Should().BeFalse();
    }

    [Fact]
    public void ValidateFtpCommand_WithInvalidCommand_ShouldReturnTrue()
    {
        // Arrange
        var inspector = new SecurityInspector();

        // Act
        var isInvalid = inspector.ValidateFtpCommand("MALICIOUS_CMD");

        // Assert
        isInvalid.Should().BeTrue();
    }

    [Fact]
    public void ValidateFtpCommand_WithCommandInjection_ShouldReturnTrue()
    {
        // Arrange
        var inspector = new SecurityInspector();

        // Act
        var isInvalid = inspector.ValidateFtpCommand("USER admin && rm -rf /");

        // Assert
        isInvalid.Should().BeTrue();
    }

    [Fact]
    public void ScanFile_WithNonExistentFile_ShouldReturnFalse()
    {
        // Arrange
        var inspector = new SecurityInspector();

        // Act
        var isThreat = inspector.ScanFile("/tmp/nonexistent_file.txt");

        // Assert
        isThreat.Should().BeFalse();
    }
}
