using System.IO.Hashing;

namespace NonIPFileDelivery.Utilities;

/// <summary>
/// CRC32チェックサム計算ユーティリティ
/// .NET標準のハードウェアアクセラレーション対応CRC32を使用
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
        
        if (data.Length == 0)
        {
            // 空配列の場合は初期CRC値を返す
            return 0;
        }
        
        return Calculate(data.AsSpan());
    }

    /// <summary>
    /// データのCRC32チェックサムを計算（Span版 - ゼロコピー）
    /// </summary>
    /// <param name="data">計算対象のスパン</param>
    /// <returns>CRC32チェックサム値（32ビット符号なし整数）</returns>
    public static uint Calculate(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 0;
        }
        
        try
        {
            // .NET 8のSystem.IO.Hashing.Crc32を使用
            // ハードウェアアクセラレーション対応（SSE4.2等）
            var hash = Crc32.Hash(data);
            
            // ビッグエンディアン形式でuintに変換
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("CRC32 calculation failed", ex);
        }
    }

    /// <summary>
    /// ストリームのCRC32チェックサムを計算
    /// 大きなファイルの検証に最適
    /// </summary>
     /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>CRC32チェックサム値</returns>
    /// <exception cref="ArgumentNullException">streamがnullの場合</exception>
    public static async Task<uint> CalculateAsync(
        Stream stream, 
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        
        if (!stream.CanRead)
        {
            throw new ArgumentException("Stream must be readable", nameof(stream));
        }
        
        var crc32 = new Crc32();
        var buffer = new byte[81920]; // 80KB バッファ
        int bytesRead;
        
        try
        {
            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                crc32.Append(buffer.AsSpan(0, bytesRead));
            }
            
            var hash = crc32.GetCurrentHash();
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash);
        }
        catch (OperationCanceledException)
        {
            throw; // キャンセルは再スロー
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("CRC32 stream calculation failed", ex);
        }
    }

    /// <summary>
    /// 複数のデータ片を連結してCRC32を計算
    /// フレームの増分検証に使用
    /// </summary>
    /// <param name="dataParts">計算対象のデータ片の配列</param>
    /// <returns>CRC32チェックサム値</returns>    
    /// <exception cref="ArgumentNullException">dataPartsがnullの場合</exception>
    public static uint CalculateComposite(params ReadOnlySpan<byte>[] dataParts)
    {
        ArgumentNullException.ThrowIfNull(dataParts);
        
        if (dataParts.Length == 0)
        {
            return 0;
        }
        
        var crc32 = new Crc32();
        
        foreach (var part in dataParts)
        {
            if (!part.IsEmpty)
            {
                crc32.Append(part);
            }
        }
        
        var hash = crc32.GetCurrentHash();
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash);
    }
}
