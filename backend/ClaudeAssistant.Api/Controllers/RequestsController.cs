using System.Security.Claims;
using ClaudeAssistant.Api.Data;
using ClaudeAssistant.Api.Dtos;
using ClaudeAssistant.Api.Hubs;
using ClaudeAssistant.Api.Models;
using ClaudeAssistant.Api.Services.AiCompletion;
using ClaudeAssistant.Api.Services.FileTextExtraction;
using ClaudeAssistant.Api.Services.ProjectFiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
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
    ConversationStore conversationStore,
    IHubContext<UpdatesHub> hub,
    ILogger<RequestsController> logger
) : ControllerBase
{
    // Живой статус обработки пушим пользователю по SignalR (best-effort, потеря
    // события не влияет на ответ). IProgress.Report вызывается из agentic-цикла;
    // отправку не ждём (fire-and-forget), чтобы не тормозить обработку.
    private IProgress<string> BuildStatusReporter(Guid userId) =>
        new Progress<string>(text =>
            _ = hub.Clients.User(userId.ToString()).SendAsync(UpdatesHubEvents.RequestStatus, text)
        );
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

    [HttpDelete("history/{id:guid}")]
    public async Task<IActionResult> DeleteHistory(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var entry = await db.RequestHistory.FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId);
        if (entry is null)
        {
            return NotFound();
        }
        db.RequestHistory.Remove(entry);
        await db.SaveChangesAsync();
        return NoContent();
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
            // sandboxRoot != null — режим поиска по папке (инструменты); null —
            // прикреплённые файлы. StartConversationAsync унифицирует оба и, на
            // нативном пути, делает диалог продолжаемым (возвращает conversationId).
            var (agenticResult, conversationId) = await chatCompletionService.StartConversationAsync(
                model,
                systemPrompt,
                "Выполни задачу, описанную в системном промпте, и верни результат.",
                useFileSearch ? sandboxRoot! : null,
                userId,
                images,
                BuildStatusReporter(userId)
            );
            var resultText = agenticResult.ResultText;
            var accessedFiles = agenticResult.AccessedFiles.Count > 0 ? string.Join(", ", agenticResult.AccessedFiles) : null;

            var historyEntry = new RequestHistoryEntry
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
            };
            db.RequestHistory.Add(historyEntry);
            await db.SaveChangesAsync();

            // Привязываем запись истории к диалогу — чтобы follow-up'ы дописывали в
            // неё полный тред (см. Continue). Только для продолжаемых (нативный путь).
            if (conversationId is { } cid)
            {
                var state = conversationStore.Get(cid);
                if (state is not null)
                {
                    state.HistoryEntryId = historyEntry.Id;
                    conversationStore.Save(cid, state);
                }
            }

            var generatedFiles = agenticResult.GeneratedFiles.Select(f => new GeneratedFileDto(f.Id, f.FileName)).ToList();
            return Ok(new ExecuteResponse(resultText, accessedFiles, conversationId, generatedFiles.Count > 0 ? generatedFiles : null));
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

    // Продолжение диалога в пределах окна кэша (1 час). conversationId выдаётся в
    // ответе на execute. Дописывает вопрос, гоняет тот же ход (с инструментами,
    // если исходный запрос был поиском по папке), возвращает ответ. 410 — диалог
    // истёк/не найден: продолжать нельзя, контекст выпал из кэша.
    [HttpPost("{conversationId:guid}/continue")]
    [RequestSizeLimit(FileUploadLimits.MaxTotalSizeBytes)]
    public async Task<ActionResult<ContinueResponse>> Continue(Guid conversationId, [FromForm] string? message, List<IFormFile> files)
    {
        if (string.IsNullOrWhiteSpace(message) && files.Count == 0)
        {
            return BadRequest(new ErrorResponse("Пустое сообщение"));
        }

        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // Файлы, прикреплённые к follow-up: извлекаем текст/изображения так же, как в execute.
        string? combinedFileText = null;
        IReadOnlyList<byte[]> images = [];
        if (files.Count > 0)
        {
            try
            {
                var extraction = await extractionService.ExtractAndCombineAsync(files);
                combinedFileText = extraction.CombinedText;
                images = extraction.Images;
            }
            catch (FileValidationException ex)
            {
                return BadRequest(new ErrorResponse(ex.Message));
            }
        }

        var displayMessage = string.IsNullOrWhiteSpace(message) ? "(файлы)" : message!;

        try
        {
            var result = await chatCompletionService.ContinueConversationAsync(
                conversationId,
                message ?? string.Empty,
                userId,
                images: images,
                combinedFileText: combinedFileText,
                status: BuildStatusReporter(userId)
            );
            var accessedFiles = result.AccessedFiles.Count > 0 ? string.Join(", ", result.AccessedFiles) : null;

            // Дописываем ход диалога в запись истории — так весь диалог сохраняется
            // в БД и виден в истории даже после истечения окна кэша (продолжить
            // будет нельзя, но перечитать — можно).
            var state = conversationStore.Get(conversationId);
            if (state?.HistoryEntryId is { } entryId)
            {
                var entry = await db.RequestHistory.FindAsync(entryId);
                if (entry is not null)
                {
                    entry.ResultText =
                        (entry.ResultText ?? "")
                        + $"\n\n---\n\n**Вопрос:** {displayMessage}\n\n{result.ResultText}";
                    if (accessedFiles is not null)
                    {
                        entry.AccessedFiles = string.IsNullOrEmpty(entry.AccessedFiles)
                            ? accessedFiles
                            : $"{entry.AccessedFiles}, {accessedFiles}";
                    }
                    await db.SaveChangesAsync();
                }
            }

            var generatedFiles = result.GeneratedFiles.Select(f => new GeneratedFileDto(f.Id, f.FileName)).ToList();
            return Ok(new ContinueResponse(result.ResultText, accessedFiles, generatedFiles.Count > 0 ? generatedFiles : null));
        }
        catch (ConversationNotFoundException)
        {
            return StatusCode(410, new ErrorResponse("Сессия истекла — начните новый запрос"));
        }
        catch (InvalidOperationException ex)
        {
            // Осознанные ошибки модели (пустой ответ и т.п.) — как в execute.
            logger.LogError(ex, "Не удалось продолжить диалог {ConversationId}", conversationId);
            return StatusCode(502, new ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось продолжить диалог {ConversationId}", conversationId);
            return StatusCode(502, new ErrorResponse("Не удалось получить ответ от модели"));
        }
    }
}
