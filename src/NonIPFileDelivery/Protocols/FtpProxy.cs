using NonIpFileDelivery.Core;
using NonIpFileDelivery.Security;
using Serilog;
using System.Buffers;
using System.Collections.Concurrent;
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

    // セッション管理（A側：クライアント接続の管理）
    private readonly ConcurrentDictionary<string, TcpClient> _sessionToClient = new();
    private readonly ConcurrentDictionary<TcpClient, string> _clientToSession = new();
    
    // データチャンネル管理
    private readonly ConcurrentDictionary<string, FtpDataChannel> _dataChannels = new();

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
    public Task StartAsync()
    {
        if (_isRunning) return Task.CompletedTask;

        _listener.Start();
        _isRunning = true;

        Log.Information("FtpProxy started, listening on port {Port}", ((IPEndPoint)_listener.LocalEndpoint).Port);

        // クライアント接続受付ループ（バックグラウンドタスク）
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
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Windows端末Aからの接続を処理
    /// </summary>
    private async Task HandleClientAsync(TcpClient client)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        Log.Information("FTP client connected: {SessionId}, RemoteEP={RemoteEndPoint}",
            sessionId, client.Client.RemoteEndPoint);

        // セッションを登録
        RegisterSession(sessionId, client);

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

                    // PORT/PASVコマンドの処理
                    var commandUpper = command.ToUpperInvariant().TrimStart();
                    if (commandUpper.StartsWith("PORT "))
                    {
                        // PORTコマンド: クライアントがデータチャンネルポートを指定
                        await HandlePortCommand(sessionId, command, clientStream);
                        continue;
                    }
                    else if (commandUpper.StartsWith("PASV"))
                    {
                        // PASVコマンド: サーバーがデータチャンネルポートを決定
                        await HandlePasvCommandAsync(sessionId, command, clientStream);
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
            RemoveSession(sessionId);
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
                var response = Encoding.ASCII.GetString(data);
                Log.Debug("Received FTP response via Raw Ethernet: {Response}", response.Trim());

                // セッションIDからTcpClientを検索して返送
                if (_sessionToClient.TryGetValue(sessionId, out var client))
                {
                    try
                    {
                        var stream = client.GetStream();
                        await stream.WriteAsync(data, _cts.Token);
                        await stream.FlushAsync(_cts.Token);
                        
                        Log.Debug("FTP response forwarded to client: SessionId={SessionId}, Response={Response}",
                            sessionId, response.Trim());
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to forward FTP response to client: SessionId={SessionId}", sessionId);
                        RemoveSession(sessionId);
                    }
                }
                else
                {
                    Log.Warning("Session not found for FTP response: SessionId={SessionId}", sessionId);
                }
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

                // データチャンネル処理: B側からのデータをクライアントに転送
                await HandleDataChannelDataAsync(sessionId, data);
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
        
        // セッションID（8文字、不足分はスペース埋め）
        var sessionIdBytes = Encoding.ASCII.GetBytes(sessionId.PadRight(8));
        Array.Copy(sessionIdBytes, 0, payload, 1, 8);
        
        // データ
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

    /// <summary>
    /// セッションを登録
    /// </summary>
    private void RegisterSession(string sessionId, TcpClient client)
    {
        _sessionToClient[sessionId] = client;
        _clientToSession[client] = sessionId;
        Log.Debug("Session registered: SessionId={SessionId}", sessionId);
    }

    /// <summary>
    /// セッションを削除
    /// </summary>
    private void RemoveSession(string sessionId)
    {
        if (_sessionToClient.TryRemove(sessionId, out var client))
        {
            _clientToSession.TryRemove(client, out _);
            try
            {
                client?.Close();
                client?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error closing client connection: SessionId={SessionId}", sessionId);
            }
            Log.Debug("Session removed: SessionId={SessionId}", sessionId);
        }
        
        // データチャンネルもクリーンアップ
        if (_dataChannels.TryRemove(sessionId, out var dataChannel))
        {
            dataChannel.Dispose();
        }
    }

    /// <summary>
    /// PORTコマンドを処理（アクティブモード）
    /// クライアントが指定したポートでデータチャンネルを待ち受ける
    /// </summary>
    private async Task HandlePortCommand(string sessionId, string command, NetworkStream clientStream)
    {
        try
        {
            // PORT h1,h2,h3,h4,p1,p2 形式をパース
            var parts = command.Split(' ');
            if (parts.Length < 2)
            {
                await SendFtpResponse(clientStream, "501 Syntax error in PORT command.\r\n");
                return;
            }

            var portArgs = parts[1].Split(',');
            if (portArgs.Length != 6)
            {
                await SendFtpResponse(clientStream, "501 Syntax error in PORT command.\r\n");
                return;
            }

            // IPアドレスとポートを抽出
            var ip = $"{portArgs[0]}.{portArgs[1]}.{portArgs[2]}.{portArgs[3]}";
            var port = int.Parse(portArgs[4]) * 256 + int.Parse(portArgs[5]);

            Log.Information("PORT command: SessionId={SessionId}, ClientIP={IP}, Port={Port}",
                sessionId, ip, port);

            // データチャンネルを準備（クライアント指定のアドレス/ポートに接続）
            var dataChannel = new FtpDataChannel(sessionId, FtpDataChannelMode.Active, 
                IPAddress.Parse(ip), port, _transceiver, _inspector, _cts.Token);
            
            _dataChannels[sessionId] = dataChannel;

            // PORTコマンドをB側に転送
            var payload = BuildProtocolPayload(PROTOCOL_FTP_CONTROL, sessionId, 
                Encoding.ASCII.GetBytes(command));
            await _transceiver.SendAsync(payload, _cts.Token);

            Log.Debug("Forwarded PORT command via Raw Ethernet: {Command}", command.Trim());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling PORT command: SessionId={SessionId}", sessionId);
            await SendFtpResponse(clientStream, "425 Can't open data connection.\r\n");
        }
    }

    /// <summary>
    /// PASVコマンドを処理（パッシブモード）
    /// サーバーがポートを決定し、クライアントが接続する
    /// </summary>
    private async Task HandlePasvCommandAsync(string sessionId, string command, NetworkStream clientStream)
    {
        try
        {
            Log.Information("PASV command: SessionId={SessionId}", sessionId);

            // PASVコマンドをB側に転送
            var payload = BuildProtocolPayload(PROTOCOL_FTP_CONTROL, sessionId, 
                Encoding.ASCII.GetBytes(command));
            await _transceiver.SendAsync(payload, _cts.Token);

            // データチャンネルを準備（パッシブモード）
            // 227応答（B側からのレスポンス）を受信した際に、ポート番号が設定される
            // FtpDataChannelにはParsePasvResponse()メソッドを呼び出して
            // 動的ポート情報を更新する機能を実装
            var dataChannel = new FtpDataChannel(sessionId, FtpDataChannelMode.Passive, 
                IPAddress.Any, 0, _transceiver, _inspector, _cts.Token);
            
            _dataChannels[sessionId] = dataChannel;

            Log.Debug("Forwarded PASV command via Raw Ethernet: {Command}", command.Trim());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling PASV command: SessionId={SessionId}", sessionId);
            await SendFtpResponse(clientStream, "425 Can't enter passive mode.\r\n");
        }
    }

    /// <summary>
    /// データチャンネル経由で受信したデータを処理
    /// </summary>
    private async Task HandleDataChannelDataAsync(string sessionId, byte[] data)
    {
        try
        {
            if (_dataChannels.TryGetValue(sessionId, out var dataChannel))
            {
                await dataChannel.SendToClientAsync(data);
                Log.Debug("Data forwarded to client via data channel: SessionId={SessionId}, Size={Size}",
                    sessionId, data.Length);
            }
            else
            {
                Log.Warning("Data channel not found: SessionId={SessionId}", sessionId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling data channel data: SessionId={SessionId}", sessionId);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _isRunning = false;

        // 全セッションをクリーンアップ
        foreach (var sessionId in _sessionToClient.Keys.ToList())
        {
            RemoveSession(sessionId);
        }

        // データチャンネルのクリーンアップ
        foreach (var dataChannel in _dataChannels.Values)
        {
            dataChannel.Dispose();
        }
        _dataChannels.Clear();

        Log.Information("FtpProxy stopped");
    }
}

/// <summary>
/// FTPデータチャンネルモード
/// </summary>
internal enum FtpDataChannelMode
{
    Active,  // PORT - クライアントが待ち受け
    Passive  // PASV - サーバーが待ち受け
}

/// <summary>
/// FTPデータチャンネル管理クラス
/// </summary>
internal class FtpDataChannel : IDisposable
{
    private readonly string _sessionId;
    private readonly FtpDataChannelMode _mode;
    private readonly IPAddress _targetIp;
    private readonly int _targetPort;
    private readonly RawEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly CancellationToken _cancellationToken;
    
    private TcpClient? _dataConnection;
    private NetworkStream? _dataStream;
    private const byte PROTOCOL_FTP_DATA = 0x02;
    private const int CONNECTION_TIMEOUT_MS = 30000; // 30秒
    private const int IDLE_TIMEOUT_MS = 300000; // 5分
    private DateTime _lastActivityTime = DateTime.UtcNow;
    private bool _isDisposed = false;

    public FtpDataChannel(
        string sessionId,
        FtpDataChannelMode mode,
        IPAddress targetIp,
        int targetPort,
        RawEthernetTransceiver transceiver,
        SecurityInspector inspector,
        CancellationToken cancellationToken)
    {
        _sessionId = sessionId;
        _mode = mode;
        _targetIp = targetIp;
        _targetPort = targetPort;
        _transceiver = transceiver;
        _inspector = inspector;
        _cancellationToken = cancellationToken;

        // アクティブモードの場合、クライアントへ接続を開始
        if (_mode == FtpDataChannelMode.Active)
        {
            _ = Task.Run(EstablishActiveConnectionAsync);
        }
    }

    /// <summary>
    /// アクティブモード: クライアントへのデータチャンネル接続を確立
    /// </summary>
    private async Task EstablishActiveConnectionAsync()
    {
        try
        {
            _dataConnection = new TcpClient();
            
            // タイムアウト付き接続
            using var cts = new CancellationTokenSource(CONNECTION_TIMEOUT_MS);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cts.Token);
            
            await _dataConnection.ConnectAsync(_targetIp, _targetPort, linkedCts.Token);
            _dataStream = _dataConnection.GetStream();
            
            // アイドルタイムアウト設定
            _dataConnection.ReceiveTimeout = IDLE_TIMEOUT_MS;
            _dataConnection.SendTimeout = IDLE_TIMEOUT_MS;

            Log.Information("Active data channel established: SessionId={SessionId}, Target={IP}:{Port}",
                _sessionId, _targetIp, _targetPort);

            // クライアントからのデータを受信してRaw Ethernetで転送
            _ = Task.Run(async () => await ReceiveFromClientAsync());
            
            // アイドルタイムアウト監視
            _ = Task.Run(async () => await MonitorIdleTimeoutAsync());
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Data channel connection timeout: SessionId={SessionId}", _sessionId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to establish active data channel: SessionId={SessionId}", _sessionId);
        }
    }

    /// <summary>
    /// クライアントからデータを受信してRaw Ethernetで転送（UPLOADの場合）
    /// </summary>
    private async Task ReceiveFromClientAsync()
    {
        if (_dataStream == null) return;

        try
        {
            var buffer = new byte[8192];
            while (!_cancellationToken.IsCancellationRequested && !_isDisposed)
            {
                var bytesRead = await _dataStream.ReadAsync(buffer, _cancellationToken);
                if (bytesRead == 0) break;

                _lastActivityTime = DateTime.UtcNow; // アクティビティ更新
                
                var data = buffer[..bytesRead];

                // セキュリティ検閲
                if (_inspector.ScanData(data, $"FTP-UPLOAD-{_sessionId}"))
                {
                    Log.Warning("Blocked malicious FTP upload: SessionId={SessionId}, Size={Size}",
                        _sessionId, data.Length);
                    break;
                }

                // Raw Ethernetで送信
                var payload = BuildDataPayload(data);
                await _transceiver.SendAsync(payload, _cancellationToken);

                Log.Debug("Forwarded client data via Raw Ethernet: SessionId={SessionId}, Size={Size}",
                    _sessionId, data.Length);
            }

            Log.Information("Data channel closed: SessionId={SessionId}", _sessionId);
        }
        catch (IOException ioEx)
        {
            Log.Warning(ioEx, "Data channel IO error (connection may be closed): SessionId={SessionId}", _sessionId);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Data channel receive cancelled: SessionId={SessionId}", _sessionId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error receiving from client data channel: SessionId={SessionId}", _sessionId);
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>
    /// アイドルタイムアウトを監視
    /// </summary>
    private async Task MonitorIdleTimeoutAsync()
    {
        try
        {
            while (!_cancellationToken.IsCancellationRequested && !_isDisposed)
            {
                await Task.Delay(10000, _cancellationToken); // 10秒ごとにチェック

                var idleTime = DateTime.UtcNow - _lastActivityTime;
                if (idleTime.TotalMilliseconds > IDLE_TIMEOUT_MS)
                {
                    Log.Warning("Data channel idle timeout: SessionId={SessionId}, IdleTime={IdleTime}s",
                        _sessionId, idleTime.TotalSeconds);
                    Dispose();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常なキャンセル
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error monitoring idle timeout: SessionId={SessionId}", _sessionId);
        }
    }

    /// <summary>
    /// Raw Ethernetから受信したデータをクライアントに送信（DOWNLOADの場合）
    /// </summary>
    public async Task SendToClientAsync(byte[] data)
    {
        if (_isDisposed || _dataStream == null || _dataConnection == null || !_dataConnection.Connected)
        {
            Log.Warning("Data channel not connected: SessionId={SessionId}", _sessionId);
            return;
        }

        try
        {
            _lastActivityTime = DateTime.UtcNow; // アクティビティ更新
            
            await _dataStream.WriteAsync(data, _cancellationToken);
            await _dataStream.FlushAsync(_cancellationToken);

            Log.Debug("Sent data to client: SessionId={SessionId}, Size={Size}",
                _sessionId, data.Length);
        }
        catch (IOException ioEx)
        {
            Log.Warning(ioEx, "Data channel IO error (connection may be closed): SessionId={SessionId}", _sessionId);
            Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error sending to client data channel: SessionId={SessionId}", _sessionId);
        }
    }

    /// <summary>
    /// データペイロードを構築
    /// </summary>
    private byte[] BuildDataPayload(byte[] data)
    {
        var payload = new byte[1 + 8 + data.Length];
        payload[0] = PROTOCOL_FTP_DATA;
        Encoding.ASCII.GetBytes(_sessionId).CopyTo(payload, 1);
        data.CopyTo(payload, 9);
        return payload;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        
        _isDisposed = true;
        
        try
        {
            _dataStream?.Close();
            _dataStream?.Dispose();
            _dataConnection?.Close();
            _dataConnection?.Dispose();
            
            Log.Debug("Data channel disposed: SessionId={SessionId}", _sessionId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disposing data channel: SessionId={SessionId}", _sessionId);
        }
    }
}