# テスト実装ガイド

このドキュメントは、Functional Design Document (functionaldesign.md) に記載されているテストを実装するためのガイドです。

---

## 📋 概要

Functional Design Document では、以下の6つのテストクラスが「実装済み ✅」として記載されていますが、実際には `tests/` ディレクトリが存在しません。このガイドでは、これらのテストを実装する手順を説明します。

| テストクラス | 対象サービス | 優先度 |
|-------------|-------------|--------|
| SecurityServiceTests.cs | SecurityService | 🔴 高 |
| ProtocolAnalyzerTests.cs | ProtocolAnalyzer | 🔴 高 |
| SessionManagerTests.cs | SessionManager | 🔴 高 |
| FragmentationServiceTests.cs | FragmentationService | 🔴 高 |
| RetransmissionServiceTests.cs | RetransmissionService | 🟡 中 |
| QoSServiceTests.cs | QoSService | 🟡 中 |

---

## 🚀 セットアップ

### 1. テストプロジェクトの作成

```bash
# プロジェクトルートで実行
cd /home/runner/work/Non-IP-File-Delivery/Non-IP-File-Delivery

# testsディレクトリを作成
mkdir -p tests

# xUnitテストプロジェクトを作成
dotnet new xunit -n NonIPFileDelivery.Tests -o tests/NonIPFileDelivery.Tests

# ソリューションに追加
dotnet sln add tests/NonIPFileDelivery.Tests/NonIPFileDelivery.Tests.csproj

# プロジェクト参照を追加
cd tests/NonIPFileDelivery.Tests
dotnet add reference ../../src/NonIPFileDelivery/NonIPFileDelivery.csproj
```

### 2. 必要なNuGetパッケージの追加

```bash
cd tests/NonIPFileDelivery.Tests

# Moq（モックライブラリ）
dotnet add package Moq --version 4.20.70

# FluentAssertions（アサーションライブラリ）
dotnet add package FluentAssertions --version 6.12.0

# Bogus（テストデータ生成）
dotnet add package Bogus --version 35.0.1

# xUnit拡張
dotnet add package xunit.runner.visualstudio --version 2.5.5
dotnet add package coverlet.collector --version 6.0.0
```

### 3. ディレクトリ構造

```
tests/
└── NonIPFileDelivery.Tests/
    ├── NonIPFileDelivery.Tests.csproj
    ├── SecurityServiceTests.cs
    ├── ProtocolAnalyzerTests.cs
    ├── SessionManagerTests.cs
    ├── FragmentationServiceTests.cs
    ├── RetransmissionServiceTests.cs
    ├── QoSServiceTests.cs
    └── Helpers/
        ├── MockLoggingService.cs
        └── TestDataGenerator.cs
```

---

## 📝 テスト実装例

### 1. SecurityServiceTests.cs

