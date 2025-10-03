using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NonIPFileDelivery.Services
{
    /// <summary>
    /// ClamAVスキャナー実装（clamdソケット通信）
    /// </summary>
    public class ClamAVScanner
    {
        private readonly ILoggingService _logger;
        private readonly string _clamdHost;
        private readonly int _clamdPort;

        public ClamAVScanner(ILoggingService logger, string clamdHost = "localhost", int clamdPort = 3310)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clamdHost = clamdHost;
            _clamdPort = clamdPort;
        }

        /// <summary>
        /// clamdへの接続テスト（PING/PONG）
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                _logger.Debug($"Testing ClamAV connection to {_clamdHost}:{_clamdPort}");

                using var client = new TcpClient();
                await client.ConnectAsync(_clamdHost, _clamdPort);
                using var stream = client.GetStream();

                // PINGコマンド送信
                var pingCommand = Encoding.ASCII.GetBytes("zPING\0");
                await stream.WriteAsync(pingCommand, 0, pingCommand.Length);

                // レスポンス受信
                var buffer = new byte[1024];
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                var response = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();

                if (response == "PONG")
                {
                    _logger.Info($"ClamAV connection successful: {_clamdHost}:{_clamdPort}");
                    return true;
                }

                _logger.Warning($"ClamAV unexpected response: {response}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Warning($"ClamAV connection failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ClamAVバージョン取得
        /// </summary>
        public async Task<string?> GetVersionAsync()
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_clamdHost, _clamdPort);
                using var stream = client.GetStream();

                // VERSIONコマンド送信
                var versionCommand = Encoding.ASCII.GetBytes("zVERSION\0");
                await stream.WriteAsync(versionCommand, 0, versionCommand.Length);

                // レスポンス受信
                var buffer = new byte[1024];
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                var version = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();

                _logger.Info($"ClamAV version: {version}");
                return version;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get ClamAV version: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// データをClamAVでスキャン（INSTREAMプロトコル）
        /// </summary>
        public async Task<ClamAVScanResult> ScanAsync(byte[] data, int timeoutMs)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty", nameof(data));

            try
            {
                _logger.Debug($"Starting ClamAV scan ({data.Length} bytes, timeout: {timeoutMs}ms)");

                using var cts = new CancellationTokenSource(timeoutMs);
                using var client = new TcpClient();
                await client.ConnectAsync(_clamdHost, _clamdPort);
                using var stream = client.GetStream();

                // INSTREAMコマンド送信
                var instreamCommand = Encoding.ASCII.GetBytes("zINSTREAM\0");
                await stream.WriteAsync(instreamCommand, 0, instreamCommand.Length, cts.Token);

                // データサイズ送信（ビッグエンディアン）
                var sizeBytes = BitConverter.GetBytes(data.Length);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(sizeBytes);
                await stream.WriteAsync(sizeBytes, 0, sizeBytes.Length, cts.Token);

                // データ本体送信
                await stream.WriteAsync(data, 0, data.Length, cts.Token);

                // 終了マーカー送信（サイズ=0）
                await stream.WriteAsync(new byte[4], 0, 4, cts.Token);

                // レスポンス受信
                var buffer = new byte[2048];
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                var response = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();

                _logger.Debug($"ClamAV response: {response}");

                // レスポンス解析
                if (response.Contains("OK"))
                {
                    _logger.Info("ClamAV scan: Clean");
                    return new ClamAVScanResult
                    {
                        IsClean = true,
                        VirusName = null,
                        ErrorMessage = null
                    };
                }
                else if (response.Contains("FOUND"))
                {
                    var parts = response.Split(':');
                    var virusName = parts.Length > 1 ? parts[1].Replace("FOUND", "").Trim() : "Unknown";
                    _logger.Warning($"ClamAV detected: {virusName}");
                    return new ClamAVScanResult
                    {
                        IsClean = false,
                        VirusName = virusName,
                        ErrorMessage = null
                    };
                }

                _logger.Warning($"ClamAV unexpected response: {response}");
                return new ClamAVScanResult
                {
                    IsClean = false,
                    VirusName = null,
                    ErrorMessage = response
                };
            }
            catch (OperationCanceledException)
            {
                _logger.Warning($"ClamAV scan timed out ({timeoutMs}ms)");
                return new ClamAVScanResult
                {
                    IsClean = false,
                    VirusName = null,
                    ErrorMessage = $"Scan timeout ({timeoutMs}ms)"
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"ClamAV scan error: {ex.Message}", ex);
                return new ClamAVScanResult
                {
                    IsClean = false,
                    VirusName = null,
                    ErrorMessage = ex.Message
                };
            }
        }
    }

    /// <summary>
    /// ClamAVスキャン結果
    /// </summary>
    public class ClamAVScanResult
    {
        public bool IsClean { get; set; }
        public string? VirusName { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
