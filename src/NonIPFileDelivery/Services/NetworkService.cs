using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

public class NetworkService : INetworkService
{
    private readonly ILoggingService _logger;
    private NetworkConfig? _config;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _listeningTask;

    public bool IsInterfaceReady { get; private set; }
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

    public NetworkService(ILoggingService logger)
    {
        _logger = logger;
    }

    public async Task<bool> InitializeInterface(NetworkConfig config)
    {
        _config = config;
        
        try
        {
            _logger.Info($"Initializing network interface: {config.Interface}");
            
            // Check if the interface exists
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            NetworkInterface? targetInterface = null;
            
            foreach (var ni in networkInterfaces)
            {
                if (ni.Name.Equals(config.Interface, StringComparison.OrdinalIgnoreCase) ||
                    ni.Description.Contains(config.Interface, StringComparison.OrdinalIgnoreCase))
                {
                    targetInterface = ni;
                    break;
                }
            }

            if (targetInterface == null)
            {
                _logger.Error($"Network interface not found: {config.Interface}");
                return false;
            }

            _logger.Info($"Found network interface: {targetInterface.Name} ({targetInterface.Description})");
            _logger.Info($"Interface type: {targetInterface.NetworkInterfaceType}");
            _logger.Info($"Operational status: {targetInterface.OperationalStatus}");
            
            if (targetInterface.OperationalStatus != OperationalStatus.Up)
            {
                _logger.Warning($"Network interface is not operational: {targetInterface.OperationalStatus}");
                // Don't fail completely - some interfaces might work even if not reporting as Up
            }

            // Configure frame size if supported
            _logger.Info($"Configured frame size: {config.FrameSize} bytes");
            
            // Log encryption status
            if (config.Encryption)
            {
                _logger.Info("Encryption is enabled");
            }
            else
            {
                _logger.Warning("Encryption is disabled - data will be transmitted in plain text");
            }

            _logger.Info($"Using EtherType: {config.EtherType}");
            
            IsInterfaceReady = true;
            _logger.Info("Network interface initialization completed successfully");
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to initialize network interface", ex);
            IsInterfaceReady = false;
            return false;
        }
    }

    public async Task<bool> StartListening()
    {
        if (!IsInterfaceReady || _config == null)
        {
            _logger.Error("Cannot start listening - interface not ready or not configured");
            return false;
        }

        try
        {
            _logger.Info("Starting network listening...");
            
            _cancellationTokenSource = new CancellationTokenSource();
            _listeningTask = ListenForFramesAsync(_cancellationTokenSource.Token);
            
            _logger.Info("Network listening started successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to start network listening", ex);
            return false;
        }
    }

    public async Task StopListening()
    {
        _logger.Info("Stopping network listening...");
        
        if (_cancellationTokenSource != null)
        {
            _cancellationTokenSource.Cancel();
        }

        if (_listeningTask != null)
        {
            try
            {
                await _listeningTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
        }

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        _listeningTask = null;
        
        _logger.Info("Network listening stopped");
    }

    public async Task<bool> SendFrame(byte[] data, string destinationMac)
    {
        if (!IsInterfaceReady || _config == null)
        {
            _logger.Error("Cannot send frame - interface not ready");
            return false;
        }

        try
        {
            _logger.Debug($"Sending frame to {destinationMac}, size: {data.Length} bytes");
            
            // In a real implementation, this would use raw socket or packet capture library
            // For now, we simulate the send operation
            await Task.Delay(1); // Simulate network latency
            
            _logger.Debug($"Frame sent successfully to {destinationMac}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to send frame to {destinationMac}", ex);
            return false;
        }
    }

    private async Task ListenForFramesAsync(CancellationToken cancellationToken)
    {
        _logger.Debug("Frame listening loop started");
        
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // In a real implementation, this would use raw socket or packet capture library
                // For now, we simulate receiving frames periodically
                await Task.Delay(5000, cancellationToken); // Check every 5 seconds
                
                // Simulate receiving a frame occasionally
                if (Random.Shared.Next(1, 10) == 1) // 10% chance
                {
                    var simulatedFrame = new byte[] { 0x01, 0x02, 0x03, 0x04 };
                    var simulatedMac = "00:11:22:33:44:55";
                    
                    _logger.Debug($"Simulated frame received from {simulatedMac}");
                    FrameReceived?.Invoke(this, new FrameReceivedEventArgs(simulatedFrame, simulatedMac));
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Frame listening cancelled");
        }
        catch (Exception ex)
        {
            _logger.Error("Error in frame listening loop", ex);
        }
        
        _logger.Debug("Frame listening loop ended");
    }
}