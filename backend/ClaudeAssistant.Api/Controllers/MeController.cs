using System.Security.Claims;
using ClaudeAssistant.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAssistant.Api.Controllers;

[ApiController]
[Route("api/me")]
[Authorize]
public class MeController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var userIdText = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var role = User.FindFirstValue(ClaimTypes.Role);
        var deviceId = User.FindFirstValue("device_id");

        string? fullName = null;
        if (Guid.TryParse(userIdText, out var userId))
        {
            fullName = await db.Users.Where(u => u.Id == userId).Select(u => u.FullName).FirstOrDefaultAsync();
        }

        return Ok(new { userId = userIdText, fullName, role, deviceId });
    }

    // Логаут по инициативе самого пользователя (кнопка в клиенте), а не отзыв
    // админом — та же db-операция (RevokedAt), но без похода в AdminService и без
    // прав администратора: устройство отзывает само себя по своему же токену.
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var deviceIdText = User.FindFirstValue("device_id");
        if (!Guid.TryParse(deviceIdText, out var deviceId))
        {
            return Unauthorized();
        }

        var device = await db.Devices.FindAsync(deviceId);
        if (device is null)
        {
            return NotFound();
        }

        device.RevokedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
