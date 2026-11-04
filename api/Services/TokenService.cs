using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Triage.Api.Data;

namespace Triage.Api.Services;

public class TokenService
{
    public SymmetricSecurityKey Key { get; }

    public TokenService(IConfiguration cfg)
    {
        // JWT_SECRET wins; otherwise a random key is generated once and persisted so logins survive restarts.
        var secret = Environment.GetEnvironmentVariable("JWT_SECRET");
        if (string.IsNullOrWhiteSpace(secret))
        {
            var dir = Path.GetFullPath(cfg["DataDir"] ?? "App_Data");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "jwt.key");
            if (!File.Exists(file)) File.WriteAllText(file, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
            secret = File.ReadAllText(file).Trim();
        }
        Key = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(secret.PadRight(32, '!')));
    }

    public string Create(AppUser u)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, u.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new Claim(ClaimTypes.Name, u.DisplayName),
            new Claim(ClaimTypes.Email, u.Email),
            new Claim(ClaimTypes.Role, u.Role)
        };
        var token = new JwtSecurityToken("triage", "triage", claims,
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
