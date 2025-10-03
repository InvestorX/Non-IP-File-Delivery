using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;
using NonIPFileDelivery.Services;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// ClamAVマルウェアスキャナー
    /// clamdソケット通信（INSTREAMプロトコル）を使用
    /// </summary>
    public class ClamAVScanner
    {
        private readonly ILoggingService _logger;
        private readonly string _clamdHost;
        private readonly int _clamdPort;

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="logger">ロギングサービス</param>
        /// <param name="clamdHost">clamdホスト（デフォルト: localhost）</param>
        /// <param name="clamdPort">clamdポート（デフォルト: 3310）</param>
        public ClamAVScanner(ILoggingService logger, string clamdHost = "localhost", int clamdPort = 3310)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clamdHost = clamdHost;
            _clamdPort = clamdPort;

            _logger.Info($"ClamAVScanner initialized: {_clamdHost}:{_clamdPort}");
        }

        /// <summary>
        /// clamdへの接続テスト
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_clamdHost, _clamdPort);

                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.ASCII);

                await writer.WriteLineAsync("PING");
                var response = await reader.ReadLineAsync();

                var isConnected = response?.Trim() == "PONG";
                _logger.Info($"ClamAV connection test: {(isConnected ? "OK" : "FAIL")}");
                return isConnected;
            }
            catch (Exception ex)
            {
                _logger.Error($"ClamAV connection test failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// データをClamAVでスキャン
        /// </summary>
        /// <param name="data">スキャン対象データ</param>
        /// <param name="timeoutMs">タイムアウト（ミリ秒）</param>
        /// <returns>スキャン結果</returns>
        public async Task<ClamAVScanResult> ScanAsync(byte[] data, int timeoutMs = 5000)
        {
            if (data == null || data.Length == 0)
            {
                _logger.Warning("Attempted to scan empty data");
                return new ClamAVScanResult { IsClean = true };
            }

            try
            {
                _logger.Debug($"Scanning {data.Length} bytes with ClamAV...");

                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(_clamdHost, _clamdPort);
                var completedTask = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));

                if (completedTask != connectTask)
                {
                    _logger.Warning($"ClamAV connection timeout ({timeoutMs}ms)");
                    return new ClamAVScanResult
                    {
                        IsClean = false,
                        ErrorMessage = "Connection timeout"
                    };
                }

                using var stream = client.GetStream();
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;

                // INSTREAMプロトコル: データをチャンク単位で送信
                var command = Encoding.ASCII.GetBytes("zINSTREAM\0");
                await stream.WriteAsync(command, 0, command.Length);

                // データサイズを送信（4バイト、ネットワークバイトオーダー）
                var sizeBytes = BitConverter.GetBytes(data.Length);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(sizeBytes);
                }
                await stream.WriteAsync(sizeBytes, 0, sizeBytes.Length);

                // データ本体を送信
                await stream.WriteAsync(data, 0, data.Length);

                // 終了マーカー（サイズ0）を送信
                var endMarker = new byte[4] { 0, 0, 0, 0 };
                await stream.WriteAsync(endMarker, 0, endMarker.Length);

                // 応答を受信
                using var reader = new StreamReader(stream, Encoding.ASCII);
                var response = await reader.ReadLineAsync();

                if (string.IsNullOrEmpty(response))
                {
                    _logger.Warning("ClamAV returned empty response");
                    return new ClamAVScanResult
                    {
                        IsClean = false,
                        ErrorMessage = "Empty response from ClamAV"
                    };
                }

                _logger.Debug($"ClamAV response: {response}");

                // 応答解析: "stream: OK" or "stream: Win.Test.EICAR_HDB-1 FOUND"
                if (response.Contains("OK"))
                {
                    _logger.Debug("ClamAV scan completed: No threats detected");
                    return new ClamAVScanResult { IsClean = true };
                }
                else if (response.Contains("FOUND"))
                {
                    var virusName = ExtractVirusName(response);
                    _logger.Warning($"ClamAV detected virus: {virusName}");
                    return new ClamAVScanResult
                    {
                        IsClean = false,
                        VirusName = virusName,
                        Details = response
                    };
                }
                else
                {
                    _logger.Warning($"Unexpected ClamAV response: {response}");
                    return new ClamAVScanResult
                    {
                        IsClean = false,
                        ErrorMessage = $"Unexpected response: {response}"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"ClamAV scan error: {ex.Message}", ex);
                return new ClamAVScanResult
                {
                    IsClean = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// ClamAV応答からウイルス名を抽出
        /// </summary>
        private string ExtractVirusName(string response)
        {
            // 例: "stream: Win.Test.EICAR_HDB-1 FOUND"
            var parts = response.Split(':');
            if (parts.Length >= 2)
            {
                var virusPart = parts[1].Trim();
                return virusPart.Replace(" FOUND", "").Trim();
            }
            return "Unknown";
        }

        /// <summary>
        /// ClamAVバージョンを取得
        /// </summary>
        public async Task<string?> GetVersionAsync()
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_clamdHost, _clamdPort);

                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.ASCII);

                await writer.WriteLineAsync("VERSION");
                var version = await reader.ReadLineAsync();

                _logger.Info($"ClamAV version: {version}");
                return version;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get ClamAV version: {ex.Message}", ex);
                return null;
            }
        }
    }
}
