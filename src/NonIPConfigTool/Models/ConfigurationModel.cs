using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NonIPConfigTool.Models;

/// <summary>
/// 設定データモデル（バリデーション機能付き）
/// </summary>
public partial class ConfigurationModel : ObservableObject, INotifyDataErrorInfo
{
    private readonly Dictionary<string, List<string>> _errors = new();

    public bool HasErrors => _errors.Any();

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    // General Settings
    [ObservableProperty]
    private string _mode = "ActiveStandby";

    [ObservableProperty]
    private string _logLevel = "Warning";

    // Network Settings
    [ObservableProperty]
    private string _interface = "eth0";

    [ObservableProperty]
    private int _frameSize = 9000;

    [ObservableProperty]
    private bool _encryption = true;

    [ObservableProperty]
    private string _etherType = "0x88B5";

    // Security Settings
    [ObservableProperty]
    private bool _enableVirusScan = true;

    [ObservableProperty]
    private int _scanTimeout = 5000;

    [ObservableProperty]
    private string _quarantinePath = @"C:\NonIP\Quarantine";

    [ObservableProperty]
    private string _policyFile = "security_policy.ini";

    // Performance Settings
    [ObservableProperty]
    private int _maxMemoryMB = 8192;

    [ObservableProperty]
    private int _bufferSize = 65536;

    [ObservableProperty]
    private string _threadPool = "auto";

    // Redundancy Settings
    [ObservableProperty]
    private int _heartbeatInterval = 1000;

    [ObservableProperty]
    private int _failoverTimeout = 5000;

    [ObservableProperty]
    private string _dataSyncMode = "realtime";

    // Additional Settings
    [ObservableProperty]
    private string _configFilePath = "config.ini";

    [ObservableProperty]
    private bool _isDirty = false;

    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
            return _errors.Values.SelectMany(e => e);

        return _errors.ContainsKey(propertyName) ? _errors[propertyName] : Enumerable.Empty<string>();
    }

    private void AddError(string propertyName, string error)
    {
        if (!_errors.ContainsKey(propertyName))
            _errors[propertyName] = new List<string>();

        if (!_errors[propertyName].Contains(error))
        {
            _errors[propertyName].Add(error);
            OnErrorsChanged(propertyName);
        }
    }

    private void ClearErrors(string propertyName)
    {
        if (_errors.ContainsKey(propertyName))
        {
            _errors.Remove(propertyName);
            OnErrorsChanged(propertyName);
        }
    }

    private void OnErrorsChanged(string propertyName)
    {
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
    }

    /// <summary>
    /// プロパティ変更時のバリデーション
    /// </summary>
    partial void OnInterfaceChanged(string value)
    {
        ClearErrors(nameof(Interface));
        if (string.IsNullOrWhiteSpace(value))
            AddError(nameof(Interface), "ネットワークインターフェースは必須です");
        IsDirty = true;
    }

    partial void OnFrameSizeChanged(int value)
    {
        ClearErrors(nameof(FrameSize));
        if (value < 64 || value > 9000)
            AddError(nameof(FrameSize), "フレームサイズは64～9000の範囲で指定してください");
        IsDirty = true;
    }

    partial void OnMaxMemoryMBChanged(int value)
    {
        ClearErrors(nameof(MaxMemoryMB));
        if (value < 512 || value > 65536)
            AddError(nameof(MaxMemoryMB), "最大メモリは512～65536MBの範囲で指定してください");
        IsDirty = true;
    }

    partial void OnBufferSizeChanged(int value)
    {
        ClearErrors(nameof(BufferSize));
        if (value < 1024 || value > 1048576)
            AddError(nameof(BufferSize), "バッファサイズは1024～1048576バイトの範囲で指定してください");
        IsDirty = true;
    }

    partial void OnScanTimeoutChanged(int value)
    {
        ClearErrors(nameof(ScanTimeout));
        if (value < 100 || value > 60000)
            AddError(nameof(ScanTimeout), "スキャンタイムアウトは100～60000msの範囲で指定してください");
        IsDirty = true;
    }

    partial void OnHeartbeatIntervalChanged(int value)
    {
        ClearErrors(nameof(HeartbeatInterval));
        if (value < 100 || value > 10000)
            AddError(nameof(HeartbeatInterval), "ハートビート間隔は100～10000msの範囲で指定してください");
        IsDirty = true;
    }

    partial void OnFailoverTimeoutChanged(int value)
    {
        ClearErrors(nameof(FailoverTimeout));
        if (value < 1000 || value > 30000)
            AddError(nameof(FailoverTimeout), "フェイルオーバータイムアウトは1000～30000msの範囲で指定してください");
        IsDirty = true;
    }
}
