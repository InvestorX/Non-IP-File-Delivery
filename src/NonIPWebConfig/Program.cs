using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using NonIPFileDelivery.Services;
using NonIPWebConfig.Models;
using NonIPWebConfig.Services;

var builder = WebApplication.CreateBuilder(args);

// JWT認証設定
var jwtSecret = builder.Configuration["Auth:JwtSecret"];
if (string.IsNullOrEmpty(jwtSecret))
{
    // ランダムなシークレットを生成（初回起動時）
    jwtSecret = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + 
                Convert.ToBase64String(Guid.NewGuid().ToByteArray());
    Console.WriteLine($"JWT Secret generated: {jwtSecret}");
    Console.WriteLine("Please add this to appsettings.json under Auth:JwtSecret");
}

var key = Encoding.UTF8.GetBytes(jwtSecret);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = builder.Environment.IsDevelopment() ? false : true; // 開発環境ではfalse、本番ではtrue
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = false,
        ValidateAudience = false,
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddAuthorization();

// Add services to the container
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("https://localhost:5001", "http://localhost:5000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// サービス登録
builder.Services.AddSingleton<NonIPWebConfig.Services.ConfigurationService>();
builder.Services.AddSingleton<NonIPWebConfig.Services.ConfigValidationService>();
builder.Services.AddSingleton<IAuthService, AuthService>();

var app = builder.Build();

// 初期管理者アカウントの作成
var authService = app.Services.GetRequiredService<IAuthService>();
await authService.InitializeDefaultAdminAsync();

// 設定ファイルのパスを定義
var configPath = Path.Combine(AppContext.BaseDirectory, "config.ini");

// Configure the HTTP request pipeline
app.UseCors();

// HTTPSリダイレクト（本番環境では有効化推奨）
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Serve static files (HTML, CSS, JS)
app.UseStaticFiles();

// 認証・認可ミドルウェア
app.UseAuthentication();
app.UseAuthorization();

// ルートへのアクセスはログインページにリダイレクト（未認証の場合）
app.MapGet("/", () => Results.Redirect("/login.html"));

// 旧ルート（削除予定）
app.MapGet("/old", () => Results.Content("""
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
                    <input type="number" id="scanTimeout" name="scanTimeout" value="5000" min="0" max="60000">
                </div>
                <div class="form-group">
                    <label for="quarantinePath">隔離パス:</label>
                    <input type="text" id="quarantinePath" name="quarantinePath" value="C:\\NonIP\\Quarantine">
                </div>
            </div>
            
            <div class="config-section">
                <h3>⚡ パフォーマンス設定</h3>
                <div class="form-group">
                    <label for="maxMemoryMB">最大メモリ (MB):</label>
                    <input type="number" id="maxMemoryMB" name="maxMemoryMB" value="8192" min="1" max="65536">
                </div>
                <div class="form-group">
                    <label for="bufferSize">バッファサイズ (バイト):</label>
                    <input type="number" id="bufferSize" name="bufferSize" value="65536" min="1" max="1048576">
                </div>
                <div class="form-group">
                    <label for="threadPool">スレッドプール:</label>
                    <select id="threadPool" name="threadPool">
                        <option value="auto" selected>自動</option>
                        <option value="manual">手動</option>
                    </select>
                </div>
            </div>
            
            <div class="config-section">
                <h3>� 冗長性設定</h3>
                <div class="form-group">
                    <label for="heartbeatInterval">ハートビート間隔 (ms):</label>
                    <input type="number" id="heartbeatInterval" name="heartbeatInterval" value="1000" min="100" max="10000">
                </div>
                <div class="form-group">
                    <label for="failoverTimeout">フェイルオーバータイムアウト (ms):</label>
                    <input type="number" id="failoverTimeout" name="failoverTimeout" value="5000" min="100" max="60000">
                </div>
                <div class="form-group">
                    <label for="dataSyncMode">データ同期モード:</label>
                    <select id="dataSyncMode" name="dataSyncMode">
                        <option value="realtime" selected>リアルタイム</option>
                        <option value="batch">バッチ</option>
                        <option value="manual">手動</option>
                    </select>
                </div>
                <div class="form-group">
                    <label for="primaryNode">プライマリノード (オプション):</label>
                    <input type="text" id="primaryNode" name="primaryNode" placeholder="例: 192.168.1.10">
                </div>
                <div class="form-group">
                    <label for="standbyNode">スタンバイノード (オプション):</label>
                    <input type="text" id="standbyNode" name="standbyNode" placeholder="例: 192.168.1.11">
                </div>
                <div class="form-group">
                    <label for="virtualIP">仮想IP (オプション):</label>
                    <input type="text" id="virtualIP" name="virtualIP" placeholder="例: 192.168.1.100">
                </div>
                <div class="form-group">
                    <label for="loadBalancingAlgorithm">ロードバランシングアルゴリズム:</label>
                    <select id="loadBalancingAlgorithm" name="loadBalancingAlgorithm">
                        <option value="RoundRobin" selected>ラウンドロビン</option>
                        <option value="WeightedRoundRobin">重み付きラウンドロビン</option>
                        <option value="LeastConnections">最小接続数</option>
                        <option value="Random">ランダム</option>
                    </select>
                </div>
            </div>
            
            <button type="submit">�💾 設定を保存</button>
            <button type="button" onclick="loadConfig()">📁 設定を読み込み</button>
            <button type="button" onclick="resetToDefaults()" style="background-color: #95a5a6;">🔄 デフォルトに戻す</button>
        </form>
    </div>
    
    <script>
        document.getElementById('configForm').addEventListener('submit', function(e) {
            e.preventDefault();
            saveConfig();
        });
        
        function validateForm() {
            const errors = [];
            
            // フレームサイズ検証
            const frameSize = parseInt(document.getElementById('frameSize').value);
            if (frameSize < 1 || frameSize > 9000) {
                errors.push('フレームサイズは1〜9000の範囲で指定してください');
            }
            
            // スキャンタイムアウト検証
            const scanTimeout = parseInt(document.getElementById('scanTimeout').value);
            if (scanTimeout < 0 || scanTimeout > 60000) {
                errors.push('スキャンタイムアウトは0〜60000ミリ秒の範囲で指定してください');
            }
            
            // ハートビート間隔検証
            const heartbeat = parseInt(document.getElementById('heartbeatInterval').value);
            if (heartbeat < 100 || heartbeat > 10000) {
                errors.push('ハートビート間隔は100〜10000ミリ秒の範囲で指定してください');
            }
            
            // フェイルオーバータイムアウト検証
            const failover = parseInt(document.getElementById('failoverTimeout').value);
            if (failover < heartbeat) {
                errors.push('フェイルオーバータイムアウトはハートビート間隔より大きい値を指定してください');
            }
            
            // メモリ検証
            const maxMemory = parseInt(document.getElementById('maxMemoryMB').value);
            if (maxMemory < 1 || maxMemory > 65536) {
                errors.push('最大メモリは1〜65536MBの範囲で指定してください');
            }
            
            return errors;
        }
        
        function saveConfig() {
            // クライアント側検証
            const validationErrors = validateForm();
            if (validationErrors.length > 0) {
                showStatus('入力エラー:\\n' + validationErrors.join('\\n'), 'error');
                return;
            }
            
            const formData = new FormData(document.getElementById('configForm'));
            const config = {};
            
            for (let [key, value] of formData.entries()) {
                // 空の値は送信しない（オプション項目）
                if (value.trim() !== '') {
                    config[key] = value;
                }
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
                if (data.success) {
                    showStatus('✅ ' + data.message, 'success');
                } else {
                    let errorMsg = data.message;
                    if (data.errors && data.errors.length > 0) {
                        errorMsg += ':\\n' + data.errors.join('\\n');
                    }
                    showStatus('❌ ' + errorMsg, 'error');
                }
            })
            .catch(error => {
                showStatus('❌ 設定の保存中にエラーが発生しました: ' + error.message, 'error');
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
        
        function resetToDefaults() {
            if (!confirm('設定をデフォルト値に戻しますか？')) {
                return;
            }
            
            document.getElementById('mode').value = 'ActiveStandby';
            document.getElementById('logLevel').value = 'Warning';
            document.getElementById('interface').value = 'eth0';
            document.getElementById('frameSize').value = '9000';
            document.getElementById('encryption').value = 'true';
            document.getElementById('enableVirusScan').value = 'true';
            document.getElementById('scanTimeout').value = '5000';
            document.getElementById('quarantinePath').value = 'C:\\\\NonIP\\\\Quarantine';
            document.getElementById('maxMemoryMB').value = '8192';
            document.getElementById('bufferSize').value = '65536';
            document.getElementById('threadPool').value = 'auto';
            document.getElementById('heartbeatInterval').value = '1000';
            document.getElementById('failoverTimeout').value = '5000';
            document.getElementById('dataSyncMode').value = 'realtime';
            document.getElementById('primaryNode').value = '';
            document.getElementById('standbyNode').value = '';
            document.getElementById('virtualIP').value = '';
            document.getElementById('loadBalancingAlgorithm').value = 'RoundRobin';
            
            showStatus('✅ デフォルト値に戻しました', 'success');
        }
        
        function showStatus(message, type) {
            const statusDiv = document.getElementById('status');
            // 改行をHTMLの<br>に変換
            const formattedMessage = message.replace(/\\n/g, '<br>');
            statusDiv.innerHTML = `<div class="status ${type}">${formattedMessage}</div>`;
            setTimeout(() => {
                statusDiv.innerHTML = '';
            }, 8000);
        }
        
        // Load initial config
        loadConfig();
    </script>
</body>
</html>
""", "text/html"));

app.MapGet("/api/config", (IConfigurationService configService) => 
{
    try
    {
        // 設定ファイルが存在しない場合はデフォルト設定を作成
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"⚠️ 設定ファイルが見つかりません。デフォルト設定を作成します: {configPath}");
            configService.CreateDefaultConfiguration(configPath);
        }
        
        // 設定ファイルを読み込み
        var config = configService.LoadConfiguration(configPath);
        var webConfig = WebConfigDto.FromConfiguration(config);
        
        Console.WriteLine($"✅ 設定を読み込みました: {configPath}");
        return Results.Json(webConfig);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ 設定の読み込みに失敗: {ex.Message}");
        // エラー時はデフォルト設定を返す
        return Results.Json(new WebConfigDto());
    }
}).RequireAuthorization();

