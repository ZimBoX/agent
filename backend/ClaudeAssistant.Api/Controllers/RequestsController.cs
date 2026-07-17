using System.Security.Claims;
using ClaudeAssistant.Api.Data;
using ClaudeAssistant.Api.Dtos;
using ClaudeAssistant.Api.Models;
using ClaudeAssistant.Api.Services.AiCompletion;
using ClaudeAssistant.Api.Services.FileTextExtraction;
using ClaudeAssistant.Api.Services.ProjectFiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAssistant.Api.Controllers;

[ApiController]
[Route("api/requests")]
[Authorize]
public class RequestsController(
    AppDbContext db,
    MultiFileExtractionService extractionService,
    ChatCompletionService chatCompletionService,
    SandboxedFileAccessService fileAccess,
    ILogger<RequestsController> logger
) : ControllerBase
{
    // Модель для запросов со своим промптом — шаблону взять её неоткуда, поэтому
    // статичная, как и везде в проекте пока не дошли до агрегирования по задачам
    // (см. PROJECT_PLAN.md, раздел 8.2).
    private const string CustomPromptModel = "claude-sonnet-5";

    // Компания в этих запросах всегда выступает подрядчиком или субподрядчиком,
    // никогда заказчиком — держим это явным для модели сразу для всех запросов
    // (и по шаблонам, и по свободному промпту) одной вставкой в коде, а не
    // редактируя формулировку в каждом шаблоне по отдельности: так не потеряется
    // и для новых шаблонов, которые появятся позже.
    private const string CompanyContextPreamble =
        "Контекст: наша компания всегда выступает подрядчиком или субподрядчиком по обсуждаемым документам (договорам, сметам, техническим заданиям и т.п.) — и никогда заказчиком. Там, где сторона документа влияет на анализ (риски, обязательства, ответственность, интересы), рассматривай его именно с этой стороны; отдельный разбор с точки зрения заказчика не нужен.\n\n";

    [HttpGet("history")]
    public async Task<ActionResult<List<HistoryEntryResponse>>> History()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var history = await db.RequestHistory
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new HistoryEntryResponse(
                r.Id,
                r.TemplateId,
                r.TemplateId != null ? r.Template!.Title : "Свой промпт",
                r.FileNames,
                r.Status.ToString(),
                r.ResultText,
                r.ErrorMessage,
                r.CreatedAt,
                r.AccessedFiles
            ))
            .Take(50)
            .ToListAsync();

        return Ok(history);
    }

    // Принимает либо templateId (готовый шаблон), либо customPrompt (свой текст) —
    // ровно одно из двух. Свободный промпт — сознательный выбор вместо попытки
    // заранее угадать полную библиотеку шаблонов под каждую должность (см.
    // PROJECT_PLAN.md, раздел 5): история такого рода запросов покажет, что
    // реально нужно людям, и это можно будет оформить в шаблоны позже.
    [HttpPost("execute")]
    [RequestSizeLimit(FileUploadLimits.MaxTotalSizeBytes)]
    public async Task<ActionResult<ExecuteResponse>> Execute(
        [FromForm] Guid? templateId,
        [FromForm] string? customPrompt,
        [FromForm] string? objectFolder,
        List<IFormFile> files
    )
    {
        Template? template = null;
        string systemPromptSource;
        string model;

        if (templateId is { } id && id != Guid.Empty)
        {
            template = await db.Templates.FindAsync(id);
            if (template is null)
            {
                return NotFound(new ErrorResponse("Шаблон не найден"));
            }

            systemPromptSource = template.SystemPrompt;
            model = template.Model;
        }
        else if (!string.IsNullOrWhiteSpace(customPrompt))
        {
            systemPromptSource = customPrompt;
            model = CustomPromptModel;
        }
        else
        {
            return BadRequest(new ErrorResponse("Укажите шаблон или свой промпт"));
        }

        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // Поиск по объекту доступен у шаблонов с AllowFileSearch, а также всегда —
        // у свободного промпта (template здесь null именно в этом случае, ветки
        // выше это гарантируют): своего шаблона в БД у него нет и взять флаг
        // неоткуда, но сама возможность нужна ему не меньше. objectFolder — не
        // "имя объекта" из заранее известного списка, а реальный путь к папке,
        // которую определил сам клиент (папка прикреплённого файла или папка,
        // по которой кликнули в Проводнике) — валидируем его здесь.
        string? sandboxRoot = null;
        if ((template is null || template.AllowFileSearch) && !string.IsNullOrWhiteSpace(objectFolder))
        {
            sandboxRoot = fileAccess.ValidateRoot(objectFolder, userId);
            if (sandboxRoot is null)
            {
                return BadRequest(new ErrorResponse("Указанная папка недоступна"));
            }
        }
        var useFileSearch = sandboxRoot is not null;
        var fileNames = string.Join(", ", files.Select(f => f.FileName));

        string combinedText;
        IReadOnlyList<byte[]> images = [];
        if (files.Count == 0 && useFileSearch)
        {
            // Поиск по объекту не требует ручного прикрепления файлов — это и есть
            // весь смысл: модель сама найдёт и прочитает нужное внутри папки объекта.
            combinedText = "";
        }
        else
        {
            try
            {
                var extraction = await extractionService.ExtractAndCombineAsync(files);
                combinedText = extraction.CombinedText;
                images = extraction.Images;
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
                // Настоящий catch-all — раньше его тут не было, и любая непредвиденная
                // ошибка извлечения (например, баг парсинга конкретного xlsx) падала не
                // в BadRequest выше, а улетала необработанной дальше и в Development-режиме
                // ASP.NET Core сам показывал клиенту сырой стектрейс прямо в ответе.
                logger.LogError(ex, "Не удалось извлечь текст из файлов (шаблон {TemplateId})", templateId);
                return StatusCode(500, new ErrorResponse("Не удалось обработать файл(ы) — сообщите администратору"));
            }
        }

        // Шаблоны почти всегда содержат {{document_text}} осознанно (так их и учили
        // писать), а вот от пользователя со своим промптом никто не ждёт, что он
        // знает про этот плейсхолдер — если его нет, просто дописываем текст
        // документа в конец.
        var systemPrompt = CompanyContextPreamble + (systemPromptSource.Contains("{{document_text}}")
            ? systemPromptSource.Replace("{{document_text}}", combinedText)
            : $"{systemPromptSource}\n\nТекст документа:\n{combinedText}");

        try
        {
            string resultText;
            string? accessedFiles = null;

            if (useFileSearch)
            {
                var agenticResult = await chatCompletionService.CompleteWithFileSearchAsync(
                    model,
                    systemPrompt,
                    "Выполни задачу, описанную в системном промпте, и верни результат.",
                    sandboxRoot!,
                    userId,
                    images
                );
                resultText = agenticResult.ResultText;
                accessedFiles = agenticResult.AccessedFiles.Count > 0 ? string.Join(", ", agenticResult.AccessedFiles) : null;
            }
            else
            {
                resultText = await chatCompletionService.CompleteAsync(
                    model,
                    systemPrompt,
                    "Выполни задачу, описанную в системном промпте, и верни результат.",
                    images
                );
            }

            db.RequestHistory.Add(
                new RequestHistoryEntry
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TemplateId = template?.Id,
                    CustomPromptText = template is null ? customPrompt : null,
                    FileNames = fileNames,
                    AccessedFiles = accessedFiles,
                    Status = RequestStatus.Success,
                    ResultText = resultText,
                    CreatedAt = DateTimeOffset.UtcNow,
                }
            );
            await db.SaveChangesAsync();

            return Ok(new ExecuteResponse(resultText, accessedFiles));
        }
        // Осознанные, понятные пользователю ошибки (например, пустой ответ модели
        // из-за слишком большого объёма данных объекта — см.
        // ChatCompletionService.RequireNonEmptyText) показываем как есть, а не
        // прячем за общим "Не удалось получить ответ от модели" ниже — иначе
        // пользователь снова не поймёт, что вообще произошло и что с этим делать.
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Не удалось выполнить запрос к модели (шаблон {TemplateId})", templateId);

            db.RequestHistory.Add(
                new RequestHistoryEntry
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TemplateId = template?.Id,
                    CustomPromptText = template is null ? customPrompt : null,
                    FileNames = fileNames,
                    Status = RequestStatus.Failed,
                    ErrorMessage = ex.Message,
                    CreatedAt = DateTimeOffset.UtcNow,
                }
            );
            await db.SaveChangesAsync();

            return StatusCode(502, new ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось выполнить запрос к модели (шаблон {TemplateId})", templateId);

            db.RequestHistory.Add(
                new RequestHistoryEntry
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TemplateId = template?.Id,
                    CustomPromptText = template is null ? customPrompt : null,
                    FileNames = fileNames,
                    Status = RequestStatus.Failed,
                    ErrorMessage = ex.Message,
                    CreatedAt = DateTimeOffset.UtcNow,
                }
            );
            await db.SaveChangesAsync();

            return StatusCode(502, new ErrorResponse("Не удалось получить ответ от модели"));
        }
    }
}
