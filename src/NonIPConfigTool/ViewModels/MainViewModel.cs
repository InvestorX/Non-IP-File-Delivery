using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NonIPConfigTool.Models;
using NonIPFileDelivery.Services;
using NonIPFileDelivery.Models;

namespace NonIPConfigTool.ViewModels;

/// <summary>
/// メインウィンドウのViewModel
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IConfigurationService _configService;

    [ObservableProperty]
    private ConfigurationModel _config;

    [ObservableProperty]
    private string _statusMessage = "準備完了";

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private ObservableCollection<string> _availableInterfaces = new();

    [ObservableProperty]
    private ObservableCollection<string> _modeOptions = new() { "ActiveStandby", "LoadBalancing" };

    [ObservableProperty]
    private ObservableCollection<string> _logLevelOptions = new() { "Debug", "Info", "Warning", "Error" };

    public MainViewModel()
    {
        _configService = new ConfigurationService();
        _config = new ConfigurationModel();
        
        LoadNetworkInterfaces();
    }

    /// <summary>
    /// ネットワークインターフェースの読み込み
    /// </summary>
    private void LoadNetworkInterfaces()
    {
        try
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            AvailableInterfaces.Clear();
            
            foreach (var ni in interfaces.Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up))
            {
                AvailableInterfaces.Add(ni.Name);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"ネットワークインターフェースの取得エラー: {ex.Message}";
        }
    }

    /// <summary>
    /// 設定ファイルを開く
    /// </summary>
    [RelayCommand]
    private async Task OpenConfigAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "設定ファイル (*.ini)|*.ini|すべてのファイル (*.*)|*.*",
            Title = "設定ファイルを開く"
        };

        if (dialog.ShowDialog() == true)
        {
            await LoadConfigurationAsync(dialog.FileName);
        }
    }

    /// <summary>
    /// 設定ファイルを保存
    /// </summary>
    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        if (string.IsNullOrEmpty(Config.ConfigFilePath))
        {
            await SaveConfigAsAsync();
            return;
        }

        await SaveConfigurationAsync(Config.ConfigFilePath);
    }

    /// <summary>
    /// 名前を付けて保存
    /// </summary>
    [RelayCommand]
    private async Task SaveConfigAsAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "設定ファイル (*.ini)|*.ini|すべてのファイル (*.*)|*.*",
            Title = "設定ファイルを保存",
            FileName = "config.ini"
        };

        if (dialog.ShowDialog() == true)
        {
            Config.ConfigFilePath = dialog.FileName;
            await SaveConfigurationAsync(dialog.FileName);
        }
    }

    /// <summary>
    /// 新規設定
    /// </summary>
    [RelayCommand]
    private void NewConfig()
    {
        var result = MessageBox.Show(
            "現在の設定を破棄して新しい設定を作成しますか？",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            Config = new ConfigurationModel();
            StatusMessage = "新しい設定を作成しました";
        }
    }

    /// <summary>
    /// 設定の検証
    /// </summary>
    [RelayCommand]
    private void ValidateConfig()
    {
        try
        {
            var errors = new System.Collections.Generic.List<string>();

            // 基本的な検証
            if (string.IsNullOrWhiteSpace(Config.Interface))
                errors.Add("ネットワークインターフェースが指定されていません");

            if (Config.FrameSize < 64 || Config.FrameSize > 9000)
                errors.Add("フレームサイズは64～9000の範囲で指定してください");

            if (Config.MaxMemoryMB < 512 || Config.MaxMemoryMB > 65536)
                errors.Add("最大メモリは512～65536MBの範囲で指定してください");

            if (Config.ScanTimeout < 100 || Config.ScanTimeout > 60000)
                errors.Add("スキャンタイムアウトは100～60000msの範囲で指定してください");

            if (errors.Any())
            {
                MessageBox.Show(
                    $"以下の検証エラーがあります:\n\n{string.Join("\n", errors)}",
                    "検証エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(
                    "設定の検証に成功しました",
                    "検証成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                StatusMessage = "設定の検証に成功しました";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"検証中にエラーが発生しました: {ex.Message}",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 設定をファイルから読み込み
    /// </summary>
    private async Task LoadConfigurationAsync(string filePath)
    {
        try
        {
            IsLoading = true;
            StatusMessage = $"設定を読み込み中: {filePath}";

            await Task.Run(() =>
            {
                var configuration = _configService.LoadConfiguration(filePath);
                
                Application.Current.Dispatcher.Invoke(() =>
                {
                    // Configuration → ConfigurationModel への変換
                    Config.Mode = configuration.General?.Mode ?? "ActiveStandby";
                    Config.LogLevel = configuration.General?.LogLevel.ToString() ?? "Warning";
                    Config.Interface = configuration.Network?.Interface ?? "eth0";
                    Config.FrameSize = configuration.Network?.FrameSize ?? 9000;
                    Config.Encryption = configuration.Network?.Encryption ?? true;
                    Config.EtherType = configuration.Network?.EtherType ?? "0x88B5";
                    Config.EnableVirusScan = configuration.Security?.EnableVirusScan ?? true;
                    Config.ScanTimeout = configuration.Security?.ScanTimeout ?? 5000;
                    Config.QuarantinePath = configuration.Security?.QuarantinePath ?? @"C:\NonIP\Quarantine";
                    Config.PolicyFile = configuration.Security?.PolicyFile ?? "security_policy.ini";
                    Config.MaxMemoryMB = configuration.Performance?.MaxMemoryMB ?? 8192;
                    Config.BufferSize = configuration.Performance?.BufferSize ?? 65536;
                    Config.ThreadPool = configuration.Performance?.ThreadPool ?? "auto";
                    Config.HeartbeatInterval = configuration.Redundancy?.HeartbeatInterval ?? 1000;
                    Config.FailoverTimeout = configuration.Redundancy?.FailoverTimeout ?? 5000;
                    Config.DataSyncMode = configuration.Redundancy?.DataSyncMode ?? "realtime";
                    Config.ConfigFilePath = filePath;
                    Config.IsDirty = false;
                });
            });

            StatusMessage = $"設定を読み込みました: {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"設定ファイルの読み込みに失敗しました: {ex.Message}",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusMessage = $"読み込みエラー: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 設定をファイルに保存
    /// </summary>
    private async Task SaveConfigurationAsync(string filePath)
    {
        try
        {
            IsLoading = true;
            StatusMessage = $"設定を保存中: {filePath}";

            await Task.Run(() =>
            {
                // ConfigurationModel → Configuration への変換
                var configuration = new Configuration
                {
                    General = new GeneralConfig
                    {
                        Mode = Config.Mode,
                        LogLevel = Enum.Parse<NonIPFileDelivery.Models.LogLevel>(Config.LogLevel).ToString()
                    },
                    Network = new NetworkConfig
                    {
                        Interface = Config.Interface,
                        FrameSize = Config.FrameSize,
                        Encryption = Config.Encryption,
                        EtherType = Config.EtherType
                    },
                    Security = new SecurityConfig
                    {
                        EnableVirusScan = Config.EnableVirusScan,
                        ScanTimeout = Config.ScanTimeout,
                        QuarantinePath = Config.QuarantinePath,
                        PolicyFile = Config.PolicyFile
                    },
                    Performance = new PerformanceConfig
                    {
                        MaxMemoryMB = Config.MaxMemoryMB,
                        BufferSize = Config.BufferSize,
                        ThreadPool = Config.ThreadPool
                    },
                    Redundancy = new RedundancyConfig
                    {
                        HeartbeatInterval = Config.HeartbeatInterval,
                        FailoverTimeout = Config.FailoverTimeout,
                        DataSyncMode = Config.DataSyncMode
                    }
                };

                _configService.SaveConfiguration(configuration, filePath);
                
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Config.IsDirty = false;
                });
            });

            StatusMessage = $"設定を保存しました: {Path.GetFileName(filePath)}";
            
            MessageBox.Show(
                "設定ファイルを正常に保存しました",
                "保存完了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"設定ファイルの保存に失敗しました: {ex.Message}",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusMessage = $"保存エラー: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 接続テスト（ネットワークインターフェースの疎通確認）
    /// </summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(Config.Interface))
        {
            MessageBox.Show(
                "ネットワークインターフェースを選択してください",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = $"接続テスト中: {Config.Interface}";

            await Task.Run(() =>
            {
                var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                var targetInterface = interfaces.FirstOrDefault(ni => ni.Name == Config.Interface);

                if (targetInterface == null)
                {
                    throw new InvalidOperationException($"インターフェース '{Config.Interface}' が見つかりません");
                }

                if (targetInterface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    throw new InvalidOperationException($"インターフェース '{Config.Interface}' はダウンしています");
                }

                // 統計情報を取得
                var stats = targetInterface.GetIPv4Statistics();
                var speed = targetInterface.Speed / 1_000_000; // Mbps

                Application.Current.Dispatcher.Invoke(() =>
                {
                    var message = $"接続テスト成功\n\n" +
                                  $"インターフェース: {targetInterface.Name}\n" +
                                  $"状態: {targetInterface.OperationalStatus}\n" +
                                  $"速度: {speed} Mbps\n" +
                                  $"送信バイト: {stats.BytesSent:N0}\n" +
                                  $"受信バイト: {stats.BytesReceived:N0}\n" +
                                  $"送信パケット: {stats.UnicastPacketsSent:N0}\n" +
                                  $"受信パケット: {stats.UnicastPacketsReceived:N0}";

                    MessageBox.Show(
                        message,
                        "接続テスト成功",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });
            });

            StatusMessage = $"接続テスト成功: {Config.Interface}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"接続テストに失敗しました:\n{ex.Message}",
                "接続エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusMessage = $"接続テスト失敗: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// システム統計情報を表示
    /// </summary>
    [RelayCommand]
    private void ShowStatistics()
    {
        try
        {
            var stats = new System.Text.StringBuilder();
            stats.AppendLine("=== システム統計情報 ===\n");

            // メモリ情報
            var process = System.Diagnostics.Process.GetCurrentProcess();
            stats.AppendLine($"プロセスID: {process.Id}");
            stats.AppendLine($"メモリ使用量: {process.WorkingSet64 / 1024 / 1024:N0} MB");
            stats.AppendLine($"スレッド数: {process.Threads.Count}");
            stats.AppendLine($"ハンドル数: {process.HandleCount:N0}\n");

            // ネットワーク統計
            if (!string.IsNullOrWhiteSpace(Config.Interface))
            {
                var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                var targetInterface = interfaces.FirstOrDefault(ni => ni.Name == Config.Interface);

                if (targetInterface != null)
                {
                    var ipStats = targetInterface.GetIPv4Statistics();
                    stats.AppendLine($"=== ネットワーク統計 ({Config.Interface}) ===");
                    stats.AppendLine($"送信バイト: {ipStats.BytesSent:N0}");
                    stats.AppendLine($"受信バイト: {ipStats.BytesReceived:N0}");
                    stats.AppendLine($"送信パケット: {ipStats.UnicastPacketsSent:N0}");
                    stats.AppendLine($"受信パケット: {ipStats.UnicastPacketsReceived:N0}");
                    stats.AppendLine($"送信エラー: {ipStats.OutgoingPacketsDiscarded:N0}");
                    stats.AppendLine($"受信エラー: {ipStats.IncomingPacketsDiscarded:N0}\n");
                }
            }

            // 設定情報
            stats.AppendLine("=== 現在の設定 ===");
            stats.AppendLine($"モード: {Config.Mode}");
            stats.AppendLine($"ログレベル: {Config.LogLevel}");
            stats.AppendLine($"フレームサイズ: {Config.FrameSize} bytes");
            stats.AppendLine($"暗号化: {(Config.Encryption ? "有効" : "無効")}");
            stats.AppendLine($"ウイルススキャン: {(Config.EnableVirusScan ? "有効" : "無効")}");
            stats.AppendLine($"最大メモリ: {Config.MaxMemoryMB} MB");

            MessageBox.Show(
                stats.ToString(),
                "システム統計情報",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            StatusMessage = "統計情報を表示しました";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"統計情報の取得に失敗しました: {ex.Message}",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 設定のエクスポート（JSON形式）
    /// </summary>
    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
            Title = "設定をエクスポート",
            FileName = $"config_export_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                IsLoading = true;
                StatusMessage = "設定をエクスポート中...";

                await Task.Run(() =>
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(Config, 
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(dialog.FileName, json);
                });

                StatusMessage = $"設定をエクスポートしました: {Path.GetFileName(dialog.FileName)}";
                MessageBox.Show(
                    "設定を正常にエクスポートしました",
                    "エクスポート完了",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"エクスポートに失敗しました: {ex.Message}",
                    "エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// 設定のインポート（JSON形式）
    /// </summary>
    [RelayCommand]
    private async Task ImportConfigAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
            Title = "設定をインポート"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                IsLoading = true;
                StatusMessage = "設定をインポート中...";

                await Task.Run(() =>
                {
                    var json = File.ReadAllText(dialog.FileName);
                    var importedConfig = System.Text.Json.JsonSerializer.Deserialize<ConfigurationModel>(json);

                    if (importedConfig != null)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            Config.Mode = importedConfig.Mode;
                            Config.LogLevel = importedConfig.LogLevel;
                            Config.Interface = importedConfig.Interface;
                            Config.FrameSize = importedConfig.FrameSize;
                            Config.Encryption = importedConfig.Encryption;
                            Config.EtherType = importedConfig.EtherType;
                            Config.EnableVirusScan = importedConfig.EnableVirusScan;
                            Config.ScanTimeout = importedConfig.ScanTimeout;
                            Config.QuarantinePath = importedConfig.QuarantinePath;
                            Config.PolicyFile = importedConfig.PolicyFile;
                            Config.MaxMemoryMB = importedConfig.MaxMemoryMB;
                            Config.BufferSize = importedConfig.BufferSize;
                            Config.ThreadPool = importedConfig.ThreadPool;
                            Config.HeartbeatInterval = importedConfig.HeartbeatInterval;
                            Config.FailoverTimeout = importedConfig.FailoverTimeout;
                            Config.DataSyncMode = importedConfig.DataSyncMode;
                            Config.IsDirty = true;
                        });
                    }
                });

                StatusMessage = $"設定をインポートしました: {Path.GetFileName(dialog.FileName)}";
                MessageBox.Show(
                    "設定を正常にインポートしました",
                    "インポート完了",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"インポートに失敗しました: {ex.Message}",
                    "エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// アプリケーション終了
    /// </summary>
    [RelayCommand]
    private void Exit()
    {
        if (Config.IsDirty)
        {
            var result = MessageBox.Show(
                "設定が変更されています。保存せずに終了しますか？",
                "確認",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
                return;

            if (result == MessageBoxResult.No)
            {
                SaveConfigAsync().Wait();
            }
        }

        Application.Current.Shutdown();
    }
}