```csharp
using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;
using Xunit;

namespace NonIPFileDelivery.Tests
{
    public class SecurityServiceTests
    {
        private readonly Mock<ILoggingService> _mockLogger;
        private readonly SecurityService _securityService;

        public SecurityServiceTests()
        {
            _mockLogger = new Mock<ILoggingService>();
            _securityService = new SecurityService(_mockLogger.Object);
        }

        [Fact]
        public void Constructor_WithNullLogger_ThrowsArgumentNullException()
        {
            // Arrange & Act
            Action act = () => new SecurityService(null!);

            // Assert
            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("logger");
        }

        [Fact]
        public void IsSecurityEnabled_DefaultValue_ReturnsTrue()
        {
            // Arrange & Act
            var result = _securityService.IsSecurityEnabled;

            // Assert
            result.Should().BeTrue();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SetSecurityEnabled_WithValidValue_SetsProperty(bool enabled)
        {
            // Arrange & Act
            _securityService.SetSecurityEnabled(enabled);

            // Assert
            _securityService.IsSecurityEnabled.Should().Be(enabled);
        }

        [Fact]
        public async Task ScanDataAsync_WithValidData_ReturnsSuccessResult()
        {
            // Arrange
            var testData = new byte[] { 0x01, 0x02, 0x03, 0x04 };

            // Act
            var result = await _securityService.ScanDataAsync(testData);

            // Assert
            result.Should().NotBeNull();
            // 詳細なアサーションは実装に依存
        }

        [Fact]
        public async Task ScanDataAsync_WithNullData_ThrowsArgumentNullException()
        {
            // Arrange & Act
            Func<Task> act = async () => await _securityService.ScanDataAsync(null!);

            // Assert
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task ScanDataAsync_WithEmptyData_ReturnsCleanResult()
        {
            // Arrange
            var emptyData = Array.Empty<byte>();

            // Act
            var result = await _securityService.ScanDataAsync(emptyData);

            // Assert
            result.Should().NotBeNull();
            // Clean（脅威なし）の結果を期待
        }

        [Fact]
        public void EncryptData_WithValidKey_ReturnsEncryptedData()
        {
            // Arrange
            var plaintext = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello"

            // Act
            var encrypted = _securityService.EncryptData(plaintext);

            // Assert
            encrypted.Should().NotBeNull();
            encrypted.Should().NotBeEquivalentTo(plaintext);
            encrypted.Length.Should().BeGreaterThan(plaintext.Length); // GCMタグ分増える
        }

        [Fact]
        public void DecryptData_WithValidEncryptedData_ReturnsOriginalData()
        {
            // Arrange
            var original = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F };
            var encrypted = _securityService.EncryptData(original);

            // Act
            var decrypted = _securityService.DecryptData(encrypted);

            // Assert
            decrypted.Should().BeEquivalentTo(original);
        }

        [Fact]
        public void CalculateHMAC_WithValidData_ReturnsConsistentHash()
        {
            // Arrange
            var data = new byte[] { 0x01, 0x02, 0x03 };

            // Act
            var hash1 = _securityService.CalculateHMAC(data);
            var hash2 = _securityService.CalculateHMAC(data);

            // Assert
            hash1.Should().NotBeNullOrEmpty();
            hash1.Should().BeEquivalentTo(hash2); // 同じデータからは同じハッシュ
        }

        [Fact]
        public void VerifyHMAC_WithValidHash_ReturnsTrue()
        {
            // Arrange
            var data = new byte[] { 0x01, 0x02, 0x03 };
            var hash = _securityService.CalculateHMAC(data);

            // Act
            var isValid = _securityService.VerifyHMAC(data, hash);

            // Assert
            isValid.Should().BeTrue();
        }

        [Fact]
        public void VerifyHMAC_WithInvalidHash_ReturnsFalse()
        {
            // Arrange
            var data = new byte[] { 0x01, 0x02, 0x03 };
            var wrongHash = "invalid_hash";

            // Act
            var isValid = _securityService.VerifyHMAC(data, wrongHash);

            // Assert
            isValid.Should().BeFalse();
        }
    }
}
```

### 2. ProtocolAnalyzerTests.cs

