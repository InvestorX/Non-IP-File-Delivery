using System;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

public interface INetworkService
{
    Task<bool> InitializeInterface(NetworkConfig config);
    Task<bool> StartListening();
    Task StopListening();
    Task<bool> SendFrame(byte[] data, string destinationMac);
    bool IsInterfaceReady { get; }
    event EventHandler<FrameReceivedEventArgs>? FrameReceived;
}

public class FrameReceivedEventArgs : EventArgs
{
    public byte[] Data { get; }
    public string SourceMac { get; }
    public DateTime Timestamp { get; }

    public FrameReceivedEventArgs(byte[] data, string sourceMac)
    {
        Data = data;
        SourceMac = sourceMac;
        Timestamp = DateTime.UtcNow;
    }
}