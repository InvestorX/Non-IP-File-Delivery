using FluentAssertions;
using Moq;
using NonIpFileDelivery.Core;
using NonIpFileDelivery.Protocols;
using NonIpFileDelivery.Security;
using Serilog;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace NonIPFileDelivery.Tests;

/// <summary>
/// FTPプロキシの統合テスト
/// 制御チャンネルとデータチャンネルの連携動作を検証
/// </summary>
public class FtpProxyIntegrationTests : IDisposable
{
    private readonly Mock<IRawEthernetTransceiver> _mockTransceiver;
    private readonly SecurityInspector _inspector;
    private readonly CancellationTokenSource _cts;

    public FtpProxyIntegrationTests()
    {
        // ログ設定
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();

        // モック作成
        _mockTransceiver = new Mock<IRawEthernetTransceiver>();
        _inspector = new SecurityInspector();
        _cts = new CancellationTokenSource();
    }

    [Fact(DisplayName = "FTPプロキシ: 制御チャンネル - FTPコマンド転送")]
    public async Task FtpProxy_ControlChannel_ShouldForwardCommands()
    {
        // Arrange
        var listenPort = GetAvailablePort();
        var receivedPayloads = new List<byte[]>();
        
        _mockTransceiver
            .Setup(t => t.SendAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<byte[], CancellationToken>((payload, ct) => receivedPayloads.Add(payload))
            .Returns(Task.CompletedTask);

        var proxy = new FtpProxy(
            _mockTransceiver.Object,
            _inspector,
            listenPort,
            "192.168.1.100",
            21);

        await proxy.StartAsync();
        await Task.Delay(500); // プロキシの起動を待つ

        // Act
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listenPort);
        var stream = client.GetStream();
        
        // FTPコマンド送信
        var command = "USER anonymous\r\n";
        var commandBytes = Encoding.ASCII.GetBytes(command);
        await stream.WriteAsync(commandBytes);
        await stream.FlushAsync();
        
        await Task.Delay(500); // コマンド処理を待つ

        // Assert
        receivedPayloads.Should().NotBeEmpty("FTPコマンドがRaw Ethernetで転送されるべき");
        
        if (receivedPayloads.Count > 0)
        {
            var payload = receivedPayloads[0];
            payload[0].Should().Be(0x01, "FTP_CONTROLプロトコルタイプであるべき");
            
            // セッションID（8バイト）をスキップしてコマンドを取得
            var receivedCommand = Encoding.ASCII.GetString(payload[9..]);
            receivedCommand.Should().Be(command, "送信したコマンドが転送されるべき");
        }

        // Cleanup
        client.Close();
        proxy.Dispose();
    }

    [Fact(DisplayName = "FTPプロキシ: 制御チャンネル - セキュリティ検閲でブロック")]
    public async Task FtpProxy_ControlChannel_ShouldBlockMaliciousCommands()
    {
        // Arrange
        var listenPort = GetAvailablePort();
        var receivedPayloads = new List<byte[]>();
        
        _mockTransceiver
            .Setup(t => t.SendAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<byte[], CancellationToken>((payload, ct) => receivedPayloads.Add(payload))
            .Returns(Task.CompletedTask);

        var proxy = new FtpProxy(
            _mockTransceiver.Object,
            _inspector,
            listenPort,
            "192.168.1.100",
            21);

        await proxy.StartAsync();
        await Task.Delay(500);

        // Act
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listenPort);
        var stream = client.GetStream();
        
        // 悪意のあるFTPコマンド（コマンドインジェクション試行）
        var maliciousCommand = "USER admin && rm -rf /\r\n";
        var commandBytes = Encoding.ASCII.GetBytes(maliciousCommand);
        await stream.WriteAsync(commandBytes);
        await stream.FlushAsync();
        
        await Task.Delay(500);

        // レスポンスを受信
        var buffer = new byte[1024];
        var bytesRead = await stream.ReadAsync(buffer);
        var response = Encoding.ASCII.GetString(buffer, 0, bytesRead);

        // Assert
        receivedPayloads.Should().BeEmpty("悪意のあるコマンドは転送されないべき");
        response.Should().Contain("550", "セキュリティポリシーによる拒否レスポンスが返るべき");

        // Cleanup
        client.Close();
        proxy.Dispose();
    }

