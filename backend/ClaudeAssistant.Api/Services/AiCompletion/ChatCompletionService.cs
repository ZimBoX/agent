using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using ClaudeAssistant.Api.Services.FileTextExtraction;
using ClaudeAssistant.Api.Services.ProjectFiles;
using OpenAI;
using OpenAI.Chat;

namespace ClaudeAssistant.Api.Services.AiCompletion;

public record AgenticCompletionResult(string ResultText, List<string> AccessedFiles);

// Два полных, независимых пути к модели в одном классе — намеренно, а не через
// стирание старого кода. Нативный (Anthropic SDK, напрямую /v1/messages) — новый,
// основной, даёт доступ к prompt caching, которого у OpenAI-совместимого маршрута
// нет (cache_control не выражается в OpenAI wire-формате). Legacy (OpenAI SDK) —
// прежняя, проверенная в бою реализация, оставлена нетронутой на случай, если
// нативный путь поведёт себя неожиданно на реальной нагрузке: переключение —
// это Aggregator:UseNativeSdk=false (можно через переменную окружения
// Aggregator__UseNativeSdk, без пересборки) и рестарт процесса, а не git revert
// и повторный деплой.
public class ChatCompletionService(IConfiguration configuration, SandboxedFileAccessService fileAccess, ILogger<ChatCompletionService> logger)
{
    // Ограничивает число обращений модели к инструментам за один запрос — иначе
    // модель теоретически может уйти в цикл list_files/read_document без остановки,
    // раздувая и время ответа, и стоимость запроса. 8 оказалось мало на практике:
    // реальный объект пользователя потребовал 18 read_document (договор, допсоглашение,
    // больше десятка АОСР/ведомостей) — упёрлись в лимит на середине, что и привело
    // к пустому ответу (см. IMPLEMENTATION_CHECKLIST.md, Этап 11).
    private const int MaxToolIterations = 20;

    // Ограничивает, сколько текста одного документа модель получает за один вызов
    // read_document — иначе один большой файл может занять весь бюджет контекста.
    // Снижено с 40 000: при действительно большом объекте (десятки файлов) даже
    // по 40к на файл суммарный контекст мог раздуться до сотен тысяч символов.
    private const int MaxDocumentCharsPerTool = 20_000;

    // Суммарный бюджет текста документов на весь запрос (across всех read_document) —
    // отдельная защита от объектов с большим числом файлов, где даже уменьшенный
    // лимит на файл в сумме всё равно раздувает контекст до неприемлемых размеров
    // (см. тот же инцидент — 18 файлов почти по лимиту каждый).
    private const int MaxTotalDocumentChars = 150_000;

    // Аналогичный суммарный лимит для сканов без текстового слоя, найденных через
    // read_document (не для файлов, прикреплённых вручную — там своего протокола
    // нет) — картинки тяжелее текста, поэтому бюджет отдельный и куда скромнее.
    private const int MaxTotalToolImages = 30;

    // История этих двух констант — два противоположных реальных инцидента подряд:
    // 1) SDK по умолчанию ждёт 100с на попытку и делает несколько повторов — при
    //    настоящем затыке на стороне агрегатора это растягивало единственный вызов
    //    на 5-7 минут молчания без единой ошибки ("зависший" запрос).
    // 2) Первая правка (60с, 1 повтор) оказалась СЛИШКОМ короткой: на реальном
    //    большом объекте модель ответила успешно за 99с (68k входных токенов,
    //    подтверждено панелью агрегатора — токены и деньги списаны), но клиент
    //    получил таймаут раньше, чем пришёл ответ. Хуже того — автоповтор в таком
    //    случае не отменяет уже идущую на стороне агрегатора генерацию, а значит
    //    рискует задвоить списание за один и тот же дорогой запрос.
    // Баланс: таймаут увеличен с запасом под большие agentic-запросы (see
    // MaxTotalDocumentChars/MaxToolIterations выше), повторов больше нет —
    // лучше один раз честно подождать дольше, чем тихо платить дважды.
    private const int ModelCallTimeoutSeconds = 240;
    private const int ModelCallMaxRetries = 0;

