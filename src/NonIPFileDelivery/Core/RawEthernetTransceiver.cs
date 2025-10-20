using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using System.Net.NetworkInformation;
using System.Threading.Channels;
using Serilog;

namespace NonIpFileDelivery.Core;

/// <summary>
/// Raw Ethernetフレームの送受信を管理するトランシーバー
/// 非IPプロトコル通信の基盤層
/// </summary>
public class RawEthernetTransceiver : IRawEthernetTransceiver
{
    private readonly LibPcapLiveDevice _device;
    private readonly PhysicalAddress _localMacAddress;
    private readonly PhysicalAddress _remoteMacAddress;
    private readonly Channel<EthernetPacket> _receiveChannel;
    private readonly ushort _customEtherType;
    private bool _isRunning;

    // カスタムEtherTypeプロトコル番号（0x88B5-0x88B6は実験用）
    private const ushort CUSTOM_PROTOCOL_ETHERTYPE = 0x88B5;

    /// <summary>
    /// Raw Ethernetトランシーバーを初期化
    /// </summary>
    /// <param name="interfaceName">ネットワークインターフェース名</param>
    /// <param name="remoteMac">対向非IP送受信機のMACアドレス</param>
    /// <param name="channelCapacity">受信チャンネルバッファサイズ（デフォルト10000）</param>
    public RawEthernetTransceiver(
        string interfaceName, 
        string remoteMac, 
        int channelCapacity = 10000)
    {
        _device = LibPcapLiveDeviceList.Instance
            .FirstOrDefault(d => d.Name == interfaceName)
            ?? throw new ArgumentException($"Interface {interfaceName} not found");

        _localMacAddress = _device.MacAddress;
        _remoteMacAddress = PhysicalAddress.Parse(remoteMac.Replace(":", "-"));
        _customEtherType = CUSTOM_PROTOCOL_ETHERTYPE;

        // 高スループット対応の無制限チャンネル
        _receiveChannel = Channel.CreateBounded<EthernetPacket>(
            new BoundedChannelOptions(channelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

        Log.Information("RawEthernetTransceiver initialized: {Interface}, Local MAC: {LocalMac}, Remote MAC: {RemoteMac}",
            interfaceName, _localMacAddress, _remoteMacAddress);
    }

    /// <summary>
    /// 送受信を開始
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;

        // プロミスキャスモードで開始（全パケット受信）
        _device.Open(DeviceModes.Promiscuous, 1000);

        // カスタムEtherTypeのみをフィルタリング
        _device.Filter = $"ether proto 0x{_customEtherType:X4}";

        _device.OnPacketArrival += OnPacketArrival;
        _device.StartCapture();

        _isRunning = true;
        Log.Information("Packet capture started on {Interface}", _device.Name);
    }

    /// <summary>
    /// パケット受信イベントハンドラ
    /// </summary>
    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var rawPacket = e.GetPacket();
            var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);

            if (packet is EthernetPacket ethPacket &&
                ethPacket.DestinationHardwareAddress.Equals(_localMacAddress))
            {
                // 非ブロッキングで受信チャンネルに投入
                _receiveChannel.Writer.TryWrite(ethPacket);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing received packet");
        }
    }

    /// <summary>
    /// Raw Ethernetフレームを送信
    /// </summary>
    /// <param name="payload">送信するペイロードデータ</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    public async Task SendAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > 1500)
        {
            // MTUを超える場合はフラグメント化
            await SendFragmentedAsync(payload, cancellationToken);
            return;
        }

        var ethPacket = new EthernetPacket(
            _localMacAddress,
            _remoteMacAddress,
            (EthernetType)_customEtherType)
        {
            PayloadData = payload
        };

        await Task.Run(() => _device.SendPacket(ethPacket), cancellationToken);

        Log.Debug("Sent Ethernet frame: {Size} bytes", payload.Length);
    }

    /// <summary>
    /// 大きなペイロードをフラグメント化して送信
    /// </summary>
    private async Task SendFragmentedAsync(byte[] payload, CancellationToken cancellationToken)
    {
        const int fragmentSize = 1400; // MTU余裕を持たせる
        int totalFragments = (int)Math.Ceiling((double)payload.Length / fragmentSize);

        for (int i = 0; i < totalFragments; i++)
        {
            int offset = i * fragmentSize;
            int length = Math.Min(fragmentSize, payload.Length - offset);

            var fragment = new byte[length + 8]; // ヘッダ8バイト
            BitConverter.GetBytes(totalFragments).CopyTo(fragment, 0);
            BitConverter.GetBytes(i).CopyTo(fragment, 4);
            Array.Copy(payload, offset, fragment, 8, length);

            await SendAsync(fragment, cancellationToken);
            await Task.Delay(1, cancellationToken); // バースト制御
        }

        Log.Debug("Sent fragmented payload: {TotalSize} bytes in {Fragments} fragments",
            payload.Length, totalFragments);
    }

    /// <summary>
    /// 受信パケットを非同期で取得
    /// </summary>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>受信したEthernetパケット</returns>
    public async Task<EthernetPacket> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        return await _receiveChannel.Reader.ReadAsync(cancellationToken);
    }

    /// <summary>
    /// 受信チャンネルのストリームを取得
    /// </summary>
    public IAsyncEnumerable<EthernetPacket> ReceiveStream(CancellationToken cancellationToken)
    {
        return _receiveChannel.Reader.ReadAllAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_isRunning)
        {
            _device.StopCapture();
            _device.Close();
        }

        _receiveChannel.Writer.Complete();
        _device?.Dispose();

        Log.Information("RawEthernetTransceiver disposed");
    }
}