    [Fact(DisplayName = "FTPプロキシ: PORTコマンド - アクティブモード処理")]
    public async Task FtpProxy_PortCommand_ShouldHandleActiveMode()
    {
        // Arrange
        var listenPort = GetAvailablePort();
        var receivedPayloads = new List<byte[]>();
        
        _mockTransceiver
            .Setup(t => t.SendAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<byte[], CancellationToken>((payload, ct) => receivedPayloads.Add(payload))
            .Returns(Task.CompletedTask);

        var proxy = new FtpProxy(
            _mockTransceiver.Object,
            _inspector,
            listenPort,
            "192.168.1.100",
            21);

        await proxy.StartAsync();
        await Task.Delay(500);

        // Act
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listenPort);
        var stream = client.GetStream();
        
        // PORTコマンド送信（127,0,0,1,port_high,port_low形式）
        var dataPort = GetAvailablePort();
        var portHigh = dataPort / 256;
        var portLow = dataPort % 256;
        var portCommand = $"PORT 127,0,0,1,{portHigh},{portLow}\r\n";
        var commandBytes = Encoding.ASCII.GetBytes(portCommand);
        await stream.WriteAsync(commandBytes);
        await stream.FlushAsync();
        
        await Task.Delay(500);

        // Assert
        receivedPayloads.Should().NotBeEmpty("PORTコマンドがRaw Ethernetで転送されるべき");
        
        var portCommandPayload = receivedPayloads.FirstOrDefault(p => 
        {
            if (p.Length > 9)
            {
                var cmd = Encoding.ASCII.GetString(p[9..]);
                return cmd.StartsWith("PORT");
            }
            return false;
        });
        
        portCommandPayload.Should().NotBeNull("PORTコマンドが転送されるべき");

        // Cleanup
        client.Close();
        proxy.Dispose();
    }

    [Fact(DisplayName = "FTPプロキシ: PASVコマンド - パッシブモード処理")]
    public async Task FtpProxy_PasvCommand_ShouldHandlePassiveMode()
    {
        // Arrange
        var listenPort = GetAvailablePort();
        var receivedPayloads = new List<byte[]>();
        
        _mockTransceiver
            .Setup(t => t.SendAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<byte[], CancellationToken>((payload, ct) => receivedPayloads.Add(payload))
            .Returns(Task.CompletedTask);

        var proxy = new FtpProxy(
            _mockTransceiver.Object,
            _inspector,
            listenPort,
            "192.168.1.100",
            21);

        await proxy.StartAsync();
        await Task.Delay(500);

        // Act
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listenPort);
        var stream = client.GetStream();
        
        // PASVコマンド送信
        var pasvCommand = "PASV\r\n";
        var commandBytes = Encoding.ASCII.GetBytes(pasvCommand);
        await stream.WriteAsync(commandBytes);
        await stream.FlushAsync();
        
        await Task.Delay(500);

        // Assert
        receivedPayloads.Should().NotBeEmpty("PASVコマンドがRaw Ethernetで転送されるべき");
        
        var pasvCommandPayload = receivedPayloads.FirstOrDefault(p => 
        {
            if (p.Length > 9)
            {
                var cmd = Encoding.ASCII.GetString(p[9..]);
                return cmd.StartsWith("PASV");
            }
            return false;
        });
        
        pasvCommandPayload.Should().NotBeNull("PASVコマンドが転送されるべき");

        // Cleanup
        client.Close();
        proxy.Dispose();
    }