    // Максимальный объём ответа модели (thinking сюда не входит — не запрашиваем
    // extended thinking для этих задач). 16000 — тот же порядок, что раньше
    // подразумевался у OpenAI-совместимого пути через параметры по умолчанию.
    private const int MaxOutputTokens = 16000;

    private bool UseNativeSdk => configuration.GetValue("Aggregator:UseNativeSdk", true);

    public Task<string> CompleteAsync(
        string model,
        string systemPrompt,
        string userText,
        IReadOnlyList<byte[]>? images = null,
        CancellationToken cancellationToken = default
    ) =>
        UseNativeSdk
            ? CompleteAsyncNative(model, systemPrompt, userText, images, cancellationToken)
            : CompleteAsyncLegacy(model, systemPrompt, userText, images, cancellationToken);

    public Task<AgenticCompletionResult> CompleteWithFileSearchAsync(
        string model,
        string systemPrompt,
        string userText,
        string sandboxRoot,
        Guid userId,
        IReadOnlyList<byte[]>? images = null,
        CancellationToken cancellationToken = default
    ) =>
        UseNativeSdk
            ? CompleteWithFileSearchAsyncNative(model, systemPrompt, userText, sandboxRoot, userId, images, cancellationToken)
            : CompleteWithFileSearchAsyncLegacy(model, systemPrompt, userText, sandboxRoot, userId, images, cancellationToken);

    // ============================================================
    // Native — официальный Anthropic .NET SDK, напрямую /v1/messages
    // агрегатора (не через OpenAI-совместимый маршрут). Даёт доступ к
    // cache_control (prompt caching), которого нет в OpenAI wire-формате —
    // на этом агрегаторе подтверждено эмпирически: повторное чтение
    // закэшированного system-промпта стоит ~1/11 от полной цены.
    // ============================================================

    private async Task<string> CompleteAsyncNative(
        string model,
        string systemPrompt,
        string userText,
        IReadOnlyList<byte[]>? images,
        CancellationToken cancellationToken
    )
    {
        var client = CreateNativeClient();

        var response = await client.Messages.Create(
            new MessageCreateParams
            {
                Model = model,
                MaxTokens = MaxOutputTokens,
                System = BuildSystemBlocksNative(systemPrompt),
                Messages = [new() { Role = Role.User, Content = BuildUserContentNative(userText, images) }],
            },
            cancellationToken
        );
        LogUsageNative(response);

        return RequireNonEmptyTextNative(response);
    }

    private async Task<AgenticCompletionResult> CompleteWithFileSearchAsyncNative(
        string model,
        string systemPrompt,
        string userText,
        string sandboxRoot,
        Guid userId,
        IReadOnlyList<byte[]>? images,
        CancellationToken cancellationToken
    )
    {
        var client = CreateNativeClient();
        var systemBlocks = BuildSystemBlocksNative(systemPrompt);

        List<MessageParam> messages = [new() { Role = Role.User, Content = BuildUserContentNative(userText, images) }];

        var accessedFiles = new List<string>();
        var totalDocumentChars = 0;
        var totalToolImages = 0;

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var response = await client.Messages.Create(
                new MessageCreateParams
                {
                    Model = model,
                    MaxTokens = MaxOutputTokens,
                    System = systemBlocks,
                    Tools = [BuildListFilesTool(), BuildReadDocumentTool()],
                    Messages = messages,
                },
                cancellationToken
            );
            LogUsageNative(response);

            if (response.StopReason != "tool_use")
            {
                return new AgenticCompletionResult(RequireNonEmptyTextNative(response), accessedFiles);
            }

            List<ContentBlockParam> assistantContent = [];
            List<ContentBlockParam> toolResults = [];

            foreach (var block in response.Content)
            {
                if (block.TryPickText(out TextBlock? text))
                {
                    assistantContent.Add(new TextBlockParam { Text = text.Text });
                }
                else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
                {
                    assistantContent.Add(new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });

                    var (content, addedChars, addedImages) = await ExecuteToolAsyncNative(
                        toolUse,
                        sandboxRoot,
                        userId,
                        accessedFiles,
                        totalDocumentChars,
                        totalToolImages
                    );
                    totalDocumentChars += addedChars;
                    totalToolImages += addedImages;

                    toolResults.Add(new ToolResultBlockParam { ToolUseID = toolUse.ID, Content = content });
                }
            }

