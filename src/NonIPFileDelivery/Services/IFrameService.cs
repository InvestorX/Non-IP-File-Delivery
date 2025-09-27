using System;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

public interface IFrameService
{
    byte[] SerializeFrame(NonIPFrame frame);
    NonIPFrame? DeserializeFrame(byte[] data);
    NonIPFrame CreateHeartbeatFrame(byte[] sourceMac);
    NonIPFrame CreateDataFrame(byte[] sourceMac, byte[] destinationMac, byte[] data, FrameFlags flags = FrameFlags.None);
    NonIPFrame CreateFileTransferFrame(byte[] sourceMac, byte[] destinationMac, FileTransferFrame fileData);
    bool ValidateFrame(NonIPFrame frame, byte[] rawData);
    uint CalculateChecksum(byte[] data);
}