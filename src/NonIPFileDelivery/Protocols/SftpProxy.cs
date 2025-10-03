using NonIpFileDelivery.Core;
using NonIpFileDelivery.Security;
using Serilog;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NonIpFileDelivery.Protocols;

/// <summary>
/// SFTP (SSH File Transfer Protocol) プロキシサーバー
/// Windows端末A ⇔ 非IP送受信機A ⇔ [Raw Ethernet] ⇔ 非IP送受信機B ⇔ SFTPサーバー
/// </summary>
public class SftpProxy : IDisposable
{
    private readonly RawEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly TcpListener _listener;
    private readonly IPEndPoint _targetSftpServer;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, SftpSession> _sessions;
    private readonly object _sessionLock = new();
    private bool _isRunning;

    // プロトコル識別子
    private const byte PROTOCOL_SFTP_SSH_HANDSHAKE = 0x20;
    private const byte PROTOCOL_SFTP_CHANNEL = 0x21;
    private const byte PROTOCOL_SFTP_DATA = 0x22;

    // SSH/SFTPパケットタイプ
    private const byte SSH_MSG_KEXINIT = 20;
    private const byte SSH_MSG_NEWKEYS = 21;
    private const byte SSH_MSG_CHANNEL_OPEN = 90;
    private const byte SSH_MSG_CHANNEL_DATA = 94;

    // SFTPパケットタイプ
    private const byte SSH_FXP_INIT = 1;
    private const byte SSH_FXP_OPEN = 3;
    private const byte SSH_FXP_CLOSE = 4;
    private const byte SSH_FXP_READ = 5;
    private const byte SSH_FXP_WRITE = 6;
    private const byte SSH_FXP_REMOVE = 13;
    private const byte SSH_FXP_MKDIR = 14;
    private const byte SSH_FXP_RMDIR = 15;
    private const byte SSH_FXP_RENAME = 18;

