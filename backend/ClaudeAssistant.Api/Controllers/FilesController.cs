using ClaudeAssistant.Api.Dtos;
using ClaudeAssistant.Api.Services.FileTextExtraction;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeAssistant.Api.Controllers;

[ApiController]
[Route("api/files")]
[Authorize]
public class FilesController(MultiFileExtractionService extractionService, ILogger<FilesController> logger) : ControllerBase
{
    // Промежуточный эндпоинт для проверки пайплайна извлечения текста.
    // В реальном флоу (Этап 3) файлы уходят вместе с шаблоном в один "execute"-запрос.
    [HttpPost("extract")]
    [RequestSizeLimit(FileUploadLimits.MaxTotalSizeBytes)]
    public async Task<ActionResult<ExtractResponse>> Extract(List<IFormFile> files)
    {
        try
        {
            var combined = await extractionService.ExtractAndCombineAsync(files);
            return Ok(new ExtractResponse(combined.CombinedText));
        }
        catch (Exception ex) when (
            ex is FileValidationException
            or UnsupportedFileFormatException
            or UnreadablePdfException
        )
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            // Ничего внутреннего (стек, детали парсера) наружу не отдаём —
            // только чистое сообщение, реальная ошибка уходит в лог.
            logger.LogError(ex, "Не удалось обработать файлы: {FileNames}", string.Join(", ", files.Select(f => f.FileName)));
            return BadRequest(new ErrorResponse("Не удалось обработать один или несколько файлов"));
        }
    }
}
