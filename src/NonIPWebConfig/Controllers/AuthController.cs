using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NonIPWebConfig.Models;
using NonIPWebConfig.Services;
using System.Security.Claims;

namespace NonIPWebConfig.Controllers;

/// <summary>
/// 認証コントローラー
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService authService, ILogger<AuthController> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    /// <summary>
    /// ログイン
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new AuthResponse
            {
                Success = false,
                Message = "入力内容に誤りがあります"
            });
        }

        var response = await _authService.AuthenticateAsync(request.Username, request.Password);

        if (!response.Success)
        {
            return Unauthorized(response);
        }

        _logger.LogInformation("User logged in: {Username}", request.Username);

        return Ok(response);
    }

    /// <summary>
    /// ログアウト
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    public IActionResult Logout()
    {
        var username = User.FindFirst(ClaimTypes.Name)?.Value;
        _logger.LogInformation("User logged out: {Username}", username);

        return Ok(new { success = true, message = "ログアウトしました" });
    }

    /// <summary>
    /// パスワード変更
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new
            {
                success = false,
                message = "入力内容に誤りがあります",
                errors = ModelState.Values.SelectMany(v => v.Errors.Select(e => e.ErrorMessage))
            });
        }

        var username = User.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized(new { success = false, message = "認証情報が無効です" });
        }

        var success = await _authService.ChangePasswordAsync(
            username,
            request.CurrentPassword,
            request.NewPassword);

        if (!success)
        {
            return BadRequest(new
            {
                success = false,
                message = "現在のパスワードが正しくありません"
            });
        }

        _logger.LogInformation("Password changed for user: {Username}", username);

        return Ok(new
        {
            success = true,
            message = "パスワードを変更しました"
        });
    }

    /// <summary>
    /// 現在のユーザー情報取得
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetCurrentUser()
    {
        var username = User.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized(new { success = false, message = "認証情報が無効です" });
        }

        var user = await _authService.GetUserAsync(username);
        if (user == null)
        {
            return NotFound(new { success = false, message = "ユーザーが見つかりません" });
        }

        return Ok(new
        {
            success = true,
            user = new
            {
                username = user.Username,
                role = user.Role,
                lastLoginAt = user.LastLoginAt,
                mustChangePassword = user.MustChangePassword
            }
        });
    }

    /// <summary>
    /// トークン検証
    /// </summary>
    [HttpPost("validate")]
    [AllowAnonymous]
    public IActionResult ValidateToken([FromBody] string token)
    {
        var isValid = _authService.ValidateToken(token);
        
        return Ok(new
        {
            success = isValid,
            message = isValid ? "トークンは有効です" : "トークンが無効です"
        });
    }
}
