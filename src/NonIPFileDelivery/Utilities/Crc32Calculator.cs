using System.Buffers.Binary;
using System.IO.Hashing;
using System.Threading;

namespace NonIPFileDelivery.Utilities;

/// <summary>
/// CRC32チェックサム計算ユーティリティ
/// .NET 8のSystem.IO.Hashing.Crc32を使用（ハードウェアアクセラレーション対応）
/// </summary>
public static class Crc32Calculator
{
    /// <summary>
    /// データのCRC32チェックサムを計算
    /// </summary>
    /// <param name="data">計算対象のバイト配列</param>
    /// <returns>CRC32チェックサム値（32ビット符号なし整数）</returns>
    /// <exception cref="ArgumentNullException">dataがnullの場合</exception>
    public static uint Calculate(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) return 0;
        return Calculate(data.AsSpan());
    }

    /// <summary>
    /// データのCRC32チェックサムを計算（Span版 - ゼロコピー）
    /// </summary>
    /// <param name="data">計算対象のスパン</param>
    /// <returns>CRC32チェックサム値</returns>
    public static uint Calculate(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0;

        try
        {
            var hash = Crc32.Hash(data);
            return BinaryPrimitives.ReadUInt32BigEndian(hash);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("CRC32 calculation failed", ex);
        }
    }

    /// <summary>
    /// ストリームのCRC32チェックサムを計算（大容量データ向け）
    /// </summary>
    public static async Task<uint> CalculateAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(stream));

        var crc32 = new Crc32();
        var buffer = new byte[81920];
        int bytesRead;

        try
        {
            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                crc32.Append(buffer.AsSpan(0, bytesRead));
            }
            var hash = crc32.GetCurrentHash();
            return BinaryPrimitives.ReadUInt32BigEndian(hash);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("CRC32 stream calculation failed", ex);
        }
    }

    /// <summary>
    /// 複数のデータ片を連結してCRC32を計算
    /// </summary>
    public static uint CalculateComposite(IEnumerable<byte[]> dataParts)
    {
        ArgumentNullException.ThrowIfNull(dataParts);
        
        var crc32 = new Crc32();
        var hasData = false;
        
        foreach (var part in dataParts)
        {
            if (part != null && part.Length > 0)
            {
                crc32.Append(part);
                hasData = true;
            }
        }
        
        if (!hasData) return 0;
        
        var hash = crc32.GetCurrentHash();
        return BinaryPrimitives.ReadUInt32BigEndian(hash);
    }

    /// <summary>
    /// 複数のデータ片を連結してCRC32を計算（params 配列版）
    /// </summary>
    public static uint CalculateComposite(params byte[][] dataParts)
    {
        return CalculateComposite((IEnumerable<byte[]>)dataParts);
    }
}
