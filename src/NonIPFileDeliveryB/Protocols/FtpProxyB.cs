using NonIpFileDelivery.Core;
using NonIpFileDelivery.Security;
using NonIpFileDelivery.Models;
using Serilog;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NonIpFileDeliveryB.Protocols;

/// <summary>
/// FTPプロキシB側（受信側）
/// Raw Ethernetから受信 → FTPサーバへTCP転送
/// FTPサーバからのレスポンス → Raw Ethernetで返送
/// </summary>
public class FtpProxyB : IDisposable
{
    private readonly SecureEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly string _targetFtpHost;
    private readonly int _targetFtpPort;
    private readonly SessionManagerB _sessionManager;
    private readonly CancellationTokenSource _cts;
    private bool _isRunning;

    // データチャンネル管理
    private readonly ConcurrentDictionary<string, FtpDataChannelB> _dataChannels = new();

    // プロトコル識別子（A側と統一）
    private const byte PROTOCOL_FTP_CONTROL = 0x01;
    private const byte PROTOCOL_FTP_DATA = 0x02;

    /// <summary>
    /// FtpProxyBを初期化
    /// </summary>
    /// <param name="transceiver">SecureEthernetTransceiver</param>
    /// <param name="inspector">SecurityInspector</param>
    /// <param name="targetFtpHost">FTPサーバのホスト</param>
    /// <param name="targetFtpPort">FTPサーバのポート</param>
    public FtpProxyB(
        SecureEthernetTransceiver transceiver,
        SecurityInspector inspector,
        string targetFtpHost = "192.168.2.100",
        int targetFtpPort = 21)
    {
        _transceiver = transceiver ?? throw new ArgumentNullException(nameof(transceiver));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _targetFtpHost = targetFtpHost;
        _targetFtpPort = targetFtpPort;
        _sessionManager = new SessionManagerB(sessionTimeoutMinutes: 10);
        _cts = new CancellationTokenSource();

        // Raw Ethernetパケット受信イベントを購読
        _transceiver.FrameReceived += OnFrameReceived;

        Log.Information("FtpProxyB initialized: Target={Host}:{Port}", targetFtpHost, targetFtpPort);
    }