```csharp
using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;
using Xunit;

namespace NonIPFileDelivery.Tests
{
    public class ProtocolAnalyzerTests
    {
        private readonly Mock<ILoggingService> _mockLogger;
        private readonly ProtocolAnalyzer _protocolAnalyzer;

        public ProtocolAnalyzerTests()
        {
            _mockLogger = new Mock<ILoggingService>();
            _protocolAnalyzer = new ProtocolAnalyzer(_mockLogger.Object);
        }

        [Fact]
        public void GetSupportedProtocols_ReturnsExpectedProtocols()
        {
            // Act
            var protocols = _protocolAnalyzer.GetSupportedProtocols();

            // Assert
            protocols.Should().NotBeNull();
            protocols.Should().Contain(ProtocolType.FTP);
            protocols.Should().Contain(ProtocolType.PostgreSQL);
        }

        [Theory]
        [InlineData(ProtocolType.FTP)]
        [InlineData(ProtocolType.PostgreSQL)]
        public async Task AnalyzeByProtocolAsync_WithValidProtocol_ReturnsResult(ProtocolType protocol)
        {
            // Arrange
            var packetData = CreateTestPacket(protocol);

            // Act
            var result = await _protocolAnalyzer.AnalyzeByProtocolAsync(packetData, protocol);

            // Assert
            result.Should().NotBeNull();
            result.Protocol.Should().Be(protocol);
        }

        [Fact]
        public async Task AnalyzeByProtocolAsync_WithNullData_ThrowsArgumentNullException()
        {
            // Arrange & Act
            Func<Task> act = async () => 
                await _protocolAnalyzer.AnalyzeByProtocolAsync(null!, ProtocolType.FTP);

            // Assert
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task AnalyzeAsync_WithFTPPacket_DetectsProtocol()
        {
            // Arrange
            var ftpPacket = CreateFTPPacket("USER test");

            // Act
            var result = await _protocolAnalyzer.AnalyzeAsync(ftpPacket);

            // Assert
            result.Should().NotBeNull();
            result.Protocol.Should().Be(ProtocolType.FTP);
        }

        [Fact]
        public void GetStatistics_ReturnsValidStatistics()
        {
            // Act
            var stats = _protocolAnalyzer.GetStatistics();

            // Assert
            stats.Should().NotBeNull();
            stats.Should().ContainKey("TotalPackets");
            stats.Should().ContainKey("AnalysisSuccessRate");
        }

        // ヘルパーメソッド
        private byte[] CreateTestPacket(ProtocolType protocol)
        {
            return protocol switch
            {
                ProtocolType.FTP => CreateFTPPacket("USER test"),
                ProtocolType.PostgreSQL => CreatePostgreSQLPacket(),
                _ => new byte[64]
            };
        }

        private byte[] CreateFTPPacket(string command)
        {
            // 簡易的なFTPパケット作成
            var packet = new byte[128];
            // Ethernetヘッダー（14バイト）
            // IPヘッダー（20バイト）
            // TCPヘッダー（20バイト）
            // FTPデータ（残り）
            var commandBytes = System.Text.Encoding.ASCII.GetBytes(command + "\r\n");
            Buffer.BlockCopy(commandBytes, 0, packet, 54, commandBytes.Length);
            return packet;
        }

        private byte[] CreatePostgreSQLPacket()
        {
            // 簡易的なPostgreSQLパケット作成
            return new byte[128];
        }
    }
}
```

### 3. SessionManagerTests.cs

