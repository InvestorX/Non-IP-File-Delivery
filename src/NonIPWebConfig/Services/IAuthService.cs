using NonIPWebConfig.Models;

namespace NonIPWebConfig.Services;

/// <summary>
/// 認証サービスインターフェース
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// ユーザー認証を実行
    /// </summary>
    Task<AuthResponse> AuthenticateAsync(string username, string password);

    /// <summary>
    /// JWTトークンを生成
    /// </summary>
    string GenerateJwtToken(User user);

    /// <summary>
    /// トークンを検証
    /// </summary>
    bool ValidateToken(string token);

    /// <summary>
    /// パスワードを変更
    /// </summary>
    Task<bool> ChangePasswordAsync(string username, string currentPassword, string newPassword);

    /// <summary>
    /// ユーザーを取得
    /// </summary>
    Task<User?> GetUserAsync(string username);

    /// <summary>
    /// 初期管理者アカウントを作成
    /// </summary>
    Task InitializeDefaultAdminAsync();

    /// <summary>
    /// ユーザーを保存
    /// </summary>
    Task SaveUserAsync(User user);

    /// <summary>
    /// ログイン失敗回数をリセット
    /// </summary>
    Task ResetFailedLoginAttemptsAsync(string username);
}