    /// <summary>
    /// FTPプロキシB側を開始
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning) return;

        _isRunning = true;
        Log.Information("FtpProxyB started");

        await Task.CompletedTask;
    }

    /// <summary>
    /// フレーム受信時のイベントハンドラ
    /// </summary>
    private void OnFrameReceived(object? sender, SecureFrame frame)
    {
        // FTPプロトコルのみ処理
        if (frame.Protocol != SecureFrame.ProtocolType.FtpControl && 
            frame.Protocol != SecureFrame.ProtocolType.FtpData)
        {
            return;
        }

        _ = Task.Run(async () => await HandleFtpFrameAsync(frame));
    }

    /// <summary>
    /// FTPフレームを処理
    /// </summary>
    private async Task HandleFtpFrameAsync(SecureFrame frame)
    {
        try
        {
            var payload = frame.Payload;
            if (payload.Length < 10) return; // 最小ヘッダサイズ

            var protocolType = payload[0];
            var sessionIdBytes = payload[1..9];
            var sessionId = Encoding.ASCII.GetString(sessionIdBytes);
            var data = payload[9..];

            Log.Debug("Handling FTP frame: Protocol={Protocol}, SessionId={SessionId}, DataLen={DataLen}",
                protocolType, sessionId, data.Length);

            if (protocolType == PROTOCOL_FTP_CONTROL)
            {
                await HandleFtpControlAsync(sessionId, data);
            }
            else if (protocolType == PROTOCOL_FTP_DATA)
            {
                await HandleFtpDataAsync(sessionId, data);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling FTP frame");
        }
    }

    /// <summary>
    /// FTP制御コマンドを処理
    /// </summary>
    private async Task HandleFtpControlAsync(string sessionId, byte[] data)
    {
        try
        {
            var command = Encoding.ASCII.GetString(data).Trim();
            Log.Debug("FTP Control: SessionId={SessionId}, Command={Command}", sessionId, command);

            // PORT/PASVコマンドの検出と処理
            var commandUpper = command.ToUpperInvariant();
            if (commandUpper.StartsWith("PORT "))
            {
                await HandlePortCommandAsync(sessionId, command);
                return;
            }
            else if (commandUpper.StartsWith("PASV"))
            {
                // PASVコマンドの場合、データチャンネルを事前に準備
                // 実際の227応答は後で受信する
                if (!_dataChannels.ContainsKey(sessionId))
                {
                    var dataChannel = new FtpDataChannelB(sessionId, _targetFtpHost, _targetFtpPort,
                        _transceiver, _inspector, _cts.Token);
                    _dataChannels[sessionId] = dataChannel;
                    Log.Debug("Data channel prepared for PASV mode: SessionId={SessionId}", sessionId);
                }
            }

            // セッションに対応するTCP接続を取得（なければ作成）
            var client = _sessionManager.GetClientBySession(sessionId);
            if (client == null)
            {
                // 新規接続
                client = new TcpClient();
                await client.ConnectAsync(_targetFtpHost, _targetFtpPort, _cts.Token);
                _sessionManager.RegisterSession(sessionId, client);

                Log.Information("New FTP connection established: SessionId={SessionId}, Server={Host}:{Port}",
                    sessionId, _targetFtpHost, _targetFtpPort);

                // FTPサーバからのレスポンスを非同期で監視
                _ = Task.Run(async () => await MonitorFtpServerResponseAsync(sessionId, client));
            }

            // FTPサーバにコマンドを転送
            var stream = client.GetStream();
            await stream.WriteAsync(data, _cts.Token);
            await stream.FlushAsync(_cts.Token);

            Log.Debug("FTP command forwarded to server: SessionId={SessionId}, Command={Command}",
                sessionId, command);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling FTP control: SessionId={SessionId}", sessionId);
            _sessionManager.RemoveSession(sessionId);
        }
    }

    /// <summary>
    /// FTPデータ転送を処理
    /// </summary>
    private async Task HandleFtpDataAsync(string sessionId, byte[] data)
    {
        try
        {
            // セキュリティ検閲（アップロード方向）
            if (_inspector.ScanData(data, $"FTP-UPLOAD-{sessionId}"))
            {
                Log.Warning("Blocked malicious FTP data upload: SessionId={SessionId}, Size={Size}",
                    sessionId, data.Length);
                return;
            }

            // データチャンネル経由でFTPサーバに送信
            if (_dataChannels.TryGetValue(sessionId, out var dataChannel))
            {
                await dataChannel.SendToServerAsync(data);
                Log.Debug("Data forwarded to FTP server via data channel: SessionId={SessionId}, Size={Size}",
                    sessionId, data.Length);
            }
            else
            {
                Log.Warning("Data channel not found for upload: SessionId={SessionId}", sessionId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling FTP data: SessionId={SessionId}", sessionId);
        }
    }

    /// <summary>
    /// PORTコマンドを処理してデータチャンネルを確立
    /// </summary>
    private async Task HandlePortCommandAsync(string sessionId, string command)
    {
        try
        {
            // PORT h1,h2,h3,h4,p1,p2 形式をパース
            // この実装では、B側はA側が指定したPORTパラメータを変換し、
            // 実際のFTPサーバへのデータ接続を確立する
            
            Log.Information("PORT command received, establishing data channel: SessionId={SessionId}",
                sessionId);

            // 実際のFTPサーバにPORTコマンドを転送（サーバー側がアクティブモードで動作）
            var client = _sessionManager.GetClientBySession(sessionId);
            if (client != null)
            {
                var stream = client.GetStream();
                var commandBytes = Encoding.ASCII.GetBytes(command + "\r\n");
                await stream.WriteAsync(commandBytes, _cts.Token);
                await stream.FlushAsync(_cts.Token);

                // データチャンネルを準備
                var dataChannel = new FtpDataChannelB(sessionId, _targetFtpHost, _targetFtpPort,
                    _transceiver, _inspector, _cts.Token);
                _dataChannels[sessionId] = dataChannel;

                Log.Debug("Data channel prepared for PORT mode: SessionId={SessionId}", sessionId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling PORT command: SessionId={SessionId}", sessionId);
        }
    }

    /// <summary>
    /// FTPサーバからのレスポンスを監視し、Raw Ethernetで返送
    /// </summary>
    private async Task MonitorFtpServerResponseAsync(string sessionId, TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];

            while (!_cts.Token.IsCancellationRequested && client.Connected)
            {
                var bytesRead = await stream.ReadAsync(buffer, _cts.Token);
                if (bytesRead == 0) break; // 接続クローズ

                var responseData = buffer[..bytesRead];
                var response = Encoding.ASCII.GetString(responseData).Trim();

                Log.Debug("FTP response from server: SessionId={SessionId}, Response={Response}",
                    sessionId, response);

                // 227応答(Entering Passive Mode)を検出してデータチャンネルに情報を伝達
                if (response.StartsWith("227 ") && _dataChannels.TryGetValue(sessionId, out var dataChannel))
                {
                    if (dataChannel.ParsePasvResponse(response))
                    {
                        Log.Information("PASV mode activated for session: SessionId={SessionId}", sessionId);
                    }
                }

                // ダウンロード方向のセキュリティ検閲
                if (_inspector.ScanData(responseData, $"FTP-DOWNLOAD-{sessionId}"))
                {
                    Log.Warning("Blocked malicious FTP data download: SessionId={SessionId}, Size={Size}",
                        sessionId, bytesRead);
                    _sessionManager.RemoveSession(sessionId);
                    return;
                }

                // プロトコルペイロードを構築
                var payload = BuildProtocolPayload(PROTOCOL_FTP_CONTROL, sessionId, responseData);

                // Raw Ethernetで返送
                var frame = new SecureFrame
                {
                    SessionId = Guid.Parse(sessionId.PadRight(36, '0')), // セッションID変換
                    Protocol = SecureFrame.ProtocolType.FtpControl,
                    Payload = payload
                };

                await _transceiver.SendFrameAsync(frame, _cts.Token);

                Log.Debug("FTP response sent via Raw Ethernet: SessionId={SessionId}", sessionId);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常なキャンセル
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error monitoring FTP server response: SessionId={SessionId}", sessionId);
        }
        finally
        {
            _sessionManager.RemoveSession(sessionId);
            Log.Debug("FTP server monitoring ended: SessionId={SessionId}", sessionId);
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
        Array.Copy(data, 0, payload, 9, data.Length);
        
        return payload;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _isRunning = false;

        // フレーム受信イベントの購読解除
        _transceiver.FrameReceived -= OnFrameReceived;

        // データチャンネルのクリーンアップ
        foreach (var dataChannel in _dataChannels.Values)
        {
            dataChannel.Dispose();
        }
        _dataChannels.Clear();

        _sessionManager?.Dispose();

        Log.Information("FtpProxyB stopped");
    }
}

/// <summary>
/// FTPデータチャンネル管理クラス（B側）
/// Raw Ethernetからのデータ→FTPサーバへの送信
/// FTPサーバからのデータ→Raw Ethernetへの送信
/// </summary>
internal class FtpDataChannelB : IDisposable
{
    private readonly string _sessionId;
    private readonly string _targetFtpHost;
    private readonly int _targetFtpPort;
    private readonly SecureEthernetTransceiver _transceiver;
    private readonly SecurityInspector _inspector;
    private readonly CancellationToken _cancellationToken;

    private TcpClient? _dataConnection;
    private NetworkStream? _dataStream;
    private const byte PROTOCOL_FTP_DATA = 0x02;
    
    // PASV応答から取得した動的ポート情報
    private string? _pasvHost;
    private int? _pasvPort;

    public FtpDataChannelB(
        string sessionId,
        string targetFtpHost,
        int targetFtpPort,
        SecureEthernetTransceiver transceiver,
        SecurityInspector inspector,
        CancellationToken cancellationToken)
    {
        _sessionId = sessionId;
        _targetFtpHost = targetFtpHost;
        _targetFtpPort = targetFtpPort;
        _transceiver = transceiver;
        _inspector = inspector;
        _cancellationToken = cancellationToken;

        Log.Debug("FtpDataChannelB created: SessionId={SessionId}", sessionId);
    }
    
    /// <summary>
    /// 227応答(Entering Passive Mode)をパースしてホストとポートを取得
    /// 形式: 227 Entering Passive Mode (h1,h2,h3,h4,p1,p2)
    /// </summary>
    public bool ParsePasvResponse(string response)
    {
        try
        {
            // 227応答の括弧内の数値を抽出
            var startIndex = response.IndexOf('(');
            var endIndex = response.IndexOf(')');
            if (startIndex == -1 || endIndex == -1 || endIndex <= startIndex)
            {
                Log.Warning("Invalid PASV response format: {Response}", response);
                return false;
            }

            var numbers = response.Substring(startIndex + 1, endIndex - startIndex - 1)
                .Split(',')
                .Select(s => s.Trim())
                .ToArray();

            if (numbers.Length != 6)
            {
                Log.Warning("PASV response does not contain 6 numbers: {Response}", response);
                return false;
            }

            // h1.h2.h3.h4形式のホスト名を構築
            _pasvHost = $"{numbers[0]}.{numbers[1]}.{numbers[2]}.{numbers[3]}";
            
            // p1 * 256 + p2でポート番号を計算
            if (int.TryParse(numbers[4], out var p1) && int.TryParse(numbers[5], out var p2))
            {
                _pasvPort = (p1 * 256) + p2;
                Log.Information("Parsed PASV response: Host={Host}, Port={Port}", _pasvHost, _pasvPort);
                return true;
            }

            Log.Warning("Failed to parse port numbers from PASV response: {Response}", response);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error parsing PASV response: {Response}", response);
            return false;
        }
    }

    /// <summary>
    /// Raw Ethernetから受信したデータをFTPサーバに送信（UPLOADの場合）
    /// </summary>
    public async Task SendToServerAsync(byte[] data)
    {
        try
        {
            // データチャンネル接続がまだない場合は確立
            if (_dataConnection == null || !_dataConnection.Connected)
            {
                await EstablishDataConnectionAsync();
            }

            if (_dataStream != null && _dataConnection != null && _dataConnection.Connected)
            {
                await _dataStream.WriteAsync(data, _cancellationToken);
                await _dataStream.FlushAsync(_cancellationToken);

                Log.Debug("Data sent to FTP server: SessionId={SessionId}, Size={Size}",
                    _sessionId, data.Length);
            }
            else
            {
                Log.Warning("Data channel not connected: SessionId={SessionId}", _sessionId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error sending to FTP server data channel: SessionId={SessionId}", _sessionId);
        }
    }

    /// <summary>
    /// FTPサーバへのデータチャンネル接続を確立
    /// PASV応答から取得したホストとポート情報を使用
    /// </summary>
    private async Task EstablishDataConnectionAsync()
    {
        try
        {
            // PASV応答から取得した情報を使用
            string targetHost;
            int targetPort;

            if (_pasvHost != null && _pasvPort.HasValue)
            {
                // PASV応答で指定されたホストとポートを使用
                targetHost = _pasvHost;
                targetPort = _pasvPort.Value;
                Log.Debug("Using PASV-provided host and port: Host={Host}, Port={Port}", targetHost, targetPort);
            }
            else
            {
                // PASV応答がない場合はフォールバック（アクティブモード）
                // FTPサーバのデータポート（通常20番）に接続を試みる
                targetHost = _targetFtpHost;
                targetPort = 20; // FTPデータポート（アクティブモード）
                Log.Warning("PASV response not available, falling back to active mode: Host={Host}, Port={Port}",
                    targetHost, targetPort);
            }
            
            _dataConnection = new TcpClient();
            await _dataConnection.ConnectAsync(targetHost, targetPort, _cancellationToken);
            _dataStream = _dataConnection.GetStream();

            Log.Information("Data channel established to FTP server: SessionId={SessionId}, Host={Host}, Port={Port}",
                _sessionId, targetHost, targetPort);

            // FTPサーバからのデータを受信してRaw Ethernetで転送（DOWNLOADの場合）
            _ = Task.Run(async () => await ReceiveFromServerAsync());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to establish data channel to FTP server: SessionId={SessionId}", _sessionId);
        }
    }

    /// <summary>
    /// FTPサーバからデータを受信してRaw Ethernetで転送（DOWNLOADの場合）
    /// </summary>
    private async Task ReceiveFromServerAsync()
    {
        if (_dataStream == null) return;

        try
        {
            var buffer = new byte[8192];
            while (!_cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await _dataStream.ReadAsync(buffer, _cancellationToken);
                if (bytesRead == 0) break;

                var data = buffer[..bytesRead];

                // セキュリティ検閲（ダウンロード方向）
                if (_inspector.ScanData(data, $"FTP-DOWNLOAD-{_sessionId}"))
                {
                    Log.Warning("Blocked malicious FTP download: SessionId={SessionId}, Size={Size}",
                        _sessionId, data.Length);
                    break;
                }

                // Raw Ethernetで送信
                var payload = BuildDataPayload(data);
                await _transceiver.SendAsync(payload, SecureFrame.ProtocolType.FtpData, null, _cancellationToken);

                Log.Debug("Data forwarded to client via Raw Ethernet: SessionId={SessionId}, Size={Size}",
                    _sessionId, data.Length);
            }

            Log.Information("Data channel receive completed: SessionId={SessionId}", _sessionId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error receiving from FTP server data channel: SessionId={SessionId}", _sessionId);
        }
    }

    /// <summary>
    /// データペイロードを構築
    /// </summary>
    private byte[] BuildDataPayload(byte[] data)
    {
        var payload = new byte[1 + 8 + data.Length];
        payload[0] = PROTOCOL_FTP_DATA;
        Encoding.ASCII.GetBytes(_sessionId.PadRight(8)).CopyTo(payload, 1);
        data.CopyTo(payload, 9);
        return payload;
    }

    public void Dispose()
    {
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