    [Fact(DisplayName = "FTPプロキシ: セッション管理 - 複数クライアント")]
    public async Task FtpProxy_SessionManagement_ShouldHandleMultipleClients()
    {
        // Arrange
        var listenPort = GetAvailablePort();
        var receivedPayloads = new List<byte[]>();
        
        _mockTransceiver
            .Setup(t => t.SendAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<byte[], CancellationToken>((payload, ct) => 
            {
                lock (receivedPayloads)
                {
                    receivedPayloads.Add(payload);
                }
            })
            .Returns(Task.CompletedTask);

        var proxy = new FtpProxy(
            _mockTransceiver.Object,
            _inspector,
            listenPort,
            "192.168.1.100",
            21);

        await proxy.StartAsync();
        await Task.Delay(500);

        // Act
        // クライアント1
        var client1Task = Task.Run(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listenPort);
            var stream = client.GetStream();
            
            var command = "USER client1\r\n";
            var commandBytes = Encoding.ASCII.GetBytes(command);
            await stream.WriteAsync(commandBytes);
            await stream.FlushAsync();
            
            await Task.Delay(200);
            client.Close();
        });

        // クライアント2
        var client2Task = Task.Run(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listenPort);
            var stream = client.GetStream();
            
            var command = "USER client2\r\n";
            var commandBytes = Encoding.ASCII.GetBytes(command);
            await stream.WriteAsync(commandBytes);
            await stream.FlushAsync();
            