            messages.Add(new() { Role = Role.Assistant, Content = assistantContent });
            messages.Add(new() { Role = Role.User, Content = toolResults });
        }

        // Лимит итераций исчерпан — просим финальный текстовый ответ без
        // инструментов, а не молча обрываем запрос без ответа пользователю (см.
        // ту же логику и комментарий в legacy-версии ниже).
        var finalResponse = await client.Messages.Create(
            new MessageCreateParams
            {
                Model = model,
                MaxTokens = MaxOutputTokens,
                System = systemBlocks,
                Messages = messages,
            },
            cancellationToken
        );
        LogUsageNative(finalResponse);
        return new AgenticCompletionResult(RequireNonEmptyTextNative(finalResponse), accessedFiles);
    }

    // Единственная причина, по которой вообще затевалась миграция на нативный SDK —
    // возможность видеть попадания в prompt cache (у OpenAI-совместимого маршрута
    // usage.cache_read_input_tokens в принципе не существует). Логируем на каждый
    // вызов модели, а не только итогово — именно повторные вызовы внутри одного
    // agentic tool-loop и есть тот случай, где кэш реально экономит.
    private void LogUsageNative(Message response) =>
        logger.LogInformation(
            "Anthropic usage: input={InputTokens} cacheWrite={CacheCreationInputTokens} cacheRead={CacheReadInputTokens} output={OutputTokens}",
            response.Usage.InputTokens,
            response.Usage.CacheCreationInputTokens,
            response.Usage.CacheReadInputTokens,
            response.Usage.OutputTokens
        );

    private async Task<(List<Block> Content, int AddedChars, int AddedImages)> ExecuteToolAsyncNative(
        ToolUseBlock toolUse,
        string sandboxRoot,
        Guid userId,
        List<string> accessedFiles,
        int totalDocumentCharsSoFar,
        int totalToolImagesSoFar
    )
    {
        try
        {
            var path = "";
            if (toolUse.Input.TryGetValue("path", out var pathElement))
            {
                path = pathElement.GetString() ?? "";
            }

            switch (toolUse.Name)
            {
                case "list_files":
                    var entries = fileAccess.ListFiles(sandboxRoot, path, userId);
                    return (TextResultNative(entries.Count > 0 ? string.Join("\n", entries) : "(папка пуста)"), 0, 0);

                case "read_document":
                    if (totalDocumentCharsSoFar >= MaxTotalDocumentChars)
                    {
                        return (
                            TextResultNative(
                                "Достигнут суммарный лимит объёма прочитанных документов для одного запроса. Дальнейшие файлы не читаются — сформулируй ответ по уже прочитанным документам, при необходимости уточни у пользователя, что именно посмотреть отдельно."
                            ),
                            0,
                            0
                        );
                    }

                    var extraction = await fileAccess.ReadDocumentAsync(sandboxRoot, path, userId);
                    if (!accessedFiles.Contains(path))
                    {
                        accessedFiles.Add(path);
                    }

                    if (extraction.Text is not null)
                    {
                        var truncated = extraction.Text.Length > MaxDocumentCharsPerTool
                            ? extraction.Text[..MaxDocumentCharsPerTool] + "\n\n[...текст обрезан, документ слишком длинный...]"
                            : extraction.Text;
                        return (TextResultNative(truncated), truncated.Length, 0);
                    }

                    if (totalToolImagesSoFar >= MaxTotalToolImages)
                    {
                        return (
                            TextResultNative(
                                "Достигнут суммарный лимит числа страниц-сканов для одного запроса. Этот файл (скан без текстового слоя) не читается — сформулируй ответ по уже прочитанным документам."
                            ),
                            0,
                            0
                        );
                    }

                    var images = extraction.Images;
                    var imageParts = new List<Block>
                    {
                        new TextBlockParam { Text = $"Файл — скан без текстового слоя, {images.Count} стр. приложены как изображения:" },
                    };
                    imageParts.AddRange(
                        images.Select(img => (Block)new ImageBlockParam
                        {
                            Source = new Base64ImageSource { MediaType = "image/png", Data = Convert.ToBase64String(img) },
                        })
                    );
                    return (imageParts, 0, images.Count);

                default:
                    return (TextResultNative($"Неизвестный инструмент: {toolUse.Name}"), 0, 0);
            }
        }
        catch (PathAccessDeniedException)
        {
            return (TextResultNative("Доступ запрещён: путь вне разрешённой папки объекта, либо файл не найден/не поддерживается."), 0, 0);
        }
        catch (Exception ex)
        {
            return (TextResultNative($"Ошибка при выполнении инструмента: {ex.Message}"), 0, 0);
        }
    }

    private static Tool BuildListFilesTool() =>
        new()
        {
            Name = "list_files",
            Description =
                "Возвращает список файлов и подпапок по относительному пути внутри папки объекта. Пустой path (или его отсутствие) — корень папки объекта.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = "Относительный путь внутри папки объекта; пусто — корень" }
                    ),
                },
            },
        };

    private static Tool BuildReadDocumentTool() =>
        new()
        {
            Name = "read_document",
            Description =
                "Читает и возвращает содержимое файла (.docx/.xlsx/.pdf) по относительному пути внутри папки объекта — текстом, либо, если это скан без текстового слоя, страницами-изображениями.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = "Относительный путь к файлу внутри папки объекта" }
                    ),
                },
                Required = ["path"],
            },
        };

    private static List<Block> TextResultNative(string text) => [new TextBlockParam { Text = text }];

    // cache_control на system-промпте — то, ради чего вообще делалась миграция:
    // system-промпт у agentic-запросов один и тот же на протяжении всего
    // tool-loop'а (растёт только messages), поэтому каждое повторное обращение
    // за один и тот же запрос читает его из кэша, а не оплачивает заново.
    private static List<TextBlockParam> BuildSystemBlocksNative(string systemPrompt) =>
        [new() { Text = systemPrompt, CacheControl = new CacheControlEphemeral() }];

    private static List<ContentBlockParam> BuildUserContentNative(string text, IReadOnlyList<byte[]>? images)
    {
        var parts = new List<ContentBlockParam> { new TextBlockParam { Text = text } };
        if (images is not null)
        {
            parts.AddRange(
                images.Select(img => (ContentBlockParam)new ImageBlockParam
                {
                    Source = new Base64ImageSource { MediaType = "image/png", Data = Convert.ToBase64String(img) },
                })
            );
        }
        return parts;
    }

    // См. комментарий у RequireNonEmptyText (legacy) — тот же самый реальный
    // инцидент с формально успешным, но пустым ответом.
    private static string RequireNonEmptyTextNative(Message response)
    {
        var text = ExtractTextNative(response);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                "Модель вернула пустой ответ — вероятно, объём данных объекта оказался слишком большим для одного запроса. Попробуйте сузить область поиска или задать более точный вопрос."
            );
        }
        return text;
    }

    private static string ExtractTextNative(Message response)
    {
        var sb = new StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text))
            {
                sb.Append(text.Text);
            }
        }
        return sb.ToString();
    }

    private AnthropicClient CreateNativeClient()
    {
        var baseUrl =
            configuration["Aggregator:NativeBaseUrl"]
            ?? throw new InvalidOperationException("Aggregator:NativeBaseUrl не задан в конфигурации");
        var apiKey =
            Environment.GetEnvironmentVariable("AITUNNEL_API_KEY")
            ?? throw new InvalidOperationException("Переменная окружения AITUNNEL_API_KEY не задана");

        return new AnthropicClient
        {
            BaseUrl = baseUrl,
            // Агрегатор ожидает Authorization: Bearer <token> даже на нативном
            // /v1/messages маршруте (подтверждено curl'ом) — это не OAuth-токен,
            // поэтому AuthToken (не ApiKey, который ушёл бы как x-api-key).
            AuthToken = apiKey,
            Timeout = TimeSpan.FromSeconds(ModelCallTimeoutSeconds),
            MaxRetries = ModelCallMaxRetries,
        };
    }

    // ============================================================
    // Legacy — прежняя реализация через OpenAI .NET SDK и OpenAI-совместимый
    // маршрут агрегатора. Оставлена без единого изменения логики — только
    // переименована (суффикс Legacy) — как путь для мгновенного отката через
    // Aggregator:UseNativeSdk=false, без пересборки и без git revert.
    // ============================================================

    private async Task<string> CompleteAsyncLegacy(
        string model,
        string systemPrompt,
        string userText,
        IReadOnlyList<byte[]>? images,
        CancellationToken cancellationToken
    )
    {
        var chatClient = CreateLegacyChatClient(model);

        List<ChatMessage> messages = [new SystemChatMessage(systemPrompt), BuildUserMessageLegacy(userText, images)];

        ChatCompletion completion = await chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        return RequireNonEmptyTextLegacy(completion);
    }

    private async Task<AgenticCompletionResult> CompleteWithFileSearchAsyncLegacy(
        string model,
        string systemPrompt,
        string userText,
        string sandboxRoot,
        Guid userId,
        IReadOnlyList<byte[]>? images,
        CancellationToken cancellationToken
    )
    {
        var chatClient = CreateLegacyChatClient(model);

        var listFilesTool = ChatTool.CreateFunctionTool(
            "list_files",
            "Возвращает список файлов и подпапок по относительному пути внутри папки объекта. Пустой path (или его отсутствие) — корень папки объекта.",
            BinaryData.FromString(
                """
                {"type":"object","properties":{"path":{"type":"string","description":"Относительный путь внутри папки объекта; пусто — корень"}}}
                """
            )
        );
        var readDocumentTool = ChatTool.CreateFunctionTool(
            "read_document",
            "Читает и возвращает содержимое файла (.docx/.xlsx/.pdf) по относительному пути внутри папки объекта — текстом, либо, если это скан без текстового слоя, страницами-изображениями.",
            BinaryData.FromString(
                """
                {"type":"object","properties":{"path":{"type":"string","description":"Относительный путь к файлу внутри папки объекта"}},"required":["path"]}
                """
            )
        );

        var chatOptions = new ChatCompletionOptions();
        chatOptions.Tools.Add(listFilesTool);
        chatOptions.Tools.Add(readDocumentTool);

        List<ChatMessage> messages = [new SystemChatMessage(systemPrompt), BuildUserMessageLegacy(userText, images)];

        var accessedFiles = new List<string>();
        var totalDocumentChars = 0;
        var totalToolImages = 0;

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var completion = await chatClient.CompleteChatAsync(messages, chatOptions, cancellationToken);

            if (completion.Value.FinishReason != ChatFinishReason.ToolCalls)
            {
                return new AgenticCompletionResult(RequireNonEmptyTextLegacy(completion.Value), accessedFiles);
            }

            messages.Add(new AssistantChatMessage(completion.Value));

            foreach (var toolCall in completion.Value.ToolCalls)
            {
                var (content, addedChars, addedImages) = await ExecuteToolAsyncLegacy(
                    toolCall,
                    sandboxRoot,
                    userId,
                    accessedFiles,
                    totalDocumentChars,
                    totalToolImages
                );
                totalDocumentChars += addedChars;
                totalToolImages += addedImages;
                messages.Add(new ToolChatMessage(toolCall.Id, content));
            }
        }

        // Лимит итераций исчерпан — просим финальный текстовый ответ без инструментов,
        // а не молча обрываем запрос без ответа пользователю. chatOptions сюда
        // намеренно не передаётся — тем не менее модель уже видела в истории
        // сообщений собственные предыдущие tool_calls, поэтому проверка на пустой
        // ответ ниже (RequireNonEmptyTextLegacy) обязательна: именно на этом пути и
        // случился реальный инцидент с пустым ответом без единой ошибки.
        var finalCompletion = await chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        return new AgenticCompletionResult(RequireNonEmptyTextLegacy(finalCompletion.Value), accessedFiles);
    }

    // Модель иногда возвращает формально успешный ответ (200, без исключений) с
    // пустым содержимым — например, когда финальный вызов после исчерпания лимита
    // итераций происходит без объявленных инструментов, а модель всё ещё "хочет"
    // вызвать какой-то из них. Раньше такой пустой текст молча уходил
    // пользователю как успешный ответ (реальный инцидент — см. чеклист); теперь
    // это осознанная ошибка с понятным сообщением, а не тихая пустота.
    private static string RequireNonEmptyTextLegacy(ChatCompletion completion)
    {
        var text = ExtractTextLegacy(completion);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                "Модель вернула пустой ответ — вероятно, объём данных объекта оказался слишком большим для одного запроса. Попробуйте сузить область поиска или задать более точный вопрос."
            );
        }
        return text;
    }

    // Возвращает (содержимое результата инструмента, сколько символов текста и
    // сколько картинок этот вызов добавил в суммарные бюджеты) — последние два
    // нужны, чтобы вызывающий код мог накапливать totalDocumentChars/
    // totalToolImages самостоятельно (у async-методов не может быть ref/out
    // параметров). Content — всегда список content-частей (даже для обычного
    // текста, см. ниже) — единообразно и для текстовых результатов, и для
    // сканов без текстового слоя, у которых внутри картинки (подтверждено
    // smoke-тестом: агрегатор поддерживает изображения в результате tool-вызова).
    private async Task<(IReadOnlyList<ChatMessageContentPart> Content, int AddedChars, int AddedImages)> ExecuteToolAsyncLegacy(
        ChatToolCall toolCall,
        string sandboxRoot,
        Guid userId,
        List<string> accessedFiles,
        int totalDocumentCharsSoFar,
        int totalToolImagesSoFar
    )
    {
        try
        {
            var path = "";
            if (toolCall.FunctionArguments is { } args && args.ToMemory().Length > 0)
            {
                using var argsDoc = JsonDocument.Parse(args);
                if (argsDoc.RootElement.ValueKind == JsonValueKind.Object && argsDoc.RootElement.TryGetProperty("path", out var pathProp))
                {
                    path = pathProp.GetString() ?? "";
                }
            }

            switch (toolCall.FunctionName)
            {
                case "list_files":
                    var entries = fileAccess.ListFiles(sandboxRoot, path, userId);
                    return (TextResultLegacy(entries.Count > 0 ? string.Join("\n", entries) : "(папка пуста)"), 0, 0);

                case "read_document":
                    if (totalDocumentCharsSoFar >= MaxTotalDocumentChars)
                    {
                        // Суммарный бюджет на запрос исчерпан — не открываем больше
                        // файлов, а прямо говорим об этом модели, чтобы она подвела
                        // итог по уже прочитанному вместо попытки прочитать всё подряд.
                        return (
                            TextResultLegacy(
                                "Достигнут суммарный лимит объёма прочитанных документов для одного запроса. Дальнейшие файлы не читаются — сформулируй ответ по уже прочитанным документам, при необходимости уточни у пользователя, что именно посмотреть отдельно."
                            ),
                            0,
                            0
                        );
                    }

                    var extraction = await fileAccess.ReadDocumentAsync(sandboxRoot, path, userId);
                    if (!accessedFiles.Contains(path))
                    {
                        accessedFiles.Add(path);
                    }

                    if (extraction.Text is not null)
                    {
                        var truncated = extraction.Text.Length > MaxDocumentCharsPerTool
                            ? extraction.Text[..MaxDocumentCharsPerTool] + "\n\n[...текст обрезан, документ слишком длинный...]"
                            : extraction.Text;
                        return (TextResultLegacy(truncated), truncated.Length, 0);
                    }

                    // Скан без текстового слоя — картинки страниц идут прямо в
                    // результат этого tool-вызова, а не отдельным сообщением.
                    if (totalToolImagesSoFar >= MaxTotalToolImages)
                    {
                        return (
                            TextResultLegacy(
                                "Достигнут суммарный лимит числа страниц-сканов для одного запроса. Этот файл (скан без текстового слоя) не читается — сформулируй ответ по уже прочитанным документам."
                            ),
                            0,
                            0
                        );
                    }

                    var images = extraction.Images;
                    var imageParts = new List<ChatMessageContentPart>
                    {
                        ChatMessageContentPart.CreateTextPart($"Файл — скан без текстового слоя, {images.Count} стр. приложены как изображения:"),
                    };
                    imageParts.AddRange(images.Select(img => ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(img), "image/png")));
                    return (imageParts, 0, images.Count);

                default:
                    return (TextResultLegacy($"Неизвестный инструмент: {toolCall.FunctionName}"), 0, 0);
            }
        }
        catch (PathAccessDeniedException)
        {
            return (TextResultLegacy("Доступ запрещён: путь вне разрешённой папки объекта, либо файл не найден/не поддерживается."), 0, 0);
        }
        catch (Exception ex)
        {
            return (TextResultLegacy($"Ошибка при выполнении инструмента: {ex.Message}"), 0, 0);
        }
    }

    private static IReadOnlyList<ChatMessageContentPart> TextResultLegacy(string text) => [ChatMessageContentPart.CreateTextPart(text)];

    // Сканы без текстового слоя приходят как готовые PNG-страницы (см.
    // FileTextExtractor.ExtractPdfAsync) — добавляем их как image-части того же
    // пользовательского сообщения, а не отдельными сообщениями: модели проще
    // связать текст запроса с картинками, когда они в одном UserChatMessage.
    private static UserChatMessage BuildUserMessageLegacy(string text, IReadOnlyList<byte[]>? images)
    {
        if (images is null || images.Count == 0)
        {
            return new UserChatMessage(text);
        }

        var parts = new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart(text) };
        parts.AddRange(images.Select(img => ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(img), "image/png")));
        return new UserChatMessage(parts);
    }

    private static string ExtractTextLegacy(ChatCompletion completion) => completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;

    private ChatClient CreateLegacyChatClient(string model)
    {
        var baseUrl =
            configuration["Aggregator:BaseUrl"]
            ?? throw new InvalidOperationException("Aggregator:BaseUrl не задан в конфигурации");
        var apiKey =
            Environment.GetEnvironmentVariable("AITUNNEL_API_KEY")
            ?? throw new InvalidOperationException("Переменная окружения AITUNNEL_API_KEY не задана");

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl),
            NetworkTimeout = TimeSpan.FromSeconds(ModelCallTimeoutSeconds),
            RetryPolicy = new ClientRetryPolicy(maxRetries: ModelCallMaxRetries),
        };
        return new ChatClient(model, new ApiKeyCredential(apiKey), options);
    }
}
