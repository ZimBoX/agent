using System.Security.Claims;
using ClaudeAssistant.Api.Data;
using ClaudeAssistant.Api.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAssistant.Api.Controllers;

[ApiController]
[Route("api/templates")]
[Authorize]
public class TemplatesController(AppDbContext db) : ControllerBase
{
    // Только шаблоны для должности вошедшего сотрудника — раньше отдавались все
    // шаблоны всем, независимо от должности (см. Этап 10 чеклиста). Шаблон теперь
    // может относиться сразу к нескольким должностям (many-to-many, см. Template.cs),
    // а должность с флагом IsAdmin (см. Role.cs) видит вообще все шаблоны —
    // по запросу пользователя ("хочу чтобы админ имел доступ ко всем шаблонам").
    [HttpGet]
    public async Task<ActionResult<List<TemplateListItem>>> List()
    {
        var role = User.FindFirstValue(ClaimTypes.Role);
        var isAdmin = role is not null && await db.Roles.AnyAsync(r => r.Name == role && r.IsAdmin);

        var templates = await db.Templates
            .Where(t => isAdmin || t.Roles.Any(r => r.Name == role))
            .OrderBy(t => t.Title)
            .Select(t => new TemplateListItem(t.Id, t.Title, t.AllowFileSearch))
            .ToListAsync();

        return Ok(templates);
    }
}
