using System.Windows;

namespace NonIPConfigTool;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        // コマンドライン引数の処理（従来のコンソール版機能との互換性）
        if (e.Args.Length > 0)
        {
            HandleCommandLineArgs(e.Args);
            Shutdown();
        }
    }

    private void HandleCommandLineArgs(string[] args)
    {
        // コマンドライン引数の処理ロジック
        // （必要に応じて後で実装）
    }
}