```csharp
using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NonIPFileDelivery.Models;
using Xunit;

namespace NonIPFileDelivery.Tests
{
    public class SessionManagerTests
    {
        private readonly SessionManager _sessionManager;
        private readonly byte[] _testSourceMac = { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        private readonly byte[] _testDestMac = { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };

        public SessionManagerTests()
        {
            _sessionManager = new SessionManager();
        }

        [Fact]
        public void CreateSession_WithValidMacs_ReturnsValidSessionId()
        {
            // Act
            var sessionId = _sessionManager.CreateSession(_testSourceMac, _testDestMac);

            // Assert
            sessionId.Should().NotBe(Guid.Empty);
        }

        [Fact]
        public void CreateSession_WithNullSourceMac_ThrowsArgumentNullException()
        {
            // Arrange & Act
            Action act = () => _sessionManager.CreateSession(null!, _testDestMac);

            // Assert
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void GetSession_WithValidSessionId_ReturnsSession()
        {
            // Arrange
            var sessionId = _sessionManager.CreateSession(_testSourceMac, _testDestMac);

            // Act
            var session = _sessionManager.GetSession(sessionId);

            // Assert
            session.Should().NotBeNull();
            session!.SessionId.Should().Be(sessionId);
            session.SourceMAC.Should().BeEquivalentTo(_testSourceMac);
            session.DestinationMAC.Should().BeEquivalentTo(_testDestMac);
        }

        [Fact]
        public void GetSession_WithInvalidSessionId_ReturnsNull()
        {
            // Arrange
            var invalidSessionId = Guid.NewGuid();

            // Act
            var session = _sessionManager.GetSession(invalidSessionId);

            // Assert
            session.Should().BeNull();
        }

        [Fact]
        public void CloseSession_WithValidSessionId_RemovesSession()
        {
            // Arrange
            var sessionId = _sessionManager.CreateSession(_testSourceMac, _testDestMac);

            // Act
            _sessionManager.CloseSession(sessionId);
            var session = _sessionManager.GetSession(sessionId);

            // Assert
            session.Should().BeNull();
        }

        [Fact]
        public void GetActiveSessions_ReturnsAllActiveSessions()
        {
            // Arrange
            var sessionId1 = _sessionManager.CreateSession(_testSourceMac, _testDestMac);
            var sessionId2 = _sessionManager.CreateSession(_testDestMac, _testSourceMac);

            // Act
            var activeSessions = _sessionManager.GetActiveSessions();

            // Assert
            activeSessions.Should().HaveCount(2);
            activeSessions.Should().Contain(s => s.SessionId == sessionId1);
            activeSessions.Should().Contain(s => s.SessionId == sessionId2);
        }

        [Fact]
        public async Task SessionTimeout_AfterTimeout_SessionIsRemoved()
        {
            // Arrange
            var sessionId = _sessionManager.CreateSession(_testSourceMac, _testDestMac);
            
            // Act
            // セッションタイムアウトが5分に設定されていると仮定
            // テスト用に短いタイムアウトを設定するか、モック時計を使用
            await Task.Delay(100); // 実際のテストでは適切なタイムアウト機構をテスト

            // Assert
            // タイムアウト処理が実装されている場合のテスト
            // var session = _sessionManager.GetSession(sessionId);
            // session.Should().BeNull();
        }

        [Fact]
        public void UpdateSessionActivity_WithValidSessionId_UpdatesLastActivity()
        {
            // Arrange
            var sessionId = _sessionManager.CreateSession(_testSourceMac, _testDestMac);
            var session1 = _sessionManager.GetSession(sessionId);
            var originalLastActivity = session1!.LastActivity;

            // Act
            Task.Delay(100).Wait(); // 時間経過を確保
            _sessionManager.UpdateSessionActivity(sessionId);
            var session2 = _sessionManager.GetSession(sessionId);

            // Assert
            session2!.LastActivity.Should().BeAfter(originalLastActivity);
        }
    }
}
```

### 4. FragmentationServiceTests.cs

```csharp
using System;
using System.Linq;
using FluentAssertions;
using NonIPFileDelivery.Models;
using Xunit;

namespace NonIPFileDelivery.Tests
{
    public class FragmentationServiceTests
    {
        private readonly FragmentationService _fragmentationService;
        private const int MaxFragmentSize = 1450;

        public FragmentationServiceTests()
        {
            _fragmentationService = new FragmentationService();
        }

        [Fact]
        public void FragmentData_WithSmallData_ReturnsSingleFragment()
        {
            // Arrange
            var data = new byte[100];

            // Act
            var fragments = _fragmentationService.FragmentData(data, MaxFragmentSize);

            // Assert
            fragments.Should().HaveCount(1);
            fragments[0].Data.Should().BeEquivalentTo(data);
        }

        [Theory]
        [InlineData(1450)]
        [InlineData(2900)]
        [InlineData(5800)]
        public void FragmentData_WithLargeData_ReturnsMultipleFragments(int dataSize)
        {
            // Arrange
            var data = new byte[dataSize];
            var expectedFragments = (int)Math.Ceiling((double)dataSize / MaxFragmentSize);

            // Act
            var fragments = _fragmentationService.FragmentData(data, MaxFragmentSize);

            // Assert
            fragments.Should().HaveCount(expectedFragments);
            fragments.Sum(f => f.Data.Length).Should().Be(dataSize);
        }

        [Fact]
        public void FragmentData_WithNullData_ThrowsArgumentNullException()
        {
            // Arrange & Act
            Action act = () => _fragmentationService.FragmentData(null!, MaxFragmentSize);

            // Assert
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void ReassembleFragments_WithValidFragments_ReturnsOriginalData()
        {
            // Arrange
            var originalData = new byte[5000];
            new Random(42).NextBytes(originalData);
            var fragments = _fragmentationService.FragmentData(originalData, MaxFragmentSize);

            // Act
            var reassembled = _fragmentationService.ReassembleFragments(fragments);

            // Assert
            reassembled.Should().BeEquivalentTo(originalData);
        }

        [Fact]
        public void ReassembleFragments_WithMissingFragment_ThrowsException()
        {
            // Arrange
            var originalData = new byte[5000];
            var fragments = _fragmentationService.FragmentData(originalData, MaxFragmentSize).ToList();
            fragments.RemoveAt(1); // 1つのフラグメントを削除

            // Act
            Action act = () => _fragmentationService.ReassembleFragments(fragments);

            // Assert
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*missing*");
        }

        [Fact]
        public void TryReassemble_WithCompleteFragments_ReturnsTrue()
        {
            // Arrange
            var originalData = new byte[3000];
            var fragments = _fragmentationService.FragmentData(originalData, MaxFragmentSize);
            var sessionId = Guid.NewGuid();

            // Act
            bool isComplete = false;
            byte[]? result = null;
            foreach (var fragment in fragments)
            {
                var reassembly = _fragmentationService.TryReassemble(fragment);
                if (reassembly.IsComplete)
                {
                    isComplete = true;
                    result = reassembly.Data;
                    break;
                }
            }

            // Assert
            isComplete.Should().BeTrue();
            result.Should().BeEquivalentTo(originalData);
        }

        [Fact]
        public void FragmentData_MaxFileSize_WithTooLargeData_ThrowsException()
        {
            // Arrange
            var tooLargeData = new byte[1_074_000_000]; // > 1GB

            // Act
            Action act = () => _fragmentationService.FragmentData(tooLargeData, MaxFragmentSize);

            // Assert
            act.Should().Throw<ArgumentException>()
                .WithMessage("*exceeds maximum*");
        }
    }
}
```

