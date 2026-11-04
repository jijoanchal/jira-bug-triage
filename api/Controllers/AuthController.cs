using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Triage.Api.Data;
using Triage.Api.Services;

namespace Triage.Api.Controllers;

public record LoginRequest(string Email, string Password);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public static class UserDto
{
    public static object Of(AppUser u) => new { u.Id, u.Email, u.DisplayName, u.Role, u.IsActive, u.MustChangePassword, u.CreatedAt, u.LastLoginAt };
}

[ApiController, Route("api/auth")]
public class AuthController(AppDb db, TokenService tokens) : ControllerBase
{
    private readonly PasswordHasher<AppUser> _hasher = new();

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest req)
    {
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        var u = await db.Users.FirstOrDefaultAsync(x => x.Email == email);
        if (u == null || !u.IsActive ||
            _hasher.VerifyHashedPassword(u, u.PasswordHash, req.Password ?? "") == PasswordVerificationResult.Failed)
            return Unauthorized(new { message = "Invalid email or password." });

        u.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { token = tokens.Create(u), user = UserDto.Of(u) });
    }

    [Authorize, HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var u = await db.Users.FindAsync(CurrentUser.Id(User));
        return u == null ? Unauthorized() : Ok(UserDto.Of(u));
    }

    [Authorize, HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest req)
    {
        var u = await db.Users.FindAsync(CurrentUser.Id(User));
        if (u == null) return Unauthorized();
        if (_hasher.VerifyHashedPassword(u, u.PasswordHash, req.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
            return BadRequest(new { message = "Current password is incorrect." });
        if (PasswordPolicy.Check(req.NewPassword) is { } problem) return BadRequest(new { message = problem });
        u.PasswordHash = _hasher.HashPassword(u, req.NewPassword);
        u.MustChangePassword = false;
        await db.SaveChangesAsync();
        return Ok(UserDto.Of(u));
    }
}

public static class CurrentUser
{
    public static Guid Id(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static string Name(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.Name) ?? "unknown";
}

public static class PasswordPolicy
{
    public static string? Check(string? p) =>
        p is null || p.Length < 8 ? "Password must be at least 8 characters."
        : !p.Any(char.IsLetter) || !p.Any(char.IsDigit) ? "Password must contain letters and digits."
        : null;
}