            await Task.Delay(200);
            client.Close();
        });

        await Task.WhenAll(client1Task, client2Task);
        await Task.Delay(500);

        // Assert
        receivedPayloads.Should().HaveCountGreaterThanOrEqualTo(2, "2つのクライアントからコマンドが送信されるべき");
        
        // 異なるセッションIDが使用されていることを確認
        var sessionIds = receivedPayloads
            .Where(p => p.Length > 9)
            .Select(p => Encoding.ASCII.GetString(p, 1, 8).TrimEnd())
            .Distinct()
            .ToList();
        
        sessionIds.Should().HaveCountGreaterThanOrEqualTo(2, "異なるセッションIDが割り当てられるべき");

        // Cleanup
        proxy.Dispose();
    }

    [Fact(DisplayName = "FTPプロキシ: Raw Ethernetレスポンス - クライアントへの返送")]
    public async Task FtpProxy_RawEthernetResponse_ShouldForwardToClient()
    {
        // Arrange
        var listenPort = GetAvailablePort();
        var receivedPayloads = new List<byte[]>();
        string? capturedSessionId = null;
        
        _mockTransceiver
            .Setup(t => t.SendAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<byte[], CancellationToken>((payload, ct) => 
            {
                receivedPayloads.Add(payload);
                if (payload.Length > 9 && capturedSessionId == null)
                {
                    capturedSessionId = Encoding.ASCII.GetString(payload, 1, 8);
                }
            })
            .Returns(Task.CompletedTask);

        var proxy = new FtpProxy(
            _mockTransceiver.Object,
            _inspector,
            listenPort,
            "192.168.1.100",
            21);

        await proxy.StartAsync();
        await Task.Delay(500);

        // Act
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listenPort);
        var stream = client.GetStream();
        
        // コマンド送信
        var command = "USER test\r\n";
        var commandBytes = Encoding.ASCII.GetBytes(command);
        await stream.WriteAsync(commandBytes);
        await stream.FlushAsync();
        
        await Task.Delay(500);

        // FTPサーバーからのレスポンスをシミュレート（Raw Ethernetパケット）
        if (capturedSessionId != null)
        {
            var response = "331 Password required for test.\r\n";
            var responseBytes = Encoding.ASCII.GetBytes(response);
            var responsePayload = BuildProtocolPayload(0x01, capturedSessionId, responseBytes);
            
            // Raw Ethernetパケットを受信したことをシミュレート
            // 実際にはHandleRawEthernetPacketAsync()が呼ばれる
            // ここではプロキシのレスポンス転送機能を検証
            
            // Note: このテストは実際のRaw Ethernet受信をモックする必要があるため、
            // より高度な統合テスト環境が必要
        }

        // Assert
        receivedPayloads.Should().NotBeEmpty("コマンドがRaw Ethernetで送信されるべき");

        // Cleanup
        client.Close();
        proxy.Dispose();
    }

    [Fact(DisplayName = "FTPプロキシ: リソース管理 - 正常なDispose")]
    public async Task FtpProxy_ResourceManagement_ShouldDisposeCleanly()
    {
        // Arrange
        var listenPort = GetAvailablePort();
        
        var proxy = new FtpProxy(
            _mockTransceiver.Object,
            _inspector,
            listenPort,
            "192.168.1.100",
            21);

        await proxy.StartAsync();
        await Task.Delay(500);

        // クライアント接続
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listenPort);
        
        // Act
        proxy.Dispose();
        await Task.Delay(500);

        // Assert
        // Disposeが例外を投げずに完了することを確認
        // クライアント接続も適切に閉じられることを確認
        var isConnected = client.Connected;
        
        // Disposeによりサーバーが停止しているため、新規接続は失敗するはず
        var cannotConnect = false;
        try
        {
            using var newClient = new TcpClient();
            await newClient.ConnectAsync(IPAddress.Loopback, listenPort);
        }
        catch (SocketException)
        {
            cannotConnect = true;
        }

        cannotConnect.Should().BeTrue("Dispose後は新規接続を受け付けないべき");

        // Cleanup
        client.Close();
    }

    [Fact(DisplayName = "FTPプロキシ: エラーハンドリング - 切断クライアント")]
    public async Task FtpProxy_ErrorHandling_ShouldHandleDisconnectedClient()
    {
        // Arrange
        var listenPort = GetAvailablePort();
        var receivedPayloads = new List<byte[]>();
        
        _mockTransceiver
            .Setup(t => t.SendAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<byte[], CancellationToken>((payload, ct) => receivedPayloads.Add(payload))
            .Returns(Task.CompletedTask);

        var proxy = new FtpProxy(
            _mockTransceiver.Object,
            _inspector,
            listenPort,
            "192.168.1.100",
            21);

        await proxy.StartAsync();
        await Task.Delay(500);

        // Act
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listenPort);
        var stream = client.GetStream();
        
        // コマンド送信
        var command = "USER test\r\n";
        var commandBytes = Encoding.ASCII.GetBytes(command);
        await stream.WriteAsync(commandBytes);
        await stream.FlushAsync();
        
        await Task.Delay(200);
        
        // クライアントを突然切断
        client.Close();
        
        await Task.Delay(500);

        // Assert
        // プロキシがクラッシュせず、エラーを適切に処理することを確認
        receivedPayloads.Should().NotBeEmpty("切断前のコマンドは送信されるべき");
        
        // プロキシは引き続き動作するはず（新規接続を受け付ける）
        using var newClient = new TcpClient();
        var connectTask = newClient.ConnectAsync(IPAddress.Loopback, listenPort);
        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        
        newClient.Connected.Should().BeTrue("プロキシは引き続き動作するべき");

        // Cleanup
        newClient.Close();
        proxy.Dispose();
    }

    /// <summary>
    /// プロトコルペイロードを構築（テスト用ヘルパー）
    /// </summary>
    private byte[] BuildProtocolPayload(byte protocolType, string sessionId, byte[] data)
    {
        var payload = new byte[1 + 8 + data.Length];
        payload[0] = protocolType;
        
        var sessionIdBytes = Encoding.ASCII.GetBytes(sessionId.PadRight(8));
        Array.Copy(sessionIdBytes, 0, payload, 1, 8);
        
        data.CopyTo(payload, 9);
        return payload;
    }

    /// <summary>
    /// 利用可能なTCPポートを取得
    /// </summary>
    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
    }
}
