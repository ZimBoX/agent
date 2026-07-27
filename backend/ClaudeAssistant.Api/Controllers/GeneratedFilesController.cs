using System.Security.Claims;
using ClaudeAssistant.Api.Services.FileGeneration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeAssistant.Api.Controllers;

// Отдаёт файлы, сгенерированные моделью (create_file). Файлы эфемерные (в памяти,
// TTL 1 час) и привязаны к пользователю — скачать может только владелец в пределах
// окна жизни. По истечении — 404 (пусть сгенерирует заново).
[ApiController]
[Route("api/files")]
[Authorize]
public class GeneratedFilesController(GeneratedFileStore store) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public IActionResult Get(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var file = store.Get(userId, id);
        if (file is null)
        {
            return NotFound();
        }
        return File(file.Bytes, file.ContentType, file.FileName);
    }
}
