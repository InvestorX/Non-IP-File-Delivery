using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

public interface IFrameService
{
    // 基本フレーム操作
    byte[] SerializeFrame(NonIPFrame frame);
    NonIPFrame? DeserializeFrame(byte[] data);
    NonIPFrame CreateHeartbeatFrame(byte[] sourceMac);
    NonIPFrame CreateDataFrame(byte[] sourceMac, byte[] destinationMac, byte[] data, FrameFlags flags = FrameFlags.None);
    NonIPFrame CreateFileTransferFrame(byte[] sourceMac, byte[] destinationMac, FileTransferFrame fileData);
    bool ValidateFrame(NonIPFrame frame, byte[] rawData);
    uint CalculateChecksum(byte[] data);
    
    // ACK/NAK再送機構
    NonIPFrame CreateAckFrame(byte[] sourceMac, byte[] destinationMac, ushort sequenceNumber);
    NonIPFrame CreateNackFrame(byte[] sourceMac, byte[] destinationMac, ushort sequenceNumber, string reason = "");
    void RegisterPendingAck(NonIPFrame frame);
    bool ProcessAck(ushort sequenceNumber);
    NonIPFrame? GetPendingFrame(ushort sequenceNumber);
    List<NonIPFrame> GetTimedOutFrames();
    void ClearRetryQueue();
    (int PendingAcks, int RetryQueueSize) GetStatistics();
    
    // フラグメント処理
    Task<List<NonIPFrame>> CreateFragmentedFramesAsync(byte[] sourceMac, byte[] destinationMac, byte[] data, int maxFragmentSize = 8000, FrameFlags additionalFlags = FrameFlags.None);
    Task<byte[]?> AddFragmentAndReassembleAsync(NonIPFrame fragmentFrame);
    Task<double?> GetFragmentProgressAsync(Guid fragmentGroupId);
}