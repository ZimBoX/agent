using System.Security.Claims;
using ClaudeAssistant.Api.Data;
using ClaudeAssistant.Api.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAssistant.Api.Controllers;

// Список моделей для селектора в клиенте. Управляется в админке (см.
// AdminController /api/admin/models). Отдаётся ТОЛЬКО админу (роль с IsAdmin) —
// обычным пользователям селектор не нужен, у них пустой список.
[ApiController]
[Route("api/models")]
[Authorize]
public class ModelsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<AiModelResponse>>> List()
    {
        var role = User.FindFirstValue(ClaimTypes.Role);
        var isAdmin = role is not null && await db.Roles.AnyAsync(r => r.Name == role && r.IsAdmin);
        if (!isAdmin)
        {
            return Ok(new List<AiModelResponse>());
        }

        var models = await db.AiModels
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.DisplayName)
            .Select(m => new AiModelResponse(m.Id, m.ModelId, m.DisplayName))
            .ToListAsync();
        return Ok(models);
    }
}
