using ClaudeAssistant.Api.Auth;
using ClaudeAssistant.Api.Data;
using ClaudeAssistant.Api.Dtos;
using ClaudeAssistant.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAssistant.Api.Controllers;

[ApiController]
[Route("api/enrollment")]
public class EnrollmentController(AppDbContext db) : ControllerBase
{
    [HttpPost("bind")]
    public async Task<ActionResult<BindDeviceResponse>> Bind(BindDeviceRequest request)
    {
        var code = await db.EnrollmentCodes
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.Code == request.Code);

        if (code is null || code.User is null)
        {
            return BadRequest("Неверный код привязки");
        }

        if (code.UsedAt is not null)
        {
            return BadRequest("Код привязки уже использован");
        }

        if (code.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return BadRequest("Код привязки истёк");
        }

        // 1 токен = 1 ПК: новая привязка аннулирует предыдущие активные устройства пользователя
        var activeDevices = await db.Devices
            .Where(d => d.UserId == code.UserId && d.RevokedAt == null)
            .ToListAsync();
        foreach (var old in activeDevices)
        {
            old.RevokedAt = DateTimeOffset.UtcNow;
        }

        var device = new Device
        {
            Id = Guid.NewGuid(),
            UserId = code.UserId,
            TokenHash = TokenHasher.Hash(request.Token),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Devices.Add(device);

        code.UsedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();

        return Ok(new BindDeviceResponse(device.Id, code.User.FullName, code.User.Role));
    }
}