---

## 🧪 テスト実行

### ビルドとテスト実行

```bash
# テストプロジェクトのビルド
dotnet build tests/NonIPFileDelivery.Tests/NonIPFileDelivery.Tests.csproj

# すべてのテストを実行
dotnet test tests/NonIPFileDelivery.Tests/NonIPFileDelivery.Tests.csproj

# カバレッジレポート付き実行
dotnet test tests/NonIPFileDelivery.Tests/NonIPFileDelivery.Tests.csproj \
    --collect:"XPlat Code Coverage"

# 詳細出力
dotnet test tests/NonIPFileDelivery.Tests/NonIPFileDelivery.Tests.csproj \
    --logger "console;verbosity=detailed"
```

### CI/CD統合（GitHub Actions例）

`.github/workflows/test.yml`:

```yaml
name: Tests

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main ]

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v3
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: 8.0.x
    
    - name: Restore dependencies
      run: dotnet restore
    
    - name: Build
      run: dotnet build --no-restore
    
    - name: Test
      run: dotnet test --no-build --verbosity normal --collect:"XPlat Code Coverage"
    
    - name: Upload coverage to Codecov
      uses: codecov/codecov-action@v3
      with:
        files: ./coverage.cobertura.xml
```

---

## 📊 カバレッジ目標

| テストクラス | 目標カバレッジ | ステータス |
|-------------|--------------|-----------|
| SecurityServiceTests | 95% | 実装待ち |
| ProtocolAnalyzerTests | 92% | 実装待ち |
| SessionManagerTests | 94% | 実装待ち |
| FragmentationServiceTests | 96% | 実装待ち |
| RetransmissionServiceTests | 93% | 実装待ち |
| QoSServiceTests | 95% | 実装待ち |

---

## 📝 ベストプラクティス

1. **AAA パターン**
   - Arrange（準備）
   - Act（実行）
   - Assert（検証）

2. **命名規則**
   - `MethodName_Scenario_ExpectedResult`
   - 例: `CreateSession_WithValidMacs_ReturnsValidSessionId`

3. **1テスト1アサーション**
   - 1つのテストで1つの振る舞いのみ検証

4. **依存関係の注入**
   - Moqを使用して依存関係をモック化

5. **テストデータ**
   - Bogusを使用してリアルなテストデータを生成

---

**最終更新**: 2025-01-10
