using NonIpFileDelivery.Security;
using NonIPFileDelivery.Resilience;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Threading.Channels;
using Serilog;

namespace NonIpFileDelivery.Core;

/// <summary>
/// 暗号化対応のRaw Ethernetトランシーバー
/// SecureFrameプロトコルを使用した安全な通信
/// </summary>
public class SecureEthernetTransceiver : IDisposable
{
    private readonly LibPcapLiveDevice _device;
    private readonly PhysicalAddress _localMacAddress;
    private readonly PhysicalAddress _remoteMacAddress;
    private readonly CryptoEngine _cryptoEngine;
    private readonly Channel<SecureFrame> _receiveChannel;
    private readonly ConcurrentDictionary<Guid, long> _sessionSequences;
    private readonly ushort _customEtherType;
    private readonly bool _receiverMode;
    private bool _isRunning;
    private long _sendSequence;
    
    // 再送制御とQoS機能
    private readonly RetryPolicy _retryPolicy;
    private readonly QoSFrameQueue _qosQueue;
    private readonly CancellationTokenSource _backgroundTasksCts = new();
    private Task? _qosProcessingTask;

    private const ushort CUSTOM_PROTOCOL_ETHERTYPE = 0x88B5;
    private const int MAX_SEQUENCE_GAP = 100; // リプレイ攻撃検知用

    /// <summary>
    /// フレーム受信時のイベント（B側でプロトコルプロキシが購読する）
    /// </summary>
    public event EventHandler<SecureFrame>? FrameReceived;

    /// <summary>
    /// セキュアなRaw Ethernetトランシーバーを初期化
    /// </summary>
    /// <param name="interfaceName">ネットワークインターフェース名</param>
    /// <param name="remoteMac">対向機器のMACアドレス</param>
    /// <param name="cryptoEngine">暗号化エンジン</param>
    /// <param name="receiverMode">受信モード（B側）の場合true</param>
    /// <param name="channelCapacity">受信チャンネル容量</param>
    public SecureEthernetTransceiver(
        string interfaceName,
        string remoteMac,
        CryptoEngine cryptoEngine,
        bool receiverMode = false,
        int channelCapacity = 10000)
    {
        _device = LibPcapLiveDeviceList.Instance
            .FirstOrDefault(d => d.Name == interfaceName)
            ?? throw new ArgumentException($"Interface {interfaceName} not found");

        _localMacAddress = _device.MacAddress;
        _remoteMacAddress = PhysicalAddress.Parse(remoteMac.Replace(":", "-"));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
        _customEtherType = CUSTOM_PROTOCOL_ETHERTYPE;
        _receiverMode = receiverMode;
        _sendSequence = 0;

        _receiveChannel = Channel.CreateBounded<SecureFrame>(
            new BoundedChannelOptions(channelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

        _sessionSequences = new ConcurrentDictionary<Guid, long>();

        // RetryPolicyとQoSキューを初期化
        _retryPolicy = new RetryPolicy(
            new LoggingService(),
            maxRetryAttempts: 3,
            initialDelayMs: 100,
            maxDelayMs: 5000);
        
        _qosQueue = new QoSFrameQueue();

        Log.Information("SecureEthernetTransceiver initialized: {Interface}, Local={LocalMac}, Remote={RemoteMac}, ReceiverMode={ReceiverMode}",
            interfaceName, _localMacAddress, _remoteMacAddress, receiverMode);
    }

    /// <summary>
    /// 送受信を開始
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;

        _device.Open(DeviceModes.Promiscuous, 1000);
        _device.Filter = $"ether proto 0x{_customEtherType:X4}";
        _device.OnPacketArrival += OnPacketArrival;
        _device.StartCapture();

        // QoS処理タスクを開始
        _qosProcessingTask = Task.Run(async () => await ProcessQueueAsync(_backgroundTasksCts.Token));

        _isRunning = true;
        Log.Information("Secure packet capture started on {Interface} with QoS and Retry enabled", _device.Name);
    }

    /// <summary>
    /// パケット受信イベントハンドラ（復号化処理）
    /// </summary>
    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var rawPacket = e.GetPacket();
            var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);

            if (packet is not EthernetPacket ethPacket ||
                !ethPacket.DestinationHardwareAddress.Equals(_localMacAddress))
            {
                return;
            }

            // フレームを復号化
            var frame = SecureFrame.Deserialize(ethPacket.PayloadData, _cryptoEngine);

            // リプレイ攻撃検知
            if (!ValidateSequence(frame.SessionId, frame.SequenceNumber))
            {
                Log.Warning("Potential replay attack detected: Session={SessionId}, Seq={Sequence}",
                    frame.SessionId, frame.SequenceNumber);
                return;
            }

            // タイムスタンプ検証（5秒以上古いフレームは拒否）
            var age = DateTimeOffset.UtcNow - frame.Timestamp;
            if (age.TotalSeconds > 5)
            {
                Log.Warning("Frame timestamp too old: {Age} seconds, Session={SessionId}",
                    age.TotalSeconds, frame.SessionId);
                return;
            }

            // 受信チャンネルに投入
            _receiveChannel.Writer.TryWrite(frame);

            // B側の場合、イベントも発火（プロトコルプロキシが購読）
            if (_receiverMode)
            {
                FrameReceived?.Invoke(this, frame);
            }

