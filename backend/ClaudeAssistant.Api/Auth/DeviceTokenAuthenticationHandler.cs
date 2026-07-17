using System.Security.Claims;
using System.Text.Encodings.Web;
using ClaudeAssistant.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClaudeAssistant.Api.Auth;

public static class DeviceTokenDefaults
{
    public const string SchemeName = "DeviceToken";
}

public class DeviceTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppDbContext db
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var headerValue))
        {
            return AuthenticateResult.NoResult();
        }

        var header = headerValue.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            return AuthenticateResult.Fail("Empty token");
        }

        var tokenHash = TokenHasher.Hash(token);
        var device = await db.Devices
            .Include(d => d.User)
            .FirstOrDefaultAsync(d => d.TokenHash == tokenHash);

        if (device is null || device.RevokedAt is not null || device.User is null)
        {
            return AuthenticateResult.Fail("Invalid or revoked token");
        }

        device.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, device.UserId.ToString()),
            new Claim(ClaimTypes.Role, device.User.Role),
            new Claim("device_id", device.Id.ToString()),
        };
        var identity = new ClaimsIdentity(claims, DeviceTokenDefaults.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, DeviceTokenDefaults.SchemeName);

        return AuthenticateResult.Success(ticket);
    }
}
