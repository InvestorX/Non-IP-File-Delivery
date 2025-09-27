using System;
using System.IO;

namespace NonIPConfigTool;

class Program
{
    static int Main(string[] args)
    {
        Console.WriteLine("Non-IP Configuration Tool v1.0.0");
        Console.WriteLine("🔧 Non-IP File Delivery 設定ツール");
        Console.WriteLine();

        try
        {
            if (args.Length > 0)
            {
                switch (args[0].ToLower())
                {
                    case "--help":
                    case "-h":
                        ShowHelp();
                        return 0;
                    case "--create-config":
                        CreateConfiguration();
                        return 0;
                    case "--validate-config":
                        return ValidateConfiguration(args.Length > 1 ? args[1] : "config.ini");
                    default:
                        Console.WriteLine($"❌ 不明なオプション: {args[0]}");
                        ShowHelp();
                        return 1;
                }
            }

            // Start GUI configuration tool (simulated)
            Console.WriteLine("🎨 GUI設定ツールを起動中...");
            Console.WriteLine("注意: このバージョンではコンソール版設定ツールのみ利用可能です");
            Console.WriteLine();

            ShowConfigurationMenu();
            
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ エラーが発生しました: {ex.Message}");
            return 1;
        }
    }

    private static void ShowHelp()
    {
        Console.WriteLine("使用方法:");
        Console.WriteLine("  NonIPConfigTool.exe [オプション]");
        Console.WriteLine();
        Console.WriteLine("オプション:");
        Console.WriteLine("  --create-config        新しい設定ファイルを作成");
        Console.WriteLine("  --validate-config [path] 設定ファイルを検証");
        Console.WriteLine("  --help, -h             このヘルプを表示");
        Console.WriteLine();
        Console.WriteLine("引数なしで実行すると GUI設定ツールが起動します（未実装）");
    }

