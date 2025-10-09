# Non-IP File Delivery - Test Suite

## Overview

This directory contains the automated test suite for the Non-IP File Delivery System. The tests are implemented using xUnit, FluentAssertions, and Moq.

For a comprehensive list of all mocked interfaces and their usage, see the [Mock List Documentation](../docs/mock-list.md).

## Test Structure

```
tests/
└── NonIPFileDelivery.Tests/
    ├── CryptoEngineTests.cs           # AES-256-GCM encryption tests
    ├── SecurityInspectorTests.cs      # Security validation tests
    └── SecureEthernetFrameTests.cs    # Frame serialization tests
```

## Running Tests

### Run All Tests
```bash
dotnet test
```

### Run Tests with Verbose Output
```bash
dotnet test --logger "console;verbosity=detailed"
```

### Run Specific Test Class
```bash
dotnet test --filter "FullyQualifiedName~CryptoEngineTests"
```

### Run Tests with Coverage (requires additional tools)
```bash
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=opencover
```

## Test Results (v3.1)

### Overall Statistics
- **Total Tests**: 20
- **Passing**: 19 ✅
- **Failing**: 0 ✅
- **Skipped**: 1 ⏭️
- **Success Rate**: 95%

### Test Breakdown

#### CryptoEngineTests (7/7 ✅)
1. ✅ Encrypt_ShouldProduceValidCiphertext
2. ✅ Decrypt_ShouldRecoverOriginalPlaintext
3. ✅ Encrypt_WithDifferentPasswords_ShouldProduceDifferentCiphertexts
4. ✅ Decrypt_WithWrongPassword_ShouldThrowException
5. ✅ Encrypt_TwiceWithSameEngine_ShouldProduceDifferentCiphertexts
6. ✅ Encrypt_WithEmptyData_ShouldThrowException
7. ✅ Dispose_ShouldNotThrowException

#### SecurityInspectorTests (8/8 ✅)
1. ✅ ScanData_WithCleanData_ShouldReturnFalse
2. ✅ ScanData_WithSuspiciousPattern_ShouldReturnTrue
3. ✅ ScanData_WithPathTraversal_ShouldReturnTrue
4. ✅ ScanData_WithEmptyData_ShouldReturnFalse
5. ✅ ValidateFtpCommand_WithValidCommand_ShouldReturnFalse
6. ✅ ValidateFtpCommand_WithInvalidCommand_ShouldReturnTrue
7. ✅ ValidateFtpCommand_WithCommandInjection_ShouldReturnTrue
8. ✅ ScanFile_WithNonExistentFile_ShouldReturnFalse

#### SecureEthernetFrameTests (4/5)
1. ✅ CreateEncrypted_ShouldCreateValidFrame
2. ✅ Serialize_ThenDeserialize_ShouldRecoverOriginalFrame
3. ⏭️ DecryptPayload_ShouldRecoverOriginalData (Skipped - Known Bug)
4. ✅ CreateEncrypted_WithEmptyPayload_ShouldThrowException
5. ✅ DecryptPayload_WithWrongKey_ShouldThrowException

## Known Issues

### SecureEthernetFrame Decryption Bug
- **Test**: `DecryptPayload_ShouldRecoverOriginalData`
- **Status**: Skipped
- **Issue**: Header modification after encryption causes authentication failure
- **Details**: The `PayloadLength` field is set after encryption, but the associated data for authentication includes the header. This causes a mismatch during decryption.
- **Impact**: High - affects core frame encryption/decryption functionality
- **Workaround**: None - requires code fix
- **Fix Required**: Refactor `SecureEthernetFrame.CreateEncrypted` to finalize header before encryption

## Test Dependencies

### NuGet Packages
- **xUnit**: 2.4.2+ - Test framework
- **FluentAssertions**: 8.7.1+ - Assertion library
- **Moq**: 4.20.72+ - Mocking framework
- **Microsoft.NET.Test.Sdk**: Latest - Test SDK

### Project References
- NonIPFileDelivery - Main project under test

## Writing New Tests

### Test Class Template
```csharp
using NonIpFileDelivery.YourNamespace;
using FluentAssertions;
using Xunit;

namespace NonIPFileDelivery.Tests;

public class YourClassTests
{
    [Fact]
    public void MethodName_Scenario_ExpectedBehavior()
    {
        // Arrange
        var sut = new YourClass();
        
        // Act
        var result = sut.MethodToTest();
        
        // Assert
        result.Should().Be(expectedValue);
    }
}
```

### Best Practices
1. **Use Descriptive Names**: `MethodName_Scenario_ExpectedBehavior`
2. **Follow AAA Pattern**: Arrange, Act, Assert
3. **One Assert Per Test**: Focus on single behavior
4. **Use FluentAssertions**: More readable assertions
5. **Mock External Dependencies**: Use Moq for isolation
6. **Test Edge Cases**: Empty data, null values, boundary conditions
7. **Document Known Issues**: Use Skip attribute with reason

## Future Test Plans

### Phase 5: Integration Tests
- [ ] End-to-end FTP proxy test
- [ ] End-to-end SFTP proxy test
- [ ] End-to-end PostgreSQL proxy test
- [ ] Multi-protocol concurrent test
- [ ] Network failure recovery test

### Phase 5: Performance Tests
- [ ] 2Gbps throughput test
- [ ] 10ms latency test
- [ ] Memory usage test
- [ ] CPU usage test
- [ ] Concurrent connection test

### Phase 6: Security Tests
- [ ] YARA malware detection test (when fully implemented)
- [ ] ClamAV integration test
- [ ] SQL injection detection test
- [ ] Command injection detection test
- [ ] Path traversal detection test

## CI/CD Integration

### GitHub Actions (Planned)
```yaml
name: Tests
on: [push, pull_request]
jobs:
  test:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v2
      - name: Setup .NET
        uses: actions/setup-dotnet@v1
        with:
          dotnet-version: 8.0.x
      - name: Run tests
        run: dotnet test --logger "trx;LogFileName=test-results.trx"
      - name: Publish test results
        uses: dorny/test-reporter@v1
        if: always()
        with:
          name: Test Results
          path: '**/test-results.trx'
          reporter: dotnet-trx
```

## Troubleshooting

### Tests Not Running
1. Ensure .NET 8.0 SDK is installed
2. Restore NuGet packages: `dotnet restore`
3. Rebuild solution: `dotnet build`

### Tests Failing After Code Changes
1. Review breaking changes in modified code
2. Update tests to match new behavior
3. Check for new dependencies

### Performance Issues
1. Run tests in Release mode for better performance
2. Consider parallelizing tests
3. Profile test execution time

## Contributing

When adding new features:
1. Write tests first (TDD approach)
2. Ensure all existing tests still pass
3. Aim for 80%+ code coverage
4. Document any skipped tests with reason

## License

See main project LICENSE file (Sushi-Ware).

---

**Last Updated**: 2025年1月 (v3.1)
**Test Framework**: xUnit 2.4.2+
**Success Rate**: 95% (19/20 passing)
