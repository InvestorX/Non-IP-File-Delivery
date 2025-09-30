using System;

namespace NonIPFileDelivery.Exceptions;

/// <summary>
/// 非IP送受信機システムの基底例外クラス
/// </summary>
public class TransceiverException : Exception
{
    public string ErrorCode { get; }
    public DateTime OccurredAt { get; }

    public TransceiverException(string errorCode, string message) 
        : base(message)
    {
        ErrorCode = errorCode;
        OccurredAt = DateTime.UtcNow;
    }

    public TransceiverException(string errorCode, string message, Exception innerException) 
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        OccurredAt = DateTime.UtcNow;
    }
}

/// <summary>
/// ネットワーク関連の例外
/// </summary>
public class NetworkException : TransceiverException
{
    public string? InterfaceName { get; }

    public NetworkException(string message, string? interfaceName = null) 
        : base("NET_ERROR", message)
    {
        InterfaceName = interfaceName;
    }

    public NetworkException(string message, Exception innerException, string? interfaceName = null) 
        : base("NET_ERROR", message, innerException)
    {
        InterfaceName = interfaceName;
    }
}

/// <summary>
/// セキュリティ検閲関連の例外
/// </summary>
public class SecurityException : TransceiverException
{
    public string? ThreatName { get; }
    public string? FileName { get; }

    public SecurityException(string message, string? threatName = null, string? fileName = null) 
        : base("SEC_ERROR", message)
    {
        ThreatName = threatName;
        FileName = fileName;
    }
}

/// <summary>
/// フレーム処理関連の例外
/// </summary>
public class FrameException : TransceiverException
{
    public ushort? SequenceNumber { get; }

    public FrameException(string message, ushort? sequenceNumber = null) 
        : base("FRAME_ERROR", message)
    {
        SequenceNumber = sequenceNumber;
    }

    public FrameException(string message, Exception innerException, ushort? sequenceNumber = null) 
        : base("FRAME_ERROR", message, innerException)
    {
        SequenceNumber = sequenceNumber;
    }
}
