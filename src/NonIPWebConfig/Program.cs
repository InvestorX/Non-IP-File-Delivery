using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
app.UseCors();

// Serve static files (HTML, CSS, JS)
app.UseStaticFiles();

// API endpoints
app.MapGet("/", () => Results.Content("""
<!DOCTYPE html>
<html lang="ja">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Non-IP File Delivery Web Configuration</title>
    <style>
        body { 
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; 
            margin: 0; 
            padding: 20px; 
            background-color: #f5f5f5; 
        }
        .container { 
            max-width: 1200px; 
            margin: 0 auto; 
            background: white; 
            padding: 30px; 
            border-radius: 10px; 
            box-shadow: 0 2px 10px rgba(0,0,0,0.1); 
        }
        h1 { 
            color: #2c3e50; 
            text-align: center; 
            margin-bottom: 30px; 
        }
        .config-section { 
            margin: 20px 0; 
            border: 1px solid #ddd; 
            border-radius: 5px; 
            padding: 15px; 
        }
        .config-section h3 { 
            margin-top: 0; 
            color: #34495e; 
        }
        .form-group { 
            margin-bottom: 15px; 
        }
        label { 
            display: block; 
            margin-bottom: 5px; 
            font-weight: bold; 
        }
        input, select, textarea { 
            width: 100%; 
            padding: 8px; 
            border: 1px solid #ddd; 
            border-radius: 4px; 
            box-sizing: border-box; 
        }
        button { 
            background-color: #3498db; 
            color: white; 
            padding: 10px 20px; 
            border: none; 
            border-radius: 4px; 
            cursor: pointer; 
            font-size: 16px; 
        }
        button:hover { 
            background-color: #2980b9; 
        }
        .status { 
            padding: 10px; 
            margin: 10px 0; 
            border-radius: 4px; 
        }
        .success { 
            background-color: #d4edda; 
            color: #155724; 
            border: 1px solid #c3e6cb; 
        }
        .error { 
            background-color: #f8d7da; 
            color: #721c24; 
            border: 1px solid #f5c6cb; 
        }
    </style>
</head>
<body>
    <div class="container">
        <h1>🛡️ Non-IP File Delivery Web Configuration</h1>
        <p style="text-align: center; color: #666;">ハッカー・クラッカー・ランサムウェア対策のためのRaw Ethernet非IPファイル転送システム</p>
        
        <div id="status"></div>
        
        <form id="configForm">
            <div class="config-section">
                <h3>🔧 一般設定</h3>
                <div class="form-group">
                    <label for="mode">動作モード:</label>
                    <select id="mode" name="mode">
                        <option value="ActiveStandby">アクティブ-スタンバイ</option>
                        <option value="LoadBalancing">ロードバランシング</option>
                    </select>
                </div>
                <div class="form-group">
                    <label for="logLevel">ログレベル:</label>
                    <select id="logLevel" name="logLevel">
                        <option value="Debug">Debug</option>
                        <option value="Info">Info</option>
                        <option value="Warning" selected>Warning</option>
                        <option value="Error">Error</option>
                    </select>
                </div>
            </div>
            
            <div class="config-section">
                <h3>🌐 ネットワーク設定</h3>
                <div class="form-group">
                    <label for="interface">ネットワークインターフェース:</label>
                    <input type="text" id="interface" name="interface" value="eth0">
                </div>
                <div class="form-group">
                    <label for="frameSize">フレームサイズ:</label>
                    <input type="number" id="frameSize" name="frameSize" value="9000">
                </div>
                <div class="form-group">
                    <label for="encryption">暗号化有効:</label>
                    <select id="encryption" name="encryption">
                        <option value="true" selected>有効</option>
                        <option value="false">無効</option>
                    </select>
                </div>
            </div>
            
            <div class="config-section">
                <h3>🔐 セキュリティ設定</h3>
                <div class="form-group">
                    <label for="enableVirusScan">ウイルススキャン有効:</label>
                    <select id="enableVirusScan" name="enableVirusScan">
                        <option value="true" selected>有効</option>
                        <option value="false">無効</option>
                    </select>
                </div>
                <div class="form-group">
                    <label for="scanTimeout">スキャンタイムアウト (ms):</label>
                    <input type="number" id="scanTimeout" name="scanTimeout" value="5000">
                </div>
                <div class="form-group">
                    <label for="quarantinePath">隔離パス:</label>
                    <input type="text" id="quarantinePath" name="quarantinePath" value="C:\\NonIP\\Quarantine">
                </div>
            </div>
            
            <button type="submit">💾 設定を保存</button>
            <button type="button" onclick="loadConfig()">📁 設定を読み込み</button>
        </form>
    </div>
    
    <script>
        document.getElementById('configForm').addEventListener('submit', function(e) {
            e.preventDefault();
            saveConfig();
        });
        
        function saveConfig() {
            const formData = new FormData(document.getElementById('configForm'));
            const config = {};
            
            for (let [key, value] of formData.entries()) {
                config[key] = value;
            }
            
            fetch('/api/config', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify(config)
            })
            .then(response => response.json())
            .then(data => {
                showStatus(data.message, data.success ? 'success' : 'error');
            })
            .catch(error => {
                showStatus('設定の保存中にエラーが発生しました: ' + error.message, 'error');
            });
        }
        
        function loadConfig() {
            fetch('/api/config')
            .then(response => response.json())
            .then(config => {
                Object.keys(config).forEach(key => {
                    const element = document.getElementById(key);
                    if (element) {
                        element.value = config[key];
                    }
                });
                showStatus('設定を読み込みました', 'success');
            })
            .catch(error => {
                showStatus('設定の読み込み中にエラーが発生しました: ' + error.message, 'error');
            });
        }
        
        function showStatus(message, type) {
            const statusDiv = document.getElementById('status');
            statusDiv.innerHTML = `<div class="status ${type}">${message}</div>`;
            setTimeout(() => {
                statusDiv.innerHTML = '';
            }, 5000);
        }
        
        // Load initial config
        loadConfig();
    </script>
</body>
</html>
""", "text/html"));

app.MapGet("/api/config", () => 
{
    // Return current configuration
    var defaultConfig = new
    {
        mode = "ActiveStandby",
        logLevel = "Warning",
        @interface = "eth0",
        frameSize = "9000",
        encryption = "true",
        enableVirusScan = "true",
        scanTimeout = "5000",
        quarantinePath = "C:\\NonIP\\Quarantine"
    };
    
    return Results.Json(defaultConfig);
});

app.MapPost("/api/config", (JsonElement config) =>
{
    try
    {
        // Here you would save the configuration to a file
        // For now, just simulate success
        Console.WriteLine($"📝 Configuration saved: {config}");
        
        return Results.Json(new { success = true, message = "設定が正常に保存されました" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, message = $"設定の保存に失敗しました: {ex.Message}" });
    }
});

app.MapGet("/api/status", () =>
{
    return Results.Json(new
    {
        status = "running",
        version = "1.0.0",
        uptime = "00:05:23",
        throughput = "1.2 Gbps",
        connections = 42,
        memory_usage = "2.1 GB"
    });
});

Console.WriteLine("🌐 Non-IP Web Configuration Tool が起動しました");
Console.WriteLine("📱 ブラウザで http://localhost:8080 を開いてください");

app.Run("http://localhost:8080");
