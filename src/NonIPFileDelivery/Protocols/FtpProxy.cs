using NonIpFileDelivery.Core;
using NonIpFileDelivery.Security;
using Serilog;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NonIpFileDelivery.Protocols;

/// <summary>
/// FTPプロトコルのプロキシサーバー
/// Windows端末A ⇔ 非IP送受信機A ⇔ [Raw Ethernet] ⇔ 非IP送受信機B ⇔ FTPサーバー
/// </summary>
public class FtpProxy : IDisposable
{
    private readonly RawEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly TcpListener _listener;
    private readonly IPEndPoint _targetFtpServer;
    private readonly CancellationTokenSource _cts;
    private bool _isRunning;

    // プロトコル識別子
    private const byte PROTOCOL_FTP_CONTROL = 0x01;
    private const byte PROTOCOL_FTP_DATA = 0x02;

    /// <summary>
    /// FTPプロキシを初期化
    /// </summary>
    /// <param name="transceiver">Raw Ethernetトランシーバー</param>
    /// <param name="inspector">セキュリティインスペクター</param>
    /// <param name="listenPort">Windows端末Aからの接続待ち受けポート</param>
    /// <param name="targetFtpHost">Windows端末B側のFTPサーバーホスト</param>
    /// <param name="targetFtpPort">Windows端末B側のFTPサーバーポート</param>
    public FtpProxy(
        RawEthernetTransceiver transceiver,
        SecurityInspector inspector,
        int listenPort = 21,
        string targetFtpHost = "192.168.1.100",
        int targetFtpPort = 21)
    {
        _transceiver = transceiver;
        _inspector = inspector;
        _listener = new TcpListener(IPAddress.Any, listenPort);
        _targetFtpServer = new IPEndPoint(IPAddress.Parse(targetFtpHost), targetFtpPort);
        _cts = new CancellationTokenSource();

        Log.Information("FtpProxy initialized: Listen={ListenPort}, Target={TargetHost}:{TargetPort}",
            listenPort, targetFtpHost, targetFtpPort);
    }

    /// <summary>
    /// FTPプロキシを起動
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning) return;

        _listener.Start();
        _isRunning = true;

        Log.Information("FtpProxy started, listening on port {Port}", ((IPEndPoint)_listener.LocalEndpoint).Port);

        // クライアント接続受付ループ
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = HandleClientAsync(client);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error accepting FTP client");
                }
            }
        });

        // Raw Ethernet受信ループ
        _ = Task.Run(async () =>
        {
            await foreach (var packet in _transceiver.ReceiveStream(_cts.Token))
            {
                _ = HandleRawEthernetPacketAsync(packet);
            }
        });
    }

    /// <summary>
    /// Windows端末Aからの接続を処理
    /// </summary>
    private async Task HandleClientAsync(TcpClient client)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        Log.Information("FTP client connected: {SessionId}, RemoteEP={RemoteEndPoint}",
            sessionId, client.Client.RemoteEndPoint);

        try
        {
            using var clientStream = client.GetStream();
            var pipe = PipeReader.Create(clientStream);

            while (!_cts.Token.IsCancellationRequested)
            {
                var result = await pipe.ReadAsync(_cts.Token);
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                    break;

                // FTPコマンド解析（\r\n区切り）
                while (TryReadLine(ref buffer, out var line))
                {
                    var command = Encoding.ASCII.GetString(line);

                    // セキュリティ検閲
                    if (_inspector.ValidateFtpCommand(command))
                    {
                        Log.Warning("Blocked malicious FTP command: {Command}, Session={SessionId}",
                            command.Trim(), sessionId);

                        await SendFtpResponse(clientStream, "550 Command rejected by security policy.\r\n");
                        continue;
                    }

                    // Raw Ethernetで送信
                    var payload = BuildProtocolPayload(PROTOCOL_FTP_CONTROL, sessionId, line.ToArray());
                    await _transceiver.SendAsync(payload, _cts.Token);

                    Log.Debug("Forwarded FTP command via Raw Ethernet: {Command}", command.Trim());
                }

                pipe.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling FTP client: {SessionId}", sessionId);
        }
        finally
        {
            client.Close();
            Log.Information("FTP client disconnected: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Raw Ethernetパケットから受信したFTPレスポンスを処理
    /// </summary>
    private async Task HandleRawEthernetPacketAsync(PacketDotNet.EthernetPacket packet)
    {
        try
        {
            var payload = packet.PayloadData;

            if (payload.Length < 10) return; // 最小ヘッダサイズ

            var protocolType = payload[0];
            var sessionId = Encoding.ASCII.GetString(payload, 1, 8);
            var data = payload[9..];

            if (protocolType == PROTOCOL_FTP_CONTROL)
            {
                // FTPレスポンスをWindows端末Aに返送
                // （実装簡略化のため、セッション管理は省略）
                var response = Encoding.ASCII.GetString(data);
                Log.Debug("Received FTP response via Raw Ethernet: {Response}", response.Trim());

                // TODO: セッションIDからTcpClientを検索して返送
            }
            else if (protocolType == PROTOCOL_FTP_DATA)
            {
                // FTPデータ転送（PUT/GET）
                if (_inspector.ScanData(data, $"FTP-DATA-{sessionId}"))
                {
                    Log.Warning("Blocked malicious FTP data transfer: Session={SessionId}, Size={Size}",
                        sessionId, data.Length);
                    return;
                }

                // TODO: データチャンネル処理
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling Raw Ethernet packet");
        }
    }

    /// <summary>
    /// プロトコルペイロードを構築
    /// </summary>
    private byte[] BuildProtocolPayload(byte protocolType, string sessionId, byte[] data)
    {
        var payload = new byte[1 + 8 + data.Length];
        payload[0] = protocolType;
        Encoding.ASCII.GetBytes(sessionId).CopyTo(payload, 1);
        data.CopyTo(payload, 9);
        return payload;
    }

    /// <summary>
    /// パイプから1行読み取り（\r\n区切り）
    /// </summary>
    private bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        var position = buffer.PositionOf((byte)'\n');

        if (position == null)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(0, position.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
        return true;
    }

    /// <summary>
    /// FTPレスポンスを送信
    /// </summary>
    private async Task SendFtpResponse(NetworkStream stream, string response)
    {
        var data = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(data, _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _isRunning = false;

        Log.Information("FtpProxy stopped");
    }
}