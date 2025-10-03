using NonIpFileDelivery.Security;
using FluentAssertions;
using Xunit;

namespace NonIPFileDelivery.Tests;

/// <summary>
/// AES-256-GCM暗号化エンジンのユニットテスト
/// </summary>
public class CryptoEngineTests
{
    [Fact]
    public void Encrypt_ShouldProduceValidCiphertext()
    {
        // Arrange
        var engine = new CryptoEngine("test_password_123");
        var plaintext = "Hello, Non-IP File Delivery!"u8.ToArray();

        // Act
        var ciphertext = engine.Encrypt(plaintext);

        // Assert
        ciphertext.Should().NotBeNull();
        ciphertext.Length.Should().BeGreaterThan(plaintext.Length); // Should include nonce and tag
    }

    [Fact]
    public void Decrypt_ShouldRecoverOriginalPlaintext()
    {
        // Arrange
        var engine = new CryptoEngine("test_password_123");
        var plaintext = "Hello, Non-IP File Delivery!"u8.ToArray();
        var ciphertext = engine.Encrypt(plaintext);

        // Act
        var decrypted = engine.Decrypt(ciphertext);

        // Assert
        decrypted.Should().Equal(plaintext);
    }

    [Fact]
    public void Encrypt_WithDifferentPasswords_ShouldProduceDifferentCiphertexts()
    {
        // Arrange
        var engine1 = new CryptoEngine("password1");
        var engine2 = new CryptoEngine("password2");
        var plaintext = "Test data"u8.ToArray();

        // Act
        var ciphertext1 = engine1.Encrypt(plaintext);
        var ciphertext2 = engine2.Encrypt(plaintext);

        // Assert
        ciphertext1.Should().NotEqual(ciphertext2);
    }

    [Fact]
    public void Decrypt_WithWrongPassword_ShouldThrowException()
    {
        // Arrange
        var engine1 = new CryptoEngine("correct_password");
        var engine2 = new CryptoEngine("wrong_password");
        var plaintext = "Secret data"u8.ToArray();
        var ciphertext = engine1.Encrypt(plaintext);

        // Act & Assert
        var action = () => engine2.Decrypt(ciphertext);
        action.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    [Fact]
    public void Encrypt_TwiceWithSameEngine_ShouldProduceDifferentCiphertexts()
    {
        // Arrange
        var engine = new CryptoEngine("test_password");
        var plaintext = "Test data"u8.ToArray();

        // Act
        var ciphertext1 = engine.Encrypt(plaintext);
        var ciphertext2 = engine.Encrypt(plaintext);

        // Assert - Due to different nonces, ciphertexts should differ
        ciphertext1.Should().NotEqual(ciphertext2);
    }

    [Fact]
    public void Encrypt_WithEmptyData_ShouldThrowException()
    {
        // Arrange
        var engine = new CryptoEngine("test_password");
        var emptyData = Array.Empty<byte>();

        // Act & Assert
        var action = () => engine.Encrypt(emptyData);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Dispose_ShouldNotThrowException()
    {
        // Arrange
        var engine = new CryptoEngine("test_password");

        // Act & Assert
        var action = () => engine.Dispose();
        action.Should().NotThrow();
    }
}
