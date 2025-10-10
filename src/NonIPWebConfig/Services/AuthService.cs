using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using NonIPWebConfig.Models;
using BCrypt.Net;

namespace NonIPWebConfig.Services;

/// <summary>
/// 認証サービス実装
/// 閉域環境向けのローカル認証機能
/// </summary>
public class AuthService : IAuthService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthService> _logger;
    private readonly string _userStoragePath;
    private readonly string _jwtSecret;
    private readonly int _jwtExpirationHours;
    private readonly int _maxFailedAttempts;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public AuthService(IConfiguration configuration, ILogger<AuthService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        
        // ユーザーストレージパス（暗号化JSONファイル）
        _userStoragePath = configuration["Auth:UserStoragePath"] 
            ?? Path.Combine(AppContext.BaseDirectory, "users.json");
        
        // JWT設定
        _jwtSecret = configuration["Auth:JwtSecret"] 
            ?? GenerateRandomSecret();
        _jwtExpirationHours = int.Parse(configuration["Auth:JwtExpirationHours"] ?? "8");
        _maxFailedAttempts = int.Parse(configuration["Auth:MaxFailedAttempts"] ?? "5");

        _logger.LogInformation("AuthService initialized. UserStoragePath: {Path}", _userStoragePath);
    }

    /// <summary>
    /// ユーザー認証
    /// </summary>
    public async Task<AuthResponse> AuthenticateAsync(string username, string password)
    {
        try
        {
            var user = await GetUserAsync(username);

            if (user == null)
            {
                _logger.LogWarning("Authentication failed: User not found - {Username}", username);
                return new AuthResponse
                {
                    Success = false,
                    Message = "ユーザー名またはパスワードが正しくありません"
                };
            }

            // アカウントロック確認
            if (user.IsLocked)
            {
                _logger.LogWarning("Authentication failed: Account locked - {Username}", username);
                return new AuthResponse
                {
                    Success = false,
                    Message = "アカウントがロックされています。管理者に連絡してください。"
                };
            }

            // パスワード検証
            bool isPasswordValid = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);

            if (!isPasswordValid)
            {
                // ログイン失敗回数を増加
                user.FailedLoginAttempts++;
                if (user.FailedLoginAttempts >= _maxFailedAttempts)
                {
                    user.IsLocked = true;
                    _logger.LogWarning("Account locked due to failed attempts - {Username}", username);
                }
                await SaveUserAsync(user);

                return new AuthResponse
                {
                    Success = false,
                    Message = "ユーザー名またはパスワードが正しくありません"
                };
            }

            // ログイン成功
            user.LastLoginAt = DateTime.UtcNow;
            user.FailedLoginAttempts = 0;
            await SaveUserAsync(user);

            var token = GenerateJwtToken(user);
            var expiresAt = DateTime.UtcNow.AddHours(_jwtExpirationHours);

            _logger.LogInformation("User authenticated successfully - {Username}", username);

            return new AuthResponse
            {
                Success = true,
                Token = token,
                Username = user.Username,
                Role = user.Role,
                MustChangePassword = user.MustChangePassword,
                ExpiresAt = expiresAt,
                Message = "ログインに成功しました"
            };
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Authentication failed: Invalid argument for user: {Username}", username);
            return new AuthResponse
            {
                Success = false,
                Message = "認証情報の形式が正しくありません"
            };
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Authentication failed: Invalid operation for user: {Username}", username);
            return new AuthResponse
            {
                Success = false,
                Message = "認証処理が正常に実行できませんでした"
            };
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Authentication failed: I/O error for user: {Username}", username);
            return new AuthResponse
            {
                Success = false,
                Message = "ユーザーデータの読み込みに失敗しました"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during authentication for user: {Username}", username);
            return new AuthResponse
            {
                Success = false,
                Message = "認証処理中に予期しないエラーが発生しました"
            };
        }
    }

    /// <summary>
    /// JWTトークン生成
    /// </summary>
    public string GenerateJwtToken(User user)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(_jwtSecret);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim("MustChangePassword", user.MustChangePassword.ToString())
            }),
            Expires = DateTime.UtcNow.AddHours(_jwtExpirationHours),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    /// <summary>
    /// トークン検証
    /// </summary>
    public bool ValidateToken(string token)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(_jwtSecret);

            tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = false,
                ValidateAudience = false,
                ClockSkew = TimeSpan.Zero
            }, out SecurityToken validatedToken);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// パスワード変更
    /// </summary>
    public async Task<bool> ChangePasswordAsync(string username, string currentPassword, string newPassword)
    {
        try
        {
            var user = await GetUserAsync(username);
            if (user == null)
            {
                _logger.LogWarning("Password change failed: User not found - {Username}", username);
                return false;
            }

            // 現在のパスワード検証
            if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
            {
                _logger.LogWarning("Password change failed: Invalid current password - {Username}", username);
                return false;
            }

            // 新しいパスワードをハッシュ化
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, BCrypt.Net.BCrypt.GenerateSalt());
            user.MustChangePassword = false;
            await SaveUserAsync(user);

            _logger.LogInformation("Password changed successfully for user: {Username}", username);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing password for user: {Username}", username);
            return false;
        }
    }

    /// <summary>
    /// ユーザー取得
    /// </summary>
    public async Task<User?> GetUserAsync(string username)
    {
        var storage = await LoadUserStorageAsync();
        return storage.Users.FirstOrDefault(u => 
            u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ユーザー保存
    /// </summary>
    public async Task SaveUserAsync(User user)
    {
        var storage = await LoadUserStorageAsync();
        
        var existingUser = storage.Users.FirstOrDefault(u => 
            u.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase));

        if (existingUser != null)
        {
            storage.Users.Remove(existingUser);
        }

        storage.Users.Add(user);
        storage.LastModified = DateTime.UtcNow;

        await SaveUserStorageAsync(storage);
    }

    /// <summary>
    /// ログイン失敗回数リセット
    /// </summary>
    public async Task ResetFailedLoginAttemptsAsync(string username)
    {
        var user = await GetUserAsync(username);
        if (user != null)
        {
            user.FailedLoginAttempts = 0;
            user.IsLocked = false;
            await SaveUserAsync(user);
            _logger.LogInformation("Failed login attempts reset for user: {Username}", username);
        }
    }

    /// <summary>
    /// 初期管理者アカウント作成
    /// </summary>
    public async Task InitializeDefaultAdminAsync()
    {
        var storage = await LoadUserStorageAsync();

        if (storage.Users.Count == 0)
        {
            var defaultPassword = GenerateDefaultPassword();
            var admin = new User
            {
                Username = "admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(defaultPassword, BCrypt.Net.BCrypt.GenerateSalt()),
                Role = "Admin",
                CreatedAt = DateTime.UtcNow,
                MustChangePassword = true
            };

            storage.Users.Add(admin);
            storage.LastModified = DateTime.UtcNow;
            await SaveUserStorageAsync(storage);

            _logger.LogWarning(
                "Default admin account created. Username: admin, Password: {Password} - PLEASE CHANGE IMMEDIATELY!",
                defaultPassword);

            // コンソールにも出力
            Console.WriteLine("=".PadRight(80, '='));
            Console.WriteLine("初期管理者アカウントが作成されました");
            Console.WriteLine($"ユーザー名: admin");
            Console.WriteLine($"パスワード: {defaultPassword}");
            Console.WriteLine("初回ログイン後、必ずパスワードを変更してください！");
            Console.WriteLine("=".PadRight(80, '='));
        }
    }

    // プライベートメソッド

    private async Task<UserStorage> LoadUserStorageAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(_userStoragePath))
            {
                return new UserStorage();
            }

            var json = await File.ReadAllTextAsync(_userStoragePath);
            return JsonSerializer.Deserialize<UserStorage>(json) ?? new UserStorage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading user storage from: {Path}", _userStoragePath);
            return new UserStorage();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task SaveUserStorageAsync(UserStorage storage)
    {
        await _fileLock.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(_userStoragePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(storage, options);
            await File.WriteAllTextAsync(_userStoragePath, json);

            _logger.LogDebug("User storage saved to: {Path}", _userStoragePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving user storage to: {Path}", _userStoragePath);
            throw;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static string GenerateRandomSecret()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, 64)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }

    private static string GenerateDefaultPassword()
    {
        // 安全なデフォルトパスワード生成（8文字以上、大文字、小文字、数字、特殊文字）
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string digits = "0123456789";
        const string special = "@$!%*?&";
        
        var random = new Random();
        var password = new List<char>
        {
            lower[random.Next(lower.Length)],
            upper[random.Next(upper.Length)],
            digits[random.Next(digits.Length)],
            special[random.Next(special.Length)]
        };

        // 残り4文字をランダムに追加
        const string all = lower + upper + digits + special;
        for (int i = 0; i < 8; i++)
        {
            password.Add(all[random.Next(all.Length)]);
        }

        // シャッフル
        return new string(password.OrderBy(_ => random.Next()).ToArray());
    }
}
