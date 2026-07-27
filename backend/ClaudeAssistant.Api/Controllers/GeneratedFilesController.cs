using System.Security.Claims;
using ClaudeAssistant.Api.Data;
using ClaudeAssistant.Api.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAssistant.Api.Controllers;

// Отдаёт файлы, сгенерированные моделью (create_file). Файлы хранятся в БД с
// привязкой к записи истории/чата и живут 30 дней (потом Content зачищается —
// см. GeneratedFileCleanupService). Скачать может только владелец.
[ApiController]
[Route("api/files")]
[Authorize]
public class GeneratedFilesController(AppDbContext db) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var file = await db.GeneratedFiles.FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId);
        if (file is null)
        {
            return NotFound();
        }
        if (file.Content is null)
        {
            // Байты зачищены по сроку хранения — метаданные остались, но файла уже нет.
            return StatusCode(410, new ErrorResponse("Файл удалён по истечении срока хранения (30 дней)."));
        }
        return File(file.Content, file.ContentType, file.FileName);
    }
}