app.MapPost("/api/config", (WebConfigDto webConfig, IConfigurationService configService, NonIPWebConfig.Services.ConfigValidationService validationService) =>
{
    try
    {
        // WebConfigDtoをConfigurationモデルに変換
        var config = webConfig.ToConfiguration();
        
        // 詳細な設定検証
        var (isValid, errors) = validationService.ValidateDetailed(config);
        if (!isValid)
        {
            var errorMessage = string.Join("\n", errors);
            Console.WriteLine($"❌ 設定の検証に失敗しました:");
            foreach (var error in errors)
            {
                Console.WriteLine($"   - {error}");
            }
            
            return Results.Json(new 
            { 
                success = false, 
                message = "設定の検証に失敗しました",
                errors = errors
            });
        }
        
        // 設定ファイルに保存
        configService.SaveConfiguration(config, configPath);
        
        Console.WriteLine($"✅ 設定を保存しました: {configPath}");
        Console.WriteLine($"   - モード: {config.General?.Mode}");
        Console.WriteLine($"   - インターフェース: {config.Network?.Interface}");
        Console.WriteLine($"   - 暗号化: {config.Network?.Encryption}");
        
        return Results.Json(new 
        { 
            success = true, 
            message = "設定が正常に保存されました",
            savedPath = configPath
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ 設定の保存に失敗: {ex.Message}");
        Console.WriteLine($"   スタックトレース: {ex.StackTrace}");
        return Results.Json(new 
        { 
            success = false, 
            message = $"設定の保存に失敗しました: {ex.Message}",
            errors = new[] { ex.Message }
        });
    }
}).RequireAuthorization();

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
}).RequireAuthorization();

// Controllers (AuthController) のマッピング
app.MapControllers();

Console.WriteLine("🌐 Non-IP Web Configuration Tool が起動しました");
Console.WriteLine("📱 HTTP: http://localhost:5000");
Console.WriteLine("📱 HTTPS: https://localhost:5001");
Console.WriteLine("");
Console.WriteLine("⚠️  初回ログイン情報:");
Console.WriteLine("   ユーザー名: admin");
Console.WriteLine("   パスワード: Admin@123");
Console.WriteLine("   (初回ログイン後、必ずパスワードを変更してください)");
Console.WriteLine("");

app.Run();
