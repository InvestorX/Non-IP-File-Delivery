using System.ComponentModel.DataAnnotations;

namespace NonIPWebConfig.Models;

/// <summary>
/// ユーザーモデル
/// </summary>
public class User
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "Admin"; // Admin, Operator, Viewer
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public bool MustChangePassword { get; set; } = false;
    public bool IsLocked { get; set; } = false;
    public int FailedLoginAttempts { get; set; } = 0;
}

/// <summary>
/// ログインリクエストモデル
/// </summary>
public class LoginRequest
{
    [Required(ErrorMessage = "ユーザー名は必須です")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "パスワードは必須です")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// 認証レスポンスモデル
/// </summary>
public class AuthResponse
{
    public bool Success { get; set; }
    public string? Token { get; set; }
    public string? Username { get; set; }
    public string? Role { get; set; }
    public string? Message { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// パスワード変更リクエストモデル
/// </summary>
public class ChangePasswordRequest
{
    [Required(ErrorMessage = "現在のパスワードは必須です")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "新しいパスワードは必須です")]
    [MinLength(8, ErrorMessage = "パスワードは8文字以上である必要があります")]
    [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[@$!%*?&])[A-Za-z\d@$!%*?&]",
        ErrorMessage = "パスワードは大文字、小文字、数字、特殊文字を含む必要があります")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "パスワード確認は必須です")]
    [Compare(nameof(NewPassword), ErrorMessage = "パスワードが一致しません")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>
/// ユーザーストレージモデル（JSON保存用）
/// </summary>
public class UserStorage
{
    public List<User> Users { get; set; } = new();
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}