    /// <summary>
    /// SFTPプロキシを初期化
    /// </summary>
    /// <param name="transceiver">Raw Ethernetトランシーバー</param>
    /// <param name="inspector">セキュリティインスペクター</param>
    /// <param name="listenPort">Windows端末Aからの接続待ち受けポート</param>
    /// <param name="targetSftpHost">Windows端末B側のSFTPサーバーホスト</param>
    /// <param name="targetSftpPort">Windows端末B側のSFTPサーバーポート</param>
    public SftpProxy(
        RawEthernetTransceiver transceiver,
        SecurityInspector inspector,
        int listenPort = 22,
        string targetSftpHost = "192.168.1.100",
        int targetSftpPort = 22)
    {
        _transceiver = transceiver;
        _inspector = inspector;
        _listener = new TcpListener(IPAddress.Any, listenPort);
        _targetSftpServer = new IPEndPoint(IPAddress.Parse(targetSftpHost), targetSftpPort);
        _cts = new CancellationTokenSource();
        _sessions = new Dictionary<string, SftpSession>();

        Log.Information("SftpProxy initialized: Listen={ListenPort}, Target={TargetHost}:{TargetPort}",
            listenPort, targetSftpHost, targetSftpPort);
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

        var session = new SftpSession(sessionId, client);

        lock (_sessionLock)
        {
            _sessions[sessionId] = session;
        }

        try
        {
            using var clientStream = client.GetStream();
            var pipe = PipeReader.Create(clientStream);

            // SSHバージョン交換
            await SendSshVersionAsync(clientStream);
            var clientVersion = await ReceiveSshVersionAsync(pipe);
            
            Log.Information("SSH version exchange: {SessionId}, ClientVersion={Version}",
                sessionId, clientVersion);

            while (!_cts.Token.IsCancellationRequested)
            {
                var result = await pipe.ReadAsync(_cts.Token);
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                    break;

                // SSHパケット解析
                while (TryReadSshPacket(ref buffer, out var packetType, out var packetData))
                {
                    await ProcessSshPacketAsync(sessionId, packetType, packetData, clientStream);
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
            lock (_sessionLock)
            {
                _sessions.Remove(sessionId);
            }

            client.Close();
            Log.Information("SFTP client disconnected: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// SSHバージョン文字列を送信
    /// </summary>
    private async Task SendSshVersionAsync(NetworkStream stream)
    {
        var version = "SSH-2.0-NonIPFileDelivery_1.0\r\n";
        var versionBytes = Encoding.ASCII.GetBytes(version);
        await stream.WriteAsync(versionBytes, _cts.Token);
        await stream.FlushAsync(_cts.Token);
    }

    /// <summary>
    /// SSHバージョン文字列を受信
    /// </summary>
    private async Task<string> ReceiveSshVersionAsync(PipeReader pipe)
    {
        while (true)
        {
            var result = await pipe.ReadAsync(_cts.Token);
            var buffer = result.Buffer;

            var position = buffer.PositionOf((byte)'\n');
            if (position != null)
            {
                var line = buffer.Slice(0, position.Value);
                var version = Encoding.ASCII.GetString(line);
                
                pipe.AdvanceTo(buffer.GetPosition(1, position.Value));
                
                return version.TrimEnd('\r', '\n');
            }

            pipe.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
                break;
        }

        return string.Empty;
    }

    /// <summary>
    /// SSHパケットを処理
    /// </summary>
    private async Task ProcessSshPacketAsync(
        string sessionId,
        byte packetType,
        ReadOnlySequence<byte> packetData,
        NetworkStream clientStream)
    {
        try
        {
            byte protocolType = packetType switch
            {
                SSH_MSG_KEXINIT => PROTOCOL_SFTP_SSH_HANDSHAKE,
                SSH_MSG_NEWKEYS => PROTOCOL_SFTP_SSH_HANDSHAKE,
                SSH_MSG_CHANNEL_OPEN => PROTOCOL_SFTP_CHANNEL,
                SSH_MSG_CHANNEL_DATA => PROTOCOL_SFTP_DATA,
                _ => PROTOCOL_SFTP_DATA
            };

            // SFTPパケットの場合はセキュリティ検閲
            if (packetType == SSH_MSG_CHANNEL_DATA)
            {
                var sftpPacket = ExtractSftpPacket(packetData);
                
                if (sftpPacket.HasValue)
                {
                    var (sftpType, sftpData) = sftpPacket.Value;

                    // ファイル操作のロギング
                    LogSftpOperation(sessionId, sftpType, sftpData);

                    // ファイル書き込み時のマルウェアスキャン
                    if (sftpType == SSH_FXP_WRITE)
                    {
                        var fileData = ExtractFileData(sftpData);
                        
                        if (fileData.Length > 0 && _inspector.ScanData(fileData, $"SFTP-WRITE-{sessionId}"))
                        {
                            Log.Warning("Blocked malicious SFTP file write: {SessionId}, Size={Size}",
                                sessionId, fileData.Length);

                            await SendSftpError(clientStream, sessionId, 4); // SSH_FX_FAILURE
                            return;
                        }
                    }

                    // 危険なファイル操作の検出
                    if (DetectDangerousSftpOperation(sftpType, sftpData))
                    {
                        Log.Warning("Blocked dangerous SFTP operation: {SessionId}, Type={Type}",
                            sessionId, sftpType);

                        await SendSftpError(clientStream, sessionId, 3); // SSH_FX_PERMISSION_DENIED
                        return;
                    }
                }
            }

            // Raw Ethernetで送信（既存の暗号化レイヤーを使用）
            var payload = BuildProtocolPayload(protocolType, sessionId, packetType, packetData);
            await _transceiver.SendAsync(payload, _cts.Token);

            Log.Debug("Forwarded SFTP packet via Raw Ethernet: SessionId={SessionId}, Type=0x{Type:X2}",
                sessionId, packetType);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing SFTP packet: {SessionId}", sessionId);
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
            var sessionId = Encoding.ASCII.GetString(payload, 1, 8);
            var packetType = payload[9];
            var data = payload[10..];

            // SFTPプロトコルの処理
            if (protocolType is PROTOCOL_SFTP_SSH_HANDSHAKE or PROTOCOL_SFTP_CHANNEL 
                or PROTOCOL_SFTP_DATA)
            {
                lock (_sessionLock)
                {
                    if (_sessions.TryGetValue(sessionId, out var session))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var stream = session.Client.GetStream();
                                
                                // SSHパケット形式で返送
                                var sshPacket = BuildSshPacket(packetType, data);
                                await stream.WriteAsync(sshPacket, _cts.Token);
                                await stream.FlushAsync(_cts.Token);

                                Log.Debug("Sent SFTP response to client: SessionId={SessionId}, Type=0x{Type:X2}",
                                    sessionId, packetType);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Error sending SFTP response: {SessionId}", sessionId);
                            }
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling Raw Ethernet packet");
        }
    }

    /// <summary>
    /// SSHパケットを読み取る
    /// </summary>
    private bool TryReadSshPacket(
        ref ReadOnlySequence<byte> buffer,
        out byte packetType,
        out ReadOnlySequence<byte> packetData)
    {
        packetType = 0;
        packetData = default;

        // SSHパケット: packet_length(4) + padding_length(1) + payload + padding + mac
        if (buffer.Length >= 5)
        {
            var lengthBytes = buffer.Slice(0, 4).ToArray();
            var packetLength = BitConverter.ToInt32(lengthBytes, 0);

            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(lengthBytes);
                packetLength = BitConverter.ToInt32(lengthBytes, 0);
            }

            if (packetLength > 0 && packetLength + 4 <= buffer.Length)
            {
                var paddingLength = buffer.Slice(4, 1).First.Span[0];
                var payloadLength = packetLength - paddingLength - 1;

                if (payloadLength > 0)
                {
                    packetType = buffer.Slice(5, 1).First.Span[0];
                    packetData = buffer.Slice(6, payloadLength - 1);
                    buffer = buffer.Slice(4 + packetLength);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// SFTPパケットを抽出
    /// </summary>
    private (byte Type, byte[] Data)? ExtractSftpPacket(ReadOnlySequence<byte> channelData)
    {
        try
        {
            var data = channelData.ToArray();

            // SSH_MSG_CHANNEL_DATAの構造: channel(4) + data_length(4) + data
            if (data.Length < 9) return null;

            var dataLength = BitConverter.ToInt32(data, 4);
            if (BitConverter.IsLittleEndian)
            {
                var lengthBytes = data.AsSpan(4, 4).ToArray();
                Array.Reverse(lengthBytes);
                dataLength = BitConverter.ToInt32(lengthBytes, 0);
            }

            if (data.Length < 8 + dataLength) return null;

            // SFTPパケット: length(4) + type(1) + data
            var sftpLength = BitConverter.ToInt32(data, 8);
            if (BitConverter.IsLittleEndian)
            {
                var sftpLengthBytes = data.AsSpan(8, 4).ToArray();
                Array.Reverse(sftpLengthBytes);
                sftpLength = BitConverter.ToInt32(sftpLengthBytes, 0);
            }

            var sftpType = data[12];
            var sftpData = new byte[sftpLength - 1];
            Array.Copy(data, 13, sftpData, 0, sftpLength - 1);

            return (sftpType, sftpData);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// SFTP操作をロギング
    /// </summary>
    private void LogSftpOperation(string sessionId, byte sftpType, byte[] sftpData)
    {
        var operation = sftpType switch
        {
            SSH_FXP_INIT => "INIT",
            SSH_FXP_OPEN => "OPEN",
            SSH_FXP_CLOSE => "CLOSE",
            SSH_FXP_READ => "READ",
            SSH_FXP_WRITE => "WRITE",
            SSH_FXP_REMOVE => "REMOVE",
            SSH_FXP_MKDIR => "MKDIR",
            SSH_FXP_RMDIR => "RMDIR",
            SSH_FXP_RENAME => "RENAME",
            _ => $"UNKNOWN(0x{sftpType:X2})"
        };

        Log.Information("SFTP Operation: {SessionId}, Type={Operation}", sessionId, operation);
    }

    /// <summary>
    /// 危険なSFTP操作の検出
    /// </summary>
    private bool DetectDangerousSftpOperation(byte sftpType, byte[] sftpData)
    {
        // システムファイルの削除防止
        if (sftpType == SSH_FXP_REMOVE || sftpType == SSH_FXP_RMDIR)
        {
            var filename = ExtractFilename(sftpData);
            
            if (filename.Contains("/etc/") || filename.Contains("/sys/") ||
                filename.Contains("C:\\Windows\\") || filename.Contains("C:\\System"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// ファイル名を抽出
    /// </summary>
    private string ExtractFilename(byte[] sftpData)
    {
        try
        {
            if (sftpData.Length < 5) return string.Empty;

            var filenameLength = BitConverter.ToInt32(sftpData, 0);
            if (BitConverter.IsLittleEndian)
            {
                var lengthBytes = sftpData.AsSpan(0, 4).ToArray();
                Array.Reverse(lengthBytes);
                filenameLength = BitConverter.ToInt32(lengthBytes, 0);
            }

            if (sftpData.Length < 4 + filenameLength) return string.Empty;

            return Encoding.UTF8.GetString(sftpData, 4, filenameLength);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// ファイルデータを抽出
    /// </summary>
    private byte[] ExtractFileData(byte[] sftpData)
    {
        try
        {
            // SSH_FXP_WRITEの構造: handle_length(4) + handle + offset(8) + data_length(4) + data
            if (sftpData.Length < 16) return Array.Empty<byte>();

            var handleLength = BitConverter.ToInt32(sftpData, 0);
            if (BitConverter.IsLittleEndian)
            {
                var lengthBytes = sftpData.AsSpan(0, 4).ToArray();
                Array.Reverse(lengthBytes);
                handleLength = BitConverter.ToInt32(lengthBytes, 0);
            }

            var dataLengthOffset = 4 + handleLength + 8;
            if (sftpData.Length < dataLengthOffset + 4) return Array.Empty<byte>();

            var dataLength = BitConverter.ToInt32(sftpData, dataLengthOffset);
            if (BitConverter.IsLittleEndian)
            {
                var lengthBytes = sftpData.AsSpan(dataLengthOffset, 4).ToArray();
                Array.Reverse(lengthBytes);
                dataLength = BitConverter.ToInt32(lengthBytes, 0);
            }

            var dataOffset = dataLengthOffset + 4;
            if (sftpData.Length < dataOffset + dataLength) return Array.Empty<byte>();

            var fileData = new byte[dataLength];
            Array.Copy(sftpData, dataOffset, fileData, 0, dataLength);

            return fileData;
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// SFTPエラーレスポンスを送信
    /// </summary>
    private async Task SendSftpError(NetworkStream stream, string sessionId, uint errorCode)
    {
        // SSH_FXP_STATUS: type(1) + id(4) + code(4) + message_length(4) + message + lang_length(4) + lang
        var errorMessage = "Operation rejected by security policy";
        var messageBytes = Encoding.UTF8.GetBytes(errorMessage);

        var sftpStatus = new byte[1 + 4 + 4 + 4 + messageBytes.Length + 4];
        sftpStatus[0] = 101; // SSH_FXP_STATUS
        
        // Request ID (ダミー値)
        BitConverter.GetBytes(1).CopyTo(sftpStatus, 1);
        
        // Error code
        var errorCodeBytes = BitConverter.GetBytes(errorCode);
        if (BitConverter.IsLittleEndian) Array.Reverse(errorCodeBytes);
        errorCodeBytes.CopyTo(sftpStatus, 5);
        
        // Message length
        var messageLengthBytes = BitConverter.GetBytes(messageBytes.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(messageLengthBytes);
        messageLengthBytes.CopyTo(sftpStatus, 9);
        
        // Message
        messageBytes.CopyTo(sftpStatus, 13);
        
        // Language tag length (0)
        BitConverter.GetBytes(0).CopyTo(sftpStatus, 13 + messageBytes.Length);

        var sshPacket = BuildSshPacket(SSH_MSG_CHANNEL_DATA, sftpStatus);
        await stream.WriteAsync(sshPacket, _cts.Token);
        await stream.FlushAsync(_cts.Token);
    }

    /// <summary>
    /// SSHパケットを構築
    /// </summary>
    private byte[] BuildSshPacket(byte packetType, byte[] payload)
    {
        var paddingLength = (byte)(8 - ((5 + payload.Length) % 8));
        if (paddingLength < 4) paddingLength += 8;

        var packetLength = 1 + payload.Length + paddingLength;
        var packet = new byte[4 + 1 + 1 + payload.Length + paddingLength];

        var lengthBytes = BitConverter.GetBytes(packetLength);
        if (BitConverter.IsLittleEndian) Array.Reverse(lengthBytes);
        lengthBytes.CopyTo(packet, 0);

        packet[4] = paddingLength;
        packet[5] = packetType;
        payload.CopyTo(packet, 6);

        // Padding (ランダムバイト、ここではゼロで簡略化)
        // 実運用では SecureRandom でランダムバイトを生成すべき

        return packet;
    }

    /// <summary>
    /// プロトコルペイロードを構築
    /// </summary>
    private byte[] BuildProtocolPayload(
        byte protocolType,
        string sessionId,
        byte packetType,
        ReadOnlySequence<byte> packetData)
    {
        var data = packetData.ToArray();
        var payload = new byte[1 + 8 + 1 + data.Length];
        
        payload[0] = protocolType;
        Encoding.ASCII.GetBytes(sessionId).CopyTo(payload, 1);
        payload[9] = packetType;
        data.CopyTo(payload, 10);
        
        return payload;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();

        lock (_sessionLock)
        {
            foreach (var session in _sessions.Values)
            {
                session.Client.Close();
            }
            _sessions.Clear();
        }

        _isRunning = false;
        Log.Information("SftpProxy stopped");
    }

    /// <summary>
    /// SFTPセッション情報
    /// </summary>
    private class SftpSession
    {
        public string SessionId { get; }
        public TcpClient Client { get; }
        public DateTime ConnectedAt { get; }

        public SftpSession(string sessionId, TcpClient client)
        {
            SessionId = sessionId;
            Client = client;
            ConnectedAt = DateTime.UtcNow;
        }
    }
}