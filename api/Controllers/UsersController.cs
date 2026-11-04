using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Triage.Api.Data;

namespace Triage.Api.Controllers;

public record CreateUserRequest(string Email, string DisplayName, string Password, string Role);
public record UpdateUserRequest(string DisplayName, string Role, bool IsActive, string? NewPassword);

[ApiController, Route("api/users"), Authorize(Roles = Roles.Admin)]
public class UsersController(AppDb db) : ControllerBase
{
    private readonly PasswordHasher<AppUser> _hasher = new();

    [HttpGet]
    public async Task<IActionResult> List() =>
        Ok((await db.Users.OrderBy(u => u.Email).ToListAsync()).Select(UserDto.Of));

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest r)
    {
        var email = (r.Email ?? "").Trim().ToLowerInvariant();
        if (!email.Contains('@')) return BadRequest(new { message = "A valid email is required." });
        if (string.IsNullOrWhiteSpace(r.DisplayName)) return BadRequest(new { message = "Display name is required." });
        if (r.Role is not (Roles.Admin or Roles.User)) return BadRequest(new { message = "Role must be Admin or User." });
        if (PasswordPolicy.Check(r.Password) is { } p) return BadRequest(new { message = p });
        if (await db.Users.AnyAsync(u => u.Email == email)) return Conflict(new { message = "A user with this email already exists." });

        var u = new AppUser { Email = email, DisplayName = r.DisplayName.Trim(), Role = r.Role, MustChangePassword = true };
        u.PasswordHash = _hasher.HashPassword(u, r.Password);
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return Ok(UserDto.Of(u));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateUserRequest r)
    {
        var u = await db.Users.FindAsync(id);
        if (u == null) return NotFound();
        if (r.Role is not (Roles.Admin or Roles.User)) return BadRequest(new { message = "Role must be Admin or User." });
        if (string.IsNullOrWhiteSpace(r.DisplayName)) return BadRequest(new { message = "Display name is required." });

        var losingAdmin = u.Role == Roles.Admin && u.IsActive && (r.Role != Roles.Admin || !r.IsActive);
        if (losingAdmin && await LastAdmin(u.Id)) return BadRequest(new { message = "At least one active admin must remain." });
        if (id == CurrentUser.Id(User) && !r.IsActive) return BadRequest(new { message = "You cannot deactivate your own account." });

        u.DisplayName = r.DisplayName.Trim(); u.Role = r.Role; u.IsActive = r.IsActive;
        if (!string.IsNullOrEmpty(r.NewPassword))
        {
            if (PasswordPolicy.Check(r.NewPassword) is { } p) return BadRequest(new { message = p });
            u.PasswordHash = _hasher.HashPassword(u, r.NewPassword);
            u.MustChangePassword = true;
        }
        await db.SaveChangesAsync();
        return Ok(UserDto.Of(u));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (id == CurrentUser.Id(User)) return BadRequest(new { message = "You cannot delete your own account." });
        var u = await db.Users.FindAsync(id);
        if (u == null) return NotFound();
        if (u.Role == Roles.Admin && u.IsActive && await LastAdmin(u.Id)) return BadRequest(new { message = "At least one active admin must remain." });
        db.Users.Remove(u);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private Task<bool> LastAdmin(Guid except) =>
        db.Users.Where(x => x.Role == Roles.Admin && x.IsActive && x.Id != except).AnyAsync().ContinueWith(t => !t.Result);
}
