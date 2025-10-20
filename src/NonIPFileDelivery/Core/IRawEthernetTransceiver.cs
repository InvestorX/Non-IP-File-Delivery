using PacketDotNet;

namespace NonIpFileDelivery.Core;

/// <summary>
/// Raw Ethernetフレームの送受信インターフェース
/// 非IPプロトコル通信の基盤層
/// テスト可能性のためにインターフェース化
/// </summary>
public interface IRawEthernetTransceiver : IDisposable
{
    /// <summary>
    /// 送受信を開始
    /// </summary>
    void Start();

    /// <summary>
    /// Raw Ethernetフレームを送信
    /// </summary>
    /// <param name="payload">送信するペイロードデータ</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    Task SendAsync(byte[] payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// 受信パケットを非同期で取得
    /// </summary>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>受信したEthernetパケット</returns>
    Task<EthernetPacket> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 受信チャンネルのストリームを取得
    /// </summary>
    IAsyncEnumerable<EthernetPacket> ReceiveStream(CancellationToken cancellationToken);
}
