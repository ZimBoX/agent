using System.Security.Claims;
using ClaudeAssistant.Api.Data;
using ClaudeAssistant.Api.Dtos;
using ClaudeAssistant.Api.Hubs;
using ClaudeAssistant.Api.Models;
using ClaudeAssistant.Api.Services.AiCompletion;
using ClaudeAssistant.Api.Services.FileGeneration;
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
    AttachedFilesSandboxService attachedSandbox,
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

    // Сохраняет сгенерированные моделью файлы в БД (байты + привязка к записи истории
    // или сообщению чата) и возвращает их метаданные для ответа. Файлы живут 30 дней
    // (см. GeneratedFileCleanupService), потом Content зачищается.
    private async Task<List<GeneratedFileDto>> PersistGeneratedFilesAsync(
        IReadOnlyList<GeneratedFileData> files,
        Guid userId,
        Guid? historyEntryId,
        Guid? chatMessageId
    )
    {
        if (files.Count == 0)
        {
            return [];
        }
        foreach (var f in files)
        {
            db.GeneratedFiles.Add(new GeneratedFileRecord
            {
                Id = f.Id,
                UserId = userId,
                FileName = f.FileName,
                ContentType = f.ContentType,
                Content = f.Bytes,
                SizeBytes = f.Bytes.Length,
                CreatedAt = DateTimeOffset.UtcNow,
                RequestHistoryEntryId = historyEntryId,
                ChatMessageId = chatMessageId,
            });
        }
        await db.SaveChangesAsync();
        return files.Select(f => new GeneratedFileDto(f.Id, f.FileName, true)).ToList();
    }

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
                r.AccessedFiles,
                db.GeneratedFiles
                    .Where(f => f.RequestHistoryEntryId == r.Id)
                    .OrderBy(f => f.CreatedAt)
                    .Select(f => new GeneratedFileDto(f.Id, f.FileName, f.Content != null))
                    .ToList()
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
        List<IFormFile> files,
        // ASP.NET привязывает CancellationToken к HttpContext.RequestAborted: когда
        // пользователь жмёт «Прервать», клиент обрывает HTTP-запрос → токен
        // отменяется → модельный вызов останавливается (не молотит вхолостую и не
        // тратит деньги на брошенный запрос).
        CancellationToken cancellationToken
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

        // Большая пачка ВЛОЖЕННЫХ файлов (не папка объекта): не встраиваем их
        // содержимое в промпт (инлайн раздул бы запрос сверх лимита входа модели),
        // а раскладываем во временную песочницу на сервере и даём модели те же
        // инструменты, что и для папки объекта — чтение по частям (read_document с
        // offset) и зум (render_page с вырезкой региона). Небольшие пачки читаются
        // по-старому инлайном — быстрее (один ход, без tool-loop'а).
        var attachedToolMode = !useFileSearch && files.Count > 0 && files.Sum(f => f.Length) > attachedSandbox.InlineMaxBytes;

        string combinedText;
        IReadOnlyList<byte[]> images = [];
        var userTask = "Выполни задачу, описанную в системном промпте, и верни результат.";
        if (files.Count == 0 && useFileSearch)
        {
            // Поиск по объекту не требует ручного прикрепления файлов — это и есть
            // весь смысл: модель сама найдёт и прочитает нужное внутри папки объекта.
            combinedText = "";
        }
        else if (attachedToolMode)
        {
            try
            {
                MultiFileExtractionService.ValidateLimits(files);
            }
            catch (FileValidationException ex)
            {
                return BadRequest(new ErrorResponse(ex.Message));
            }

            sandboxRoot = await attachedSandbox.MaterializeAsync(files, userId, HttpContext.RequestAborted);
            useFileSearch = true;
            combinedText = "(Приложенные файлы не встроены в этот запрос из-за их объёма — они доступны для чтения инструментами, см. задачу.)";
            userTask =
                "К запросу приложены файлы — они НЕ встроены в промпт из-за объёма, а доступны через инструменты: "
                + "list_files покажет их список; read_document читает текст по частям (offset для следующей порции); "
                + "render_page рендерит страницу PDF или её вырезку с увеличением (region); document_overview/search_document — обзор и поиск по PDF. "
                + $"Файлы: {fileNames}. Прочитай нужное этими инструментами и выполни задачу из системного промпта.";
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
                userTask,
                useFileSearch ? sandboxRoot! : null,
                userId,
                images,
                BuildStatusReporter(userId),
                cancellationToken
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
                Model = model,
                InputTokens = (int)agenticResult.Usage.InputTokens,
                CacheReadTokens = (int)agenticResult.Usage.CacheReadTokens,
                OutputTokens = (int)agenticResult.Usage.OutputTokens,
                ProviderCostUsd = agenticResult.Usage.ProviderCostUsd,
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

            var generatedFiles = await PersistGeneratedFilesAsync(agenticResult.GeneratedFiles, userId, historyEntry.Id, chatMessageId: null);
            return Ok(new ExecuteResponse(resultText, accessedFiles, conversationId, generatedFiles.Count > 0 ? generatedFiles : null));
        }
        // Пользователь прервал запрос (клиент оборвал соединение → RequestAborted).
        // Соединения уже нет, тело ответа читать некому — не пишем «неудачу» в
        // историю и не шумим в логах на уровне ошибки.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Запрос прерван пользователем (шаблон {TemplateId})", templateId);
            return StatusCode(499);
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
    public async Task<ActionResult<ContinueResponse>> Continue(
        Guid conversationId,
        [FromForm] string? message,
        List<IFormFile> files,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(message) && files.Count == 0)
        {
            return BadRequest(new ErrorResponse("Пустое сообщение"));
        }

        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // Файлы, прикреплённые к follow-up. Если диалог уже читает вложения
        // инструментами (песочница вложений) — дописываем новые файлы туда же, чтобы
        // модель прочла их теми же инструментами (по частям + зум), а не встраивала
        // инлайн. Иначе — извлекаем текст/изображения так же, как в execute.
        var followupState = conversationStore.Get(conversationId);
        string? combinedFileText = null;
        IReadOnlyList<byte[]> images = [];
        if (files.Count > 0)
        {
            if (followupState?.SandboxRoot is { } followupSandbox && attachedSandbox.IsAttachedSandbox(followupSandbox))
            {
                try
                {
                    MultiFileExtractionService.ValidateLimits(files);
                }
                catch (FileValidationException ex)
                {
                    return BadRequest(new ErrorResponse(ex.Message));
                }

                await attachedSandbox.AddToAsync(followupSandbox, files, HttpContext.RequestAborted);
                combinedFileText =
                    $"К диалогу приложены новые файлы, доступные через инструменты (list_files/read_document/render_page): {string.Join(", ", files.Select(f => f.FileName))}. Прочитай их при необходимости.";
            }
            else
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
                status: BuildStatusReporter(userId),
                cancellationToken: cancellationToken
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
                    // Токены/стоимость продолжения дописываем к той же записи — весь
                    // диалог (запрос + все follow-up'ы) учитывается одной строкой статистики.
                    entry.InputTokens += (int)result.Usage.InputTokens;
                    entry.CacheReadTokens += (int)result.Usage.CacheReadTokens;
                    entry.OutputTokens += (int)result.Usage.OutputTokens;
                    entry.ProviderCostUsd += result.Usage.ProviderCostUsd;
                    await db.SaveChangesAsync();
                }
            }

            // Файлы продолжения привязываем к той же записи истории, что и исходный запрос.
            var linkedEntryId = conversationStore.Get(conversationId)?.HistoryEntryId;
            var generatedFiles = await PersistGeneratedFilesAsync(result.GeneratedFiles, userId, linkedEntryId, chatMessageId: null);
            return Ok(new ContinueResponse(result.ResultText, accessedFiles, generatedFiles.Count > 0 ? generatedFiles : null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Продолжение диалога прервано пользователем ({ConversationId})", conversationId);
            return StatusCode(499);
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