            Log.Debug("Received secure frame: {Frame}", frame);
        }
        catch (CryptographicException ex)
        {
            Log.Error(ex, "Frame decryption failed - possible tampering or wrong key");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing received packet");
        }
    }

    /// <summary>
    /// セキュアフレームを送信（QoSキューに追加）
    /// </summary>
    /// <param name="frame">送信するフレーム</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    public async Task SendFrameAsync(SecureFrame frame, CancellationToken cancellationToken = default)
    {
        try
        {
            // シーケンス番号を割り当て
            frame.SequenceNumber = Interlocked.Increment(ref _sendSequence);
            frame.Timestamp = DateTimeOffset.UtcNow;

            // QoSキューに追加（バックグラウンドタスクが優先度順に処理）
            _qosQueue.Enqueue(frame);

            Log.Debug("Frame enqueued for transmission: Session={SessionId}, Seq={Seq}, Protocol={Protocol}",
                frame.SessionId, frame.SequenceNumber, frame.Protocol);

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enqueue frame");
            throw;
        }
    }

    /// <summary>
    /// フレームを実際に送信（内部用）
    /// </summary>
    private async Task SendFrameInternalAsync(SecureFrame frame, CancellationToken cancellationToken = default)
    {
        // フレームをシリアライズ（暗号化）
        var frameData = frame.Serialize(_cryptoEngine);

        // Ethernetフレームに格納
        var ethPacket = new EthernetPacket(
            _localMacAddress,
            _remoteMacAddress,
            (EthernetType)_customEtherType)
        {
            PayloadData = frameData
        };

        // 送信
        await Task.Run(() => _device.SendPacket(ethPacket), cancellationToken);

        Log.Debug("Sent secure frame: Session={SessionId}, Seq={Seq}, Protocol={Protocol}",
            frame.SessionId, frame.SequenceNumber, frame.Protocol);
    }

    /// <summary>
    /// データを暗号化して送信（簡易版）
    /// </summary>
    /// <param name="payload">送信データ</param>
    /// <param name="protocol">プロトコル種別</param>
    /// <param name="sessionId">セッションID（nullの場合は新規作成）</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    public async Task SendAsync(
        byte[] payload,
        SecureFrame.ProtocolType protocol,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var frame = new SecureFrame
        {
            SessionId = sessionId ?? Guid.NewGuid(),
            Protocol = protocol,
            Payload = payload
        };

        await SendFrameAsync(frame, cancellationToken);
    }

    /// <summary>
    /// 受信フレームを非同期で取得
    /// </summary>
    public async Task<SecureFrame> ReceiveFrameAsync(CancellationToken cancellationToken = default)
    {
        return await _receiveChannel.Reader.ReadAsync(cancellationToken);
    }

    /// <summary>
    /// 受信フレームのストリームを取得
    /// </summary>
    public IAsyncEnumerable<SecureFrame> ReceiveStream(CancellationToken cancellationToken)
    {
        return _receiveChannel.Reader.ReadAllAsync(cancellationToken);
    }

    /// <summary>
    /// 受信開始（B側モード用）
    /// </summary>
    public async Task StartReceivingAsync()
    {
        if (!_receiverMode)
        {
            Log.Warning("StartReceivingAsync called but not in receiver mode");
        }

        Start();

        Log.Information("Started receiving in receiver mode");
        await Task.CompletedTask;
    }

    /// <summary>
    /// シーケンス番号を検証（リプレイ攻撃対策）
    /// </summary>
    private bool ValidateSequence(Guid sessionId, long sequenceNumber)
    {
        var lastSequence = _sessionSequences.GetOrAdd(sessionId, -1);

        // 初回受信
        if (lastSequence == -1)
        {
            _sessionSequences[sessionId] = sequenceNumber;
            return true;
        }

        // シーケンス番号が進んでいるか確認
        if (sequenceNumber <= lastSequence)
        {
            return false; // リプレイの可能性
        }

        // ギャップが大きすぎる場合も疑わしい
        if (sequenceNumber - lastSequence > MAX_SEQUENCE_GAP)
        {
            Log.Warning("Large sequence gap detected: Session={SessionId}, Gap={Gap}",
                sessionId, sequenceNumber - lastSequence);
        }

        _sessionSequences[sessionId] = sequenceNumber;
        return true;
    }

    /// <summary>
    /// QoSキューを処理してフレームを送信（バックグラウンドタスク）
    /// </summary>
    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        Log.Information("QoS queue processing started");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // キューから優先度順にフレームを取得
                var frame = await _qosQueue.DequeueAsync(cancellationToken);

                // RetryPolicyを使用して送信
                await _retryPolicy.ExecuteAsync(async () =>
                {
                    await SendFrameInternalAsync(frame, cancellationToken);
                    return Task.CompletedTask;
                }, $"SendFrame-{frame.SessionId}-{frame.SequenceNumber}", cancellationToken);

                // 定期的に統計をログ出力
                if (_qosQueue.TotalDequeued % 1000 == 0)
                {
                    _qosQueue.LogStatistics();
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("QoS queue processing cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in QoS queue processing");
        }

        Log.Information("QoS queue processing stopped");
    }

    public void Dispose()
    {
        if (_isRunning)
        {
            _device.StopCapture();
            _device.Close();
            _isRunning = false;
        }

        // バックグラウンドタスクを停止
        try
        {
            _backgroundTasksCts?.Cancel();
            _qosProcessingTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // 正常なキャンセル
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // 正常なキャンセル
        }
        finally
        {
            _backgroundTasksCts?.Dispose();
        }

        _receiveChannel.Writer.Complete();
        _qosQueue?.Dispose();
        _device?.Dispose();
        _cryptoEngine?.Dispose();

        Log.Information("SecureEthernetTransceiver disposed");
    }
}