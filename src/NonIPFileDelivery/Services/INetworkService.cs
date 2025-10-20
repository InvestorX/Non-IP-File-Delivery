using System;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

/// <summary>
/// フレーム送信の優先度
/// </summary>
public enum FramePriority
{
    Low = 0,
    Normal = 1,
    High = 2
}

public interface INetworkService
{
    Task<bool> InitializeInterface(NetworkConfig config);
    Task<bool> StartListening();
    Task StopListening();
    Task<bool> SendFrame(byte[] data, string destinationMac);
    Task<bool> SendFrame(byte[] data, string destinationMac, FramePriority priority);
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