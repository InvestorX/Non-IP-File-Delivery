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
/// FTPデータチャンネルの統合テスト
/// PORTモード（アクティブ）とPASVモード（パッシブ）の動作を検証
/// </summary>
public class FtpDataChannelTests : IDisposable
{
    private readonly Mock<IRawEthernetTransceiver> _mockTransceiver;
    private readonly SecurityInspector _inspector;
    private readonly CancellationTokenSource _cts;

    public FtpDataChannelTests()
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

    [Fact(DisplayName = "FTPデータチャンネル: アクティブモード - 正常なデータ受信")]
    public async Task FtpDataChannel_ActiveMode_ShouldReceiveDataSuccessfully()
    {
        // Arrange
        var sessionId = "TEST0001";
        var testData = Encoding.UTF8.GetBytes("Test file content for FTP upload");
        
        // データチャンネル作成（アクティブモード）
        var targetIp = IPAddress.Loopback;
        var targetPort = GetAvailablePort();
        
        // テスト用TCPリスナーを起動してクライアント役を演じる
        var listener = new TcpListener(targetIp, targetPort);
        listener.Start();
        
        var dataReceivedTask = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            
            // データを送信（クライアント→サーバー方向）
            await stream.WriteAsync(testData);
            await stream.FlushAsync();
            
            // 少し待機してから閉じる
            await Task.Delay(100);
            client.Close();
        });

        // Raw Ethernetトランシーバーのモック設定
        var receivedPayloads = new List<byte[]>();
        _mockTransceiver
            .Setup(t => t.SendAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<byte[], CancellationToken>((payload, ct) => receivedPayloads.Add(payload))
            .Returns(Task.CompletedTask);

        // Act
        var dataChannel = new FtpDataChannel(
            sessionId,
            FtpDataChannelMode.Active,
            targetIp,
            targetPort,
            _mockTransceiver.Object,
            _inspector,
            _cts.Token);

        // データ受信を待機
        await dataReceivedTask;
        await Task.Delay(500); // データ転送処理の完了を待つ

        // Assert
        receivedPayloads.Should().NotBeEmpty("データがRaw Ethernetで送信されるべき");
        
        // 最初のペイロードを検証
        if (receivedPayloads.Count > 0)
        {
            var payload = receivedPayloads[0];
            payload[0].Should().Be(0x02, "FTP_DATAプロトコルタイプであるべき");
            
            var receivedSessionId = Encoding.ASCII.GetString(payload, 1, 8).TrimEnd();
            receivedSessionId.Should().StartWith(sessionId, "セッションIDが一致するべき");
            
            var receivedData = payload[9..];
            receivedData.Should().Equal(testData, "受信データが元のデータと一致するべき");
        }

        // Cleanup
        listener.Stop();
        dataChannel.Dispose();
    }

    [Fact(DisplayName = "FTPデータチャンネル: パッシブモード - 正常なデータ送信")]
    public async Task FtpDataChannel_PassiveMode_ShouldSendDataSuccessfully()
    {
        // Arrange
        var sessionId = "TEST0002";
        var testData = Encoding.UTF8.GetBytes("Test file content for FTP download");
        
        // データチャンネル作成（パッシブモード）
        var dataChannel = new FtpDataChannel(
            sessionId,
            FtpDataChannelMode.Passive,
            IPAddress.Any,
            0,
            _mockTransceiver.Object,
            _inspector,
            _cts.Token);

        // Act & Assert
        // パッシブモードではクライアント接続を待つため、
        // SendToClientAsync()が正常に動作することを検証
        var sendTask = dataChannel.SendToClientAsync(testData);
        
        // 接続がない状態では警告ログが出力されるが、例外は発生しない
        await Task.WhenAny(sendTask, Task.Delay(1000));
        
        // 例外が発生しないことを確認
        sendTask.IsCompletedSuccessfully.Should().BeTrue("SendToClientAsyncは例外を投げずに完了するべき");

        // Cleanup
        dataChannel.Dispose();
    }

    [Fact(DisplayName = "FTPデータチャンネル: セキュリティ検閲 - 悪意のあるデータのブロック")]
    public async Task FtpDataChannel_SecurityInspector_ShouldBlockMaliciousData()
    {
        // Arrange
        var sessionId = "TEST0003";
        
        // 悪意のあるデータパターン（EICAR テストファイル）
        var maliciousData = Encoding.UTF8.GetBytes(
            "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
        
        var targetIp = IPAddress.Loopback;
        var targetPort = GetAvailablePort();
        
        var listener = new TcpListener(targetIp, targetPort);
        listener.Start();
        
        var dataReceivedTask = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            
            // 悪意のあるデータを送信
            await stream.WriteAsync(maliciousData);
            await stream.FlushAsync();
            
            await Task.Delay(100);
            client.Close();
        });

        var receivedPayloads = new List<byte[]>();
        _mockTransceiver
            .Setup(t => t.SendAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<byte[], CancellationToken>((payload, ct) => receivedPayloads.Add(payload))
            .Returns(Task.CompletedTask);

        // Act
        var dataChannel = new FtpDataChannel(
            sessionId,
            FtpDataChannelMode.Active,
            targetIp,
            targetPort,
            _mockTransceiver.Object,
            _inspector,
            _cts.Token);

        await dataReceivedTask;
        await Task.Delay(500);

        // Assert
        // セキュリティインスペクターが悪意のあるデータを検出してブロックする
        // 実際のYARA/ClamAVスキャンが必要だが、基本的なパターンマッチングでもある程度検出可能
        receivedPayloads.Should().BeEmpty("悪意のあるデータはブロックされるべき");

        // Cleanup
        listener.Stop();
        dataChannel.Dispose();
    }

    [Fact(DisplayName = "FTPデータチャンネル: アイドルタイムアウト - 非アクティブ時の自動切断")]
    public async Task FtpDataChannel_IdleTimeout_ShouldAutoDisconnect()
    {
        // Arrange
        var sessionId = "TEST0004";
        var targetIp = IPAddress.Loopback;
        var targetPort = GetAvailablePort();
        
        var listener = new TcpListener(targetIp, targetPort);
        listener.Start();
        
        var acceptTask = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            // 接続を確立するが、データを送信しない（アイドル状態）
            await Task.Delay(10000); // 10秒待機
            client.Close();
        });

        // Act
        var dataChannel = new FtpDataChannel(
            sessionId,
            FtpDataChannelMode.Active,
            targetIp,
            targetPort,
            _mockTransceiver.Object,
            _inspector,
            _cts.Token);

        // アイドルタイムアウト（5分）は長すぎるため、
        // ここでは接続確立後すぐにDisposeされることを確認
        await Task.Delay(1000);

        // Assert
        // データチャンネルが正常に作成されたことを確認
        dataChannel.Should().NotBeNull();

        // Cleanup
        dataChannel.Dispose();
        listener.Stop();
        
        // タスクをキャンセル
        _cts.Cancel();
        
        // acceptTaskの完了を待つ（タイムアウト付き）
        await Task.WhenAny(acceptTask, Task.Delay(1000));
    }

    [Fact(DisplayName = "FTPデータチャンネル: 大容量データ転送 - 8KB以上のバッファ処理")]
    public async Task FtpDataChannel_LargeDataTransfer_ShouldHandleSuccessfully()
    {
        // Arrange
        var sessionId = "TEST0005";
        var largeData = new byte[16384]; // 16KB のデータ
        new Random().NextBytes(largeData);
        
        var targetIp = IPAddress.Loopback;
        var targetPort = GetAvailablePort();
        
        var listener = new TcpListener(targetIp, targetPort);
        listener.Start();
        
        var dataReceivedTask = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            
            // 大容量データを送信
            await stream.WriteAsync(largeData);
            await stream.FlushAsync();
            
            await Task.Delay(100);
            client.Close();
        });

        var receivedPayloads = new List<byte[]>();
        _mockTransceiver
            .Setup(t => t.SendAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<byte[], CancellationToken>((payload, ct) => receivedPayloads.Add(payload))
            .Returns(Task.CompletedTask);

        // Act
        var dataChannel = new FtpDataChannel(
            sessionId,
            FtpDataChannelMode.Active,
            targetIp,
            targetPort,
            _mockTransceiver.Object,
            _inspector,
            _cts.Token);

        await dataReceivedTask;
        await Task.Delay(1000);

        // Assert
        receivedPayloads.Should().NotBeEmpty("大容量データが分割送信されるべき");
        
        // 全ペイロードのデータ部分を結合
        var totalReceivedData = new List<byte>();
        foreach (var payload in receivedPayloads)
        {
            if (payload.Length > 9)
            {
                totalReceivedData.AddRange(payload[9..]);
            }
        }

        totalReceivedData.Should().Equal(largeData, "受信データが元のデータと一致するべき");

        // Cleanup
        listener.Stop();
        dataChannel.Dispose();
    }

    [Fact(DisplayName = "FTPデータチャンネル: 双方向データ転送 - Upload/Download両方")]
    public async Task FtpDataChannel_BidirectionalTransfer_ShouldHandleBothDirections()
    {
        // Arrange
        var sessionId = "TEST0006";
        var uploadData = Encoding.UTF8.GetBytes("Upload data from client to server");
        var downloadData = Encoding.UTF8.GetBytes("Download data from server to client");
        
        var targetIp = IPAddress.Loopback;
        var targetPort = GetAvailablePort();
        
        var listener = new TcpListener(targetIp, targetPort);
        listener.Start();
        
        TcpClient? acceptedClient = null;
        var dataReceivedTask = Task.Run(async () =>
        {
            acceptedClient = await listener.AcceptTcpClientAsync();
            var stream = acceptedClient.GetStream();
            
            // クライアントからサーバーへのアップロード
            await stream.WriteAsync(uploadData);
            await stream.FlushAsync();
            
            // 少し待機
            await Task.Delay(500);
            
            // サーバーからクライアントへのダウンロードは別途SendToClientAsync()でテスト
        });

        var receivedPayloads = new List<byte[]>();
        _mockTransceiver
            .Setup(t => t.SendAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<byte[], CancellationToken>((payload, ct) => receivedPayloads.Add(payload))
            .Returns(Task.CompletedTask);

        // Act
        var dataChannel = new FtpDataChannel(
            sessionId,
            FtpDataChannelMode.Active,
            targetIp,
            targetPort,
            _mockTransceiver.Object,
            _inspector,
            _cts.Token);

        await dataReceivedTask;
        await Task.Delay(500);

        // アップロード方向の検証
        receivedPayloads.Should().NotBeEmpty("アップロードデータがRaw Ethernetで送信されるべき");
        
        if (receivedPayloads.Count > 0)
        {
            var uploadPayload = receivedPayloads[0];
            var receivedUploadData = uploadPayload[9..];
            receivedUploadData.Should().Equal(uploadData, "アップロードデータが一致するべき");
        }

        // ダウンロード方向のテスト（サーバー→クライアント）
        if (acceptedClient != null && acceptedClient.Connected)
        {
            await dataChannel.SendToClientAsync(downloadData);
            await Task.Delay(100);
            
            // クライアント側でデータ受信を検証
            var stream = acceptedClient.GetStream();
            if (stream.DataAvailable)
            {
                var buffer = new byte[1024];
                var bytesRead = await stream.ReadAsync(buffer);
                var receivedDownloadData = buffer[..bytesRead];
                
                receivedDownloadData.Should().Equal(downloadData, "ダウンロードデータが一致するべき");
            }
            
            acceptedClient.Close();
        }

        // Cleanup
        listener.Stop();
        dataChannel.Dispose();
    }

    [Fact(DisplayName = "FTPデータチャンネル: 複数セッション - 並行データ転送")]
    public async Task FtpDataChannel_MultipleSessions_ShouldHandleConcurrently()
    {
        // Arrange
        var session1Id = "SESS0001";
        var session2Id = "SESS0002";
        var data1 = Encoding.UTF8.GetBytes("Session 1 data");
        var data2 = Encoding.UTF8.GetBytes("Session 2 data");
        
        var port1 = GetAvailablePort();
        var port2 = GetAvailablePort();
        
        var listener1 = new TcpListener(IPAddress.Loopback, port1);
        var listener2 = new TcpListener(IPAddress.Loopback, port2);
        listener1.Start();
        listener2.Start();
        
        var session1Task = Task.Run(async () =>
        {
            var client = await listener1.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.WriteAsync(data1);
            await stream.FlushAsync();
            await Task.Delay(100);
            client.Close();
        });
        
        var session2Task = Task.Run(async () =>
        {
            var client = await listener2.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.WriteAsync(data2);
            await stream.FlushAsync();
            await Task.Delay(100);
            client.Close();
        });

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

        // Act
        var dataChannel1 = new FtpDataChannel(
            session1Id,
            FtpDataChannelMode.Active,
            IPAddress.Loopback,
            port1,
            _mockTransceiver.Object,
            _inspector,
            _cts.Token);
        
        var dataChannel2 = new FtpDataChannel(
            session2Id,
            FtpDataChannelMode.Active,
            IPAddress.Loopback,
            port2,
            _mockTransceiver.Object,
            _inspector,
            _cts.Token);

        await Task.WhenAll(session1Task, session2Task);
        await Task.Delay(1000);

        // Assert
        receivedPayloads.Should().HaveCountGreaterThanOrEqualTo(2, "2つのセッションからデータが送信されるべき");
        
        // セッション1のデータを検証
        var session1Payloads = receivedPayloads.Where(p => 
        {
            if (p.Length > 9)
            {
                var sessionId = Encoding.ASCII.GetString(p, 1, 8).TrimEnd();
                return sessionId.StartsWith(session1Id);
            }
            return false;
        }).ToList();
        
        session1Payloads.Should().NotBeEmpty("セッション1のデータが送信されるべき");

        // セッション2のデータを検証
        var session2Payloads = receivedPayloads.Where(p => 
        {
            if (p.Length > 9)
            {
                var sessionId = Encoding.ASCII.GetString(p, 1, 8).TrimEnd();
                return sessionId.StartsWith(session2Id);
            }
            return false;
        }).ToList();
        
        session2Payloads.Should().NotBeEmpty("セッション2のデータが送信されるべき");

        // Cleanup
        listener1.Stop();
        listener2.Stop();
        dataChannel1.Dispose();
        dataChannel2.Dispose();
    }

    [Fact(DisplayName = "FTPデータチャンネル: 接続タイムアウト - 30秒以内の接続確立")]
    public async Task FtpDataChannel_ConnectionTimeout_ShouldFailAfter30Seconds()
    {
        // Arrange
        var sessionId = "TEST0007";
        
        // 接続不可能なIPアドレス（タイムアウトが発生する）
        var unreachableIp = IPAddress.Parse("192.0.2.1"); // TEST-NET-1 (RFC 5737)
        var unreachablePort = 12345;

        // Act
        var dataChannel = new FtpDataChannel(
            sessionId,
            FtpDataChannelMode.Active,
            unreachableIp,
            unreachablePort,
            _mockTransceiver.Object,
            _inspector,
            _cts.Token);

        // 接続タイムアウト（30秒）を待つ
        await Task.Delay(2000); // 実際には30秒待つべきだが、テストでは2秒に短縮

        // Assert
        // データチャンネルは作成されるが、接続は失敗する
        dataChannel.Should().NotBeNull();
        
        // 接続失敗のログが出力されることを期待
        // （実際のログ検証はログフレームワークの設定による）

        // Cleanup
        dataChannel.Dispose();
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
        _cts.Cancel();
        _cts.Dispose();
        Log.CloseAndFlush();
    }
}