    private static void CreateConfiguration()
    {
        Console.WriteLine("📝 新しい設定ファイルを作成します");
        Console.WriteLine();
        
        var configPath = "config.ini";
        var securityPolicyPath = "security_policy.ini";

        // Create main config
        var mainConfig = @"[General]
Mode=ActiveStandby  # ActiveStandby | LoadBalancing
LogLevel=Warning    # Debug | Info | Warning | Error

[Network]
Interface=eth0
FrameSize=9000
Encryption=true
EtherType=0x88B5

[Security]
EnableVirusScan=true
ScanTimeout=5000    # milliseconds
QuarantinePath=C:\NonIP\Quarantine
PolicyFile=security_policy.ini

[Performance]
MaxMemoryMB=8192
BufferSize=65536
ThreadPool=auto

[Redundancy]
HeartbeatInterval=1000  # milliseconds
FailoverTimeout=5000
DataSyncMode=realtime";

        // Create security policy
        var securityPolicy = @"[FileExtensions]
Allowed=.txt,.pdf,.docx,.xlsx
Blocked=.exe,.bat,.cmd,.vbs,.scr

[FileSize]
MaxSizeMB=3072
MinSizeKB=1

[ContentType]
AllowedTypes=text/*,application/pdf,application/msword
BlockedPatterns=malware,virus,trojan";

        File.WriteAllText(configPath, mainConfig);
        File.WriteAllText(securityPolicyPath, securityPolicy);

        Console.WriteLine($"✅ 設定ファイルを作成しました: {configPath}");
        Console.WriteLine($"✅ セキュリティポリシーファイルを作成しました: {securityPolicyPath}");
    }

    private static int ValidateConfiguration(string configPath)
    {
        Console.WriteLine($"🔍 設定ファイルを検証中: {configPath}");
        
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"❌ 設定ファイルが見つかりません: {configPath}");
            return 1;
        }

        try
        {
            var config = File.ReadAllText(configPath);
            
            // Basic validation
            if (config.Contains("[General]") && 
                config.Contains("[Network]") && 
                config.Contains("[Security]") && 
                config.Contains("[Performance]") && 
                config.Contains("[Redundancy]"))
            {
                Console.WriteLine("✅ 設定ファイルは有効です");
                return 0;
            }
            else
            {
                Console.WriteLine("⚠️ 設定ファイルに必要なセクションが不足している可能性があります");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ 設定ファイルの読み込みエラー: {ex.Message}");
            return 1;
        }
    }

    private static void ShowConfigurationMenu()
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("🔧 設定メニュー:");
            Console.WriteLine("1. 新しい設定ファイルを作成");
            Console.WriteLine("2. 設定ファイルを検証");
            Console.WriteLine("3. 設定ファイルを表示");
            Console.WriteLine("4. ネットワークインターフェース一覧");
            Console.WriteLine("5. セキュリティポリシーを作成");
            Console.WriteLine("6. 終了");
            Console.Write("選択してください (1-6): ");

            var choice = Console.ReadLine();
            
            switch (choice)
            {
                case "1":
                    CreateConfiguration();
                    break;
                case "2":
                    Console.Write("検証する設定ファイルのパス (デフォルト: config.ini): ");
                    var path = Console.ReadLine();
                    if (string.IsNullOrEmpty(path)) path = "config.ini";
                    ValidateConfiguration(path);
                    break;
                case "3":
                    DisplayConfiguration();
                    break;
                case "4":
                    ListNetworkInterfaces();
                    break;
                case "5":
                    CreateSecurityPolicy();
                    break;
                case "6":
                    Console.WriteLine("設定ツールを終了します。");
                    return;
                default:
                    Console.WriteLine("無効な選択です。1-6を選択してください。");
                    break;
            }
        }
    }

    private static void DisplayConfiguration()
    {
        Console.Write("表示する設定ファイルのパス (デフォルト: config.ini): ");
        var path = Console.ReadLine();
        if (string.IsNullOrEmpty(path)) path = "config.ini";

        try
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"❌ 設定ファイルが見つかりません: {path}");
                return;
            }

            var content = File.ReadAllText(path);
            Console.WriteLine();
            Console.WriteLine($"📋 設定ファイル内容 ({path}):");
            Console.WriteLine(new string('-', 50));
            Console.WriteLine(content);
            Console.WriteLine(new string('-', 50));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ 設定ファイルの読み込みに失敗しました: {ex.Message}");
        }
    }

    private static void ListNetworkInterfaces()
    {
        Console.WriteLine("🔍 利用可能なネットワークインターフェース:");
        Console.WriteLine();

        try
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            int index = 1;

            foreach (var ni in interfaces)
            {
                var status = ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up ? "✅" : "❌";
                Console.WriteLine($"{index}. {status} {ni.Name}");
                Console.WriteLine($"   説明: {ni.Description}");
                Console.WriteLine($"   タイプ: {ni.NetworkInterfaceType}");
                Console.WriteLine($"   状態: {ni.OperationalStatus}");
                Console.WriteLine($"   スピード: {(ni.Speed > 0 ? $"{ni.Speed / 1_000_000} Mbps" : "不明")}");
                
                // Display MAC address if available
                try
                {
                    var physicalAddress = ni.GetPhysicalAddress();
                    if (physicalAddress != null && physicalAddress.ToString() != "")
                    {
                        Console.WriteLine($"   MACアドレス: {physicalAddress}");
                    }
                }
                catch { }

                Console.WriteLine();
                index++;
            }

            Console.WriteLine($"総数: {interfaces.Length} インターフェース");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ネットワークインターフェースの取得に失敗しました: {ex.Message}");
        }
    }

    private static void CreateSecurityPolicy()
    {
        Console.WriteLine("🔐 セキュリティポリシーファイルを作成します");
        
        var policyPath = "security_policy.ini";
        Console.Write($"ポリシーファイル名 (デフォルト: {policyPath}): ");
        var inputPath = Console.ReadLine();
        if (!string.IsNullOrEmpty(inputPath))
        {
            policyPath = inputPath;
        }

        var securityPolicy = @"[FileExtensions]
# 許可するファイル拡張子 (カンマ区切り)
Allowed=.txt,.pdf,.docx,.xlsx,.pptx,.csv,.xml,.json

# ブロックするファイル拡張子 (カンマ区切り)  
Blocked=.exe,.bat,.cmd,.vbs,.scr,.com,.pif,.js,.jar

[FileSize]
# 最大ファイルサイズ（MB）
MaxSizeMB=3072

# 最小ファイルサイズ（KB）
MinSizeKB=1

[ContentType]
# 許可するMIMEタイプ
AllowedTypes=text/*,application/pdf,application/msword,application/vnd.ms-excel,application/json

# ブロックするコンテンツパターン
BlacklistedPatterns=malware,virus,trojan,backdoor,keylogger

[ScanSettings]
# スキャンタイムアウト（ミリ秒）
TimeoutMs=30000

# 並行スキャン数
ConcurrentScans=4

# 隔離フォルダ
QuarantineFolder=C:\NonIP\Quarantine

[Encryption]
# 暗号化アルゴリズム
Algorithm=AES-256-GCM

# キー交換方式
KeyExchange=ECDH-P256

# 証明書検証
RequireCertificate=true";

        try
        {
            File.WriteAllText(policyPath, securityPolicy);
            Console.WriteLine($"✅ セキュリティポリシーファイルを作成しました: {policyPath}");
            Console.WriteLine("ポリシーファイルを編集して、環境に合わせてカスタマイズしてください。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ セキュリティポリシーファイルの作成に失敗しました: {ex.Message}");
        }
    }
}
