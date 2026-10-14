using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using TaskTrack.Service.Contracts;

namespace TaskTrack.API.Security;

public sealed record LoginResponse(string Token, string TokenType, DateTime ExpiresAt, AccountResponse Account);

public sealed class JwtTokenService(IConfiguration configuration)
{
    public SymmetricSecurityKey GetSigningKey()
    {
        var secret = configuration["JWT_SECRET"] ?? configuration["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("Set JWT_SECRET (at least 32 UTF-8 bytes) before issuing tokens.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    public (string Token, DateTime ExpiresAt) Issue(AccountResponse account)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddHours(24);
        var role = account.Role == 1 ? "Admin" : "Staff";
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, account.AccountId.ToString()),
            new Claim("accountId", account.AccountId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, account.Email),
            new Claim("role", role),
            new Claim("roleId", account.Role.ToString())
        };
        var token = new JwtSecurityToken(claims: claims, notBefore: now, expires: expires,
            signingCredentials: new SigningCredentials(GetSigningKey(), SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
