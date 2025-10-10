using NonIPFileDelivery.Models;

namespace NonIPWebConfig.Services;

/// <summary>
/// 設定の詳細な検証とエラーメッセージを提供するサービス
/// </summary>
public class ConfigValidationService
{
    /// <summary>
    /// 設定を検証し、詳細なエラーメッセージを返す
    /// </summary>
    public (bool IsValid, List<string> Errors) ValidateDetailed(Configuration config)
    {
        var errors = new List<string>();

        // General 検証
        if (config.General != null)
        {
            var validModes = new[] { "ActiveStandby", "LoadBalancing", "Standalone" };
            if (!string.IsNullOrWhiteSpace(config.General.Mode) && 
                !validModes.Contains(config.General.Mode))
            {
                errors.Add($"無効な動作モード: {config.General.Mode}。有効な値: {string.Join(", ", validModes)}");
            }

            var validLogLevels = new[] { "Debug", "Info", "Warning", "Error" };
            if (!string.IsNullOrWhiteSpace(config.General.LogLevel) &&
                !validLogLevels.Contains(config.General.LogLevel))
            {
                errors.Add($"無効なログレベル: {config.General.LogLevel}。有効な値: {string.Join(", ", validLogLevels)}");
            }
        }

        // Network 検証
        if (config.Network != null)
        {
            if (config.Network.FrameSize <= 0 || config.Network.FrameSize > 9000)
            {
                errors.Add($"フレームサイズが範囲外です: {config.Network.FrameSize}。有効範囲: 1〜9000");
            }

            if (string.IsNullOrWhiteSpace(config.Network.Interface))
            {
                errors.Add("ネットワークインターフェースが指定されていません");
            }

            if (!string.IsNullOrWhiteSpace(config.Network.EtherType))
            {
                if (!config.Network.EtherType.StartsWith("0x") || config.Network.EtherType.Length != 6)
                {
                    errors.Add($"無効なEtherType形式: {config.Network.EtherType}。形式: 0xXXXX");
                }
            }
        }

        // Security 検証
        if (config.Security != null)
        {
            if (config.Security.ScanTimeout < 0 || config.Security.ScanTimeout > 60000)
            {
                errors.Add($"スキャンタイムアウトが範囲外です: {config.Security.ScanTimeout}。有効範囲: 0〜60000ミリ秒");
            }

            if (config.Security.EnableVirusScan && 
                string.IsNullOrWhiteSpace(config.Security.QuarantinePath))
            {
                errors.Add("ウイルススキャンが有効ですが、隔離パスが指定されていません");
            }
        }

        // Performance 検証
        if (config.Performance != null)
        {
            if (config.Performance.MaxMemoryMB <= 0 || config.Performance.MaxMemoryMB > 65536)
            {
                errors.Add($"最大メモリが範囲外です: {config.Performance.MaxMemoryMB}MB。有効範囲: 1〜65536MB");
            }

            if (config.Performance.BufferSize <= 0 || config.Performance.BufferSize > 1048576)
            {
                errors.Add($"バッファサイズが範囲外です: {config.Performance.BufferSize}。有効範囲: 1〜1048576バイト");
            }

            var validThreadPools = new[] { "auto", "manual" };
            if (!string.IsNullOrWhiteSpace(config.Performance.ThreadPool) &&
                !validThreadPools.Contains(config.Performance.ThreadPool.ToLower()))
            {
                errors.Add($"無効なスレッドプール設定: {config.Performance.ThreadPool}。有効な値: {string.Join(", ", validThreadPools)}");
            }
        }

        // Redundancy 検証
        if (config.Redundancy != null)
        {
            if (config.Redundancy.HeartbeatInterval < 100 || config.Redundancy.HeartbeatInterval > 10000)
            {
                errors.Add($"ハートビート間隔が範囲外です: {config.Redundancy.HeartbeatInterval}ミリ秒。有効範囲: 100〜10000");
            }

            if (config.Redundancy.FailoverTimeout < config.Redundancy.HeartbeatInterval)
            {
                errors.Add($"フェイルオーバータイムアウト({config.Redundancy.FailoverTimeout}ms)はハートビート間隔({config.Redundancy.HeartbeatInterval}ms)より大きい必要があります");
            }

            var validSyncModes = new[] { "realtime", "batch", "manual" };
            if (!string.IsNullOrWhiteSpace(config.Redundancy.DataSyncMode) &&
                !validSyncModes.Contains(config.Redundancy.DataSyncMode.ToLower()))
            {
                errors.Add($"無効なデータ同期モード: {config.Redundancy.DataSyncMode}。有効な値: {string.Join(", ", validSyncModes)}");
            }
        }

        return (errors.Count == 0, errors);
    }
}
