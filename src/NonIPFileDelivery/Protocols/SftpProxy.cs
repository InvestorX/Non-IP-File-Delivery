using NonIpFileDelivery.Core;
using NonIpFileDelivery.Security;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using Serilog;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NonIpFileDelivery.Protocols;

/// <summary>
/// SFTPプロトコルのプロキシサーバー
/// Windows端末A ⇔ 非IP送受信機A ⇔ [Raw Ethernet] ⇔ 非IP送受信機B ⇔ SFTPサーバー
/// </summary>
public class SftpProxy : IDisposable
{
    private readonly RawEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly TcpListener _listener;
    private readonly string _targetSftpHost;
    private readonly int _targetSftpPort;
    private readonly string _targetUsername;
    private readonly string _targetPassword;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentDictionary<string, SftpSession> _sessions;
    private bool _isRunning;

    // プロトコル識別子
    private const byte PROTOCOL_SFTP = 0x04;

    // SFTPコマンドタイプ
    private enum SftpCommand : byte
    {
        Init = 0x01,
        Open = 0x02,
        Read = 0x03,
        Write = 0x04,
        Close = 0x05,
        List = 0x06,
        Remove = 0x07,
        Mkdir = 0x08,
        Rmdir = 0x09,
        Stat = 0x0A
    }

    /// <summary>
    /// SFTPプロキシを初期化
    /// </summary>
    /// <param name="transceiver">Raw Ethernetトランシーバー</param>
    /// <param name="inspector">セキュリティインスペクター</param>
    /// <param name="listenPort">Windows端末Aからの接続待ち受けポート</param>
    /// <param name="targetHost">Windows端末B側のSFTPサーバーホスト</param>
    /// <param name="targetPort">Windows端末B側のSFTPサーバーポート</param>
    /// <param name="username">SFTP認証ユーザー名</param>
    /// <param name="password">SFTP認証パスワード</param>
    public SftpProxy(
        RawEthernetTransceiver transceiver,
        SecurityInspector inspector,
        int listenPort = 22,
        string targetHost = "192.168.1.100",
        int targetPort = 22,
        string username = "sftpuser",
        string password = "password")
    {
        _transceiver = transceiver;
        _inspector = inspector;
        _listener = new TcpListener(IPAddress.Any, listenPort);
        _targetSftpHost = targetHost;
        _targetSftpPort = targetPort;
        _targetUsername = username;
        _targetPassword = password;
        _cts = new CancellationTokenSource();
        _sessions = new ConcurrentDictionary<string, SftpSession>();

        Log.Information("SftpProxy initialized: Listen={ListenPort}, Target={TargetHost}:{TargetPort}",
            listenPort, targetHost, targetPort);
    }

    /// <summary>
    /// SFTPプロキシを起動
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning) return;

        _listener.Start();
        _isRunning = true;

        Log.Information("SftpProxy started, listening on port {Port}",
            ((IPEndPoint)_listener.LocalEndpoint).Port);

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
                    Log.Error(ex, "Error accepting SFTP client");
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

        Log.Information("SFTP client connected: {SessionId}, RemoteEP={RemoteEndPoint}",
            sessionId, client.Client.RemoteEndPoint);

        try
        {
            // SFTPサーバーへの接続を確立（Raw Ethernet経由でリクエスト）
            var initPayload = BuildCommandPayload(sessionId, SftpCommand.Init, Array.Empty<byte>());
            await _transceiver.SendAsync(initPayload, _cts.Token);

            // セッション情報を保存
            var session = new SftpSession
            {
                SessionId = sessionId,
                Client = client,
                ConnectedAt = DateTime.UtcNow
            };

            _sessions.TryAdd(sessionId, session);

            using var clientStream = client.GetStream();
            var pipe = PipeReader.Create(clientStream);

            while (!_cts.Token.IsCancellationRequested)
            {
                var result = await pipe.ReadAsync(_cts.Token);
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                    break;

                // SSHプロトコルハンドリング（簡易実装）
                // 実運用では完全なSSH層実装が必要
                while (TryReadSshPacket(ref buffer, out var sshData))
                {
                    // セキュリティ検閲
                    if (_inspector.ScanData(sshData, $"SFTP-{sessionId}"))
                    {
                        Log.Warning("Blocked malicious SFTP data: Session={SessionId}, Size={Size}",
                            sessionId, sshData.Length);
                        continue;
                    }

                    // Raw Ethernetで送信
                    var payload = BuildCommandPayload(sessionId, SftpCommand.Write, sshData);
                    await _transceiver.SendAsync(payload, _cts.Token);

                    Log.Debug("Forwarded SFTP data via Raw Ethernet: Session={SessionId}, Size={Size}",
                        sessionId, sshData.Length);
                }

                pipe.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling SFTP client: {SessionId}", sessionId);
        }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
            client.Close();
            Log.Information("SFTP client disconnected: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Raw Ethernetパケットから受信したSFTPレスポンスを処理
    /// </summary>
    private async Task HandleRawEthernetPacketAsync(PacketDotNet.EthernetPacket packet)
    {
        try
        {
            var payload = packet.PayloadData;

            if (payload.Length < 10) return;

            var protocolType = payload[0];
            if (protocolType != PROTOCOL_SFTP) return;

            var sessionId = Encoding.ASCII.GetString(payload, 1, 8);
            var command = (SftpCommand)payload[9];
            var data = payload[10..];

            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                Log.Warning("SFTP session not found: {SessionId}", sessionId);
                return;
            }

            // Windows端末Aに返送
            var stream = session.Client.GetStream();
            await stream.WriteAsync(data, _cts.Token);

            Log.Debug("Forwarded SFTP response to client: Session={SessionId}, Command={Command}, Size={Size}",
                sessionId, command, data.Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling SFTP Raw Ethernet packet");
        }
    }

    /// <summary>
    /// SSHパケットを読み取り（簡易実装）
    /// </summary>
    private bool TryReadSshPacket(ref ReadOnlySequence<byte> buffer, out byte[] packetData)
    {
        packetData = Array.Empty<byte>();

        // SSHパケット形式: 4バイト長 + データ
        if (buffer.Length < 4)
            return false;

        var lengthBytes = buffer.Slice(0, 4).ToArray();
        var packetLength = BitConverter.ToInt32(lengthBytes, 0);

        if (buffer.Length < 4 + packetLength)
            return false;

        packetData = buffer.Slice(4, packetLength).ToArray();
        buffer = buffer.Slice(4 + packetLength);

        return true;
    }

    /// <summary>
    /// コマンドペイロードを構築
    /// </summary>
    private byte[] BuildCommandPayload(string sessionId, SftpCommand command, byte[] data)
    {
        var payload = new byte[1 + 8 + 1 + data.Length];
        payload[0] = PROTOCOL_SFTP;
        Encoding.ASCII.GetBytes(sessionId).CopyTo(payload, 1);
        payload[9] = (byte)command;
        data.CopyTo(payload, 10);
        return payload;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();

        foreach (var session in _sessions.Values)
        {
            session.Client?.Close();
            session.SftpClient?.Dispose();
        }
        _sessions.Clear();

        _isRunning = false;
        Log.Information("SftpProxy stopped");
    }

    /// <summary>
    /// SFTPセッション情報
    /// </summary>
    private class SftpSession
    {
        public required string SessionId { get; init; }
        public required TcpClient Client { get; init; }
        public DateTime ConnectedAt { get; init; }
        public SftpClient? SftpClient { get; set; }
    }
}