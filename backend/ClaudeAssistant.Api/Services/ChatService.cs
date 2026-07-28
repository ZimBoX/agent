using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using ClaudeAssistant.Api.Data;
using ClaudeAssistant.Api.Dtos;
using ClaudeAssistant.Api.Hubs;
using ClaudeAssistant.Api.Models;
using ClaudeAssistant.Api.Services.AiCompletion;
using ClaudeAssistant.Api.Services.FileGeneration;
using ClaudeAssistant.Api.Services.ProjectFiles;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAssistant.Api.Services;

// Режим чата — отдельный от основного потока запросов по шаблону. Лёгкая модель
// (Chat:Model, по умолчанию claude-haiku-4.5), обычный диалог без инструментов и
// поиска по файлам, история в БД. Ответ стримится по токенам в SignalR
// (UpdatesHubEvents.ChatToken), плюс возвращается целиком по HTTP как
// авторитетный текст на случай потери части токенов.
public class ChatService(
    IConfiguration configuration,
    AppDbContext db,
    IHubContext<UpdatesHub> hub,
    DocumentGenerationService documentGeneration,
    ILogger<ChatService> logger
)
{
    private const int MaxOutputTokens = 4096;
    private const int TimeoutSeconds = 300;
    private const int MaxRetries = 2;
    private const int TitleMaxLength = 48;
    private const int MaxWebSearchUses = 3;

    // Потолок ходов tool-loop за одно сообщение — в чате инструмент один
    // (create_file), обычно 1-2 хода; ограничение на всякий случай от разгона.
    private const int MaxToolIterations = 5;

    private const string SystemPrompt =
        "Ты — дружелюбный помощник в режиме чата внутреннего корпоративного приложения. "
        + "Отвечай по-русски, ясно и по делу. Если вопрос из строительной/подрядной сферы "
        + "(договоры, сметы, ТЗ, акты) — учитывай эту специфику. "
        + "У тебя есть инструмент веб-поиска (web_search) — используй его, когда нужна "
        + "свежая или актуальная информация из интернета (погода, новости, курсы и т.п.), "
        + "а не отвечай, что доступа к интернету нет. "
        + "Если пользователь просит СДЕЛАТЬ файл — таблицу (Excel) или документ Word — "
        + "используй инструмент create_file, а не вставляй содержимое как текст.";

    // Накопитель клиентского tool_use (create_file) из потока: id, имя и JSON входа,
    // который собирается по кусочкам (input_json_delta).
    private sealed class ToolCallAccum(string id, string name)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public StringBuilder Json { get; } = new();
    }

    private record ChatToolCall(string Id, string Name, IReadOnlyDictionary<string, JsonElement> Input);

    // Временно gemini-3-flash-preview для тестов (раньше claude-haiku-4.5).
    // Эффективное значение задаёт Chat__Model в docker-compose — там тоже заменено.
    private string Model => configuration["Chat:Model"] ?? "gemini-3-flash-preview";

    public async Task<ChatSendResponse> SendAsync(
        Guid userId,
        Guid? conversationId,
        string text,
        string? combinedFileText,
        IReadOnlyList<byte[]>? images,
        string? fileNames,
        CancellationToken cancellationToken
    )
    {
        // Что видит модель — текст пользователя + извлечённый текст файлов; сканы
        // уходят изображениями. Что хранится/показывается в пузыре — текст +
        // компактная пометка о приложенных файлах (без сырого текста файлов).
        var modelText = string.IsNullOrEmpty(combinedFileText)
            ? text
            : string.IsNullOrEmpty(text) ? combinedFileText : $"{text}\n\n{combinedFileText}";
        var displayText = string.IsNullOrEmpty(fileNames)
            ? text
            : string.IsNullOrEmpty(text) ? $"📎 {fileNames}" : $"{text}\n\n📎 {fileNames}";

        var conversation = await LoadOrCreateAsync(userId, conversationId, displayText, cancellationToken);

        // Собираем историю в формат SDK ДО добавления нового сообщения в БД, затем
        // дописываем текущую реплику пользователя (с файлами, если приложены).
        var messages = conversation.Messages
            .OrderBy(m => m.CreatedAt)
            .Select(m => new MessageParam
            {
                Role = m.IsUser ? Anthropic.Models.Messages.Role.User : Anthropic.Models.Messages.Role.Assistant,
                Content = new List<ContentBlockParam> { new TextBlockParam { Text = m.Text } },
            })
            .ToList();
        messages.Add(new MessageParam
        {
            Role = Anthropic.Models.Messages.Role.User,
            Content = BuildUserContent(modelText, images),
        });

        db.ChatMessages.Add(new ChatMessage
        {
            ConversationId = conversation.Id,
            IsUser = true,
            Text = displayText,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);

        var (reply, generatedFiles, usage) = await GenerateAsync(userId, conversation.Id, conversation.Model, messages, cancellationToken);
        logger.LogInformation(
            "Chat usage: input={Input} cacheRead={CacheRead} output={Output} costUsd={Cost}",
            usage.InputTokens,
            usage.CacheReadTokens,
            usage.OutputTokens,
            usage.ProviderCostUsd
        );

        var assistantMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            IsUser = false,
            Text = reply,
            InputTokens = (int)usage.InputTokens,
            CacheReadTokens = (int)usage.CacheReadTokens,
            OutputTokens = (int)usage.OutputTokens,
            ProviderCostUsd = usage.ProviderCostUsd,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ChatMessages.Add(assistantMessage);

        // Сгенерированные файлы сохраняем в БД с привязкой к этому ответу — чтобы их
        // можно было скачать из истории чата (30 дней, потом Content зачищается).
        foreach (var f in generatedFiles)
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
                ChatMessageId = assistantMessage.Id,
            });
        }

        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var files = generatedFiles.Count > 0
            ? generatedFiles.Select(f => new GeneratedFileDto(f.Id, f.FileName, true)).ToList()
            : null;
        return new ChatSendResponse(conversation.Id, conversation.Title, reply, files);
    }

    private async Task<ChatConversation> LoadOrCreateAsync(
        Guid userId,
        Guid? conversationId,
        string firstText,
        CancellationToken cancellationToken
    )
    {
        if (conversationId is { } id)
        {
            var existing = await db.ChatConversations
                .Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }
        }

        var conversation = new ChatConversation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = BuildTitle(firstText),
            Model = Model,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.ChatConversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    private static string BuildTitle(string text)
    {
        var trimmed = text.Trim().ReplaceLineEndings(" ");
        if (trimmed.Length <= TitleMaxLength)
        {
            return trimmed.Length == 0 ? "Новый чат" : trimmed;
        }
        return trimmed[..TitleMaxLength].TrimEnd() + "…";
    }

    // Гоняет ход(ы) диалога: стримит ответ модели по токенам в SignalR и, если
    // модель вызвала create_file, генерирует файл(ы) и продолжает диалог, пока не
    // получит финальный текст. Возвращает полный текст + список созданных файлов.
    //
    // web_search — СЕРВЕРНЫЙ инструмент Anthropic: выполняется внутри одного вызова
    // на стороне агрегатора и в наш tool-loop не попадает. create_file — КЛИЕНТСКИЙ:
    // модель им «останавливается» (stop_reason=tool_use), мы его выполняем и
    // продолжаем. Поэтому цикл управляется наличием клиентских tool_use в ходе.
    private async Task<(string Reply, List<GeneratedFileData> Files, TokenUsage Usage)> GenerateAsync(
        Guid userId,
        Guid conversationId,
        string model,
        List<MessageParam> messages,
        CancellationToken cancellationToken
    )
    {
        var client = CreateClient();
        List<ToolUnion> tools =
        [
            new WebSearchTool20250305 { MaxUses = MaxWebSearchUses },
            DocumentGenerationService.BuildCreateFileTool(),
        ];
        var generatedFiles = new List<GeneratedFileData>();
        var usage = TokenUsage.Empty;
        var lastText = "";

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var parameters = new MessageCreateParams
            {
                Model = model,
                MaxTokens = MaxOutputTokens,
                System = new List<TextBlockParam> { new() { Text = SystemPrompt } },
                Tools = tools,
                Messages = messages,
            };

            var (turnText, toolCalls, turnUsage) = await StreamTurnAsync(client, parameters, userId, conversationId, cancellationToken);
            lastText = turnText;
            usage = usage.Add(turnUsage.InputTokens, turnUsage.CacheReadTokens, turnUsage.OutputTokens, turnUsage.ProviderCostUsd);

            if (toolCalls.Count == 0)
            {
                // Финальный ход — модель ответила текстом без создания файлов.
                var text = string.IsNullOrWhiteSpace(turnText) ? "(пустой ответ)" : turnText;
                return (text, generatedFiles, usage);
            }

            // Дописываем ход ассистента (текст-преамбула + tool_use) и результаты
            // выполнения инструментов, затем крутим следующий ход за финальным текстом.
            var assistantContent = new List<ContentBlockParam>();
            if (!string.IsNullOrEmpty(turnText))
            {
                assistantContent.Add(new TextBlockParam { Text = turnText });
            }
            var toolResults = new List<ContentBlockParam>();
            foreach (var call in toolCalls)
            {
                assistantContent.Add(new ToolUseBlockParam { ID = call.Id, Name = call.Name, Input = call.Input });
                var resultText = ExecuteCreateFile(call, generatedFiles);
                toolResults.Add(new ToolResultBlockParam
                {
                    ToolUseID = call.Id,
                    Content = new List<Block> { new TextBlockParam { Text = resultText } },
                });
            }
            messages.Add(new MessageParam { Role = Anthropic.Models.Messages.Role.Assistant, Content = assistantContent });
            messages.Add(new MessageParam { Role = Anthropic.Models.Messages.Role.User, Content = toolResults });
        }

        // Исчерпан лимит ходов — отдаём последний полученный текст (или заглушку).
        return (string.IsNullOrWhiteSpace(lastText) ? "(пустой ответ)" : lastText, generatedFiles, usage);
    }

    // Один ход: стримит текст по токенам (SignalR) и накапливает клиентские tool_use
    // (create_file) — их вход собирается из input_json_delta. Возвращает текст хода и
    // разобранные вызовы инструментов. При обрыве стрима после части контента —
    // отдаём накопленное; если не пришло вообще ничего — откат на нестримовый вызов.
    private async Task<(string Text, List<ChatToolCall> ToolCalls, TokenUsage Usage)> StreamTurnAsync(
        AnthropicClient client,
        MessageCreateParams parameters,
        Guid userId,
        Guid conversationId,
        CancellationToken cancellationToken
    )
    {
        var sb = new StringBuilder();
        var accums = new List<ToolCallAccum>();
        ToolCallAccum? current = null;

        // Usage читаем из сырого JSON событий (типизированные поля SDK не покрывают
        // добавленный агрегатором `cost`, а часть счётчиков nullable): вход — из
        // message_start, выход и стоимость — из message_delta (кумулятивные, берём
        // последние значения).
        long inputTokens = 0,
            cacheReadTokens = 0,
            outputTokens = 0;
        double costUsd = 0;

        try
        {
            await foreach (var streamEvent in client.Messages.CreateStreaming(parameters, cancellationToken))
            {
                if (streamEvent.TryPickStart(out var msgStart) && TryGetUsageElement(msgStart.RawData, out var startUsage))
                {
                    inputTokens = RawNum(startUsage, "input_tokens") + RawNum(startUsage, "cache_creation_input_tokens");
                    cacheReadTokens = RawNum(startUsage, "cache_read_input_tokens");
                }
                else if (streamEvent.TryPickDelta(out var msgDelta) && TryGetUsageElement(msgDelta.RawData, out var deltaUsage))
                {
                    outputTokens = RawNum(deltaUsage, "output_tokens");
                    // Агрегатор (aitunnel) кладёт ПОЛНЫЙ usage в финальный message_delta,
                    // а не в message_start — поэтому вход/кэш берём и отсюда (если пришли).
                    var deltaInput = RawNum(deltaUsage, "input_tokens") + RawNum(deltaUsage, "cache_creation_input_tokens");
                    var deltaCacheRead = RawNum(deltaUsage, "cache_read_input_tokens");
                    if (deltaInput > 0)
                    {
                        inputTokens = deltaInput;
                    }
                    if (deltaCacheRead > 0)
                    {
                        cacheReadTokens = deltaCacheRead;
                    }
                    var c = RawCost(deltaUsage);
                    if (c > 0)
                    {
                        costUsd = c;
                    }
                }
                else if (streamEvent.TryPickContentBlockStart(out var start))
                {
                    // Начало нового блока: клиентский tool_use (create_file) начинаем
                    // копить; текст и серверные блоки (web_search) — не наши.
                    current = start.ContentBlock.TryPickToolUse(out var tub) ? new ToolCallAccum(tub.ID, tub.Name) : null;
                    if (current is not null)
                    {
                        accums.Add(current);
                    }
                }
                else if (streamEvent.TryPickContentBlockDelta(out var delta))
                {
                    if (delta.Delta.TryPickText(out var textDelta))
                    {
                        sb.Append(textDelta.Text);
                        // Токены отправляем последовательно (await) — сохраняется порядок.
                        await hub.Clients
                            .User(userId.ToString())
                            .SendAsync(UpdatesHubEvents.ChatToken, conversationId, textDelta.Text, cancellationToken);
                    }
                    else if (delta.Delta.TryPickInputJson(out var inputJson))
                    {
                        current?.Json.Append(inputJson.PartialJson);
                    }
                }
                else if (streamEvent.TryPickContentBlockStop(out _))
                {
                    current = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (sb.Length > 0 || accums.Count > 0)
        {
            // Стрим оборвался после части контента (редкий upstream api_error в конце) —
            // отдаём накопленное, не роняя запрос.
            logger.LogWarning(ex, "Стрим чата оборвался после части контента — отдаём накопленное");
            return (sb.ToString(), ParseToolCalls(accums), new TokenUsage(inputTokens, cacheReadTokens, outputTokens, costUsd));
        }
        catch (Exception ex)
        {
            // Совсем ничего не пришло (агрегатор не отдал SSE) — откат на обычный
            // запрос-ответ; текст отдаём одним «токеном», tool_use разбираем из ответа.
            logger.LogWarning(ex, "Стриминг чата не удался — откат на обычный запрос-ответ");
            var response = await client.Messages.Create(parameters, cancellationToken);
            var (text, calls) = ExtractTurn(response);
            if (!string.IsNullOrEmpty(text))
            {
                await hub.Clients
                    .User(userId.ToString())
                    .SendAsync(UpdatesHubEvents.ChatToken, conversationId, text, cancellationToken);
            }
            return (text, calls, UsageFromUsageRaw(response.Usage.RawData));
        }

        return (sb.ToString(), ParseToolCalls(accums), new TokenUsage(inputTokens, cacheReadTokens, outputTokens, costUsd));
    }

    // Достаёт объект usage из сырого JSON события: напрямую (message_delta) или
    // вложенным в message (message_start).
    private static bool TryGetUsageElement(IReadOnlyDictionary<string, JsonElement> eventRaw, out JsonElement usage)
    {
        if (eventRaw.TryGetValue("usage", out usage) && usage.ValueKind == JsonValueKind.Object)
        {
            return true;
        }
        if (
            eventRaw.TryGetValue("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("usage", out usage)
            && usage.ValueKind == JsonValueKind.Object
        )
        {
            return true;
        }
        usage = default;
        return false;
    }

    private static long RawNum(JsonElement usage, string key) =>
        usage.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;

    private static double RawCost(JsonElement usage) =>
        usage.TryGetProperty("cost", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;

    // Строит TokenUsage из RawData объекта usage (нестримовый ответ — ключи лежат
    // прямо в словаре).
    private static TokenUsage UsageFromUsageRaw(IReadOnlyDictionary<string, JsonElement>? usageRaw)
    {
        if (usageRaw is null)
        {
            return TokenUsage.Empty;
        }
        long N(string key) => usageRaw.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;
        var cost = usageRaw.TryGetValue("cost", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
        return new TokenUsage(N("input_tokens") + N("cache_creation_input_tokens"), N("cache_read_input_tokens"), N("output_tokens"), cost);
    }

    private static List<ChatToolCall> ParseToolCalls(List<ToolCallAccum> accums)
    {
        var calls = new List<ChatToolCall>();
        foreach (var a in accums)
        {
            IReadOnlyDictionary<string, JsonElement> input;
            var json = a.Json.ToString();
            try
            {
                input = string.IsNullOrWhiteSpace(json)
                    ? new Dictionary<string, JsonElement>()
                    : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new Dictionary<string, JsonElement>();
            }
            catch (JsonException)
            {
                // Оборвавшийся/битый JSON инструмента — пропускаем этот вызов.
                continue;
            }
            calls.Add(new ChatToolCall(a.Id, a.Name, input));
        }
        return calls;
    }

    // Разбирает нестримовый ответ на текст + клиентские tool_use (для отката).
    private static (string Text, List<ChatToolCall> ToolCalls) ExtractTurn(Message response)
    {
        var sb = new StringBuilder();
        var calls = new List<ChatToolCall>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? textBlock))
            {
                sb.Append(textBlock.Text);
            }
            else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
            {
                calls.Add(new ChatToolCall(toolUse.ID, toolUse.Name, toolUse.Input));
            }
        }
        return (sb.ToString(), calls);
    }

    // Выполняет create_file: генерирует файл, кладёт в эфемерное хранилище,
    // Возвращает модели короткое подтверждение или текст ошибки. Байты несём вверх
    // в generatedFiles — сохранит их SendAsync с привязкой к сообщению чата.
    private string ExecuteCreateFile(ChatToolCall call, List<GeneratedFileData> generatedFiles)
    {
        if (call.Name != "create_file")
        {
            return $"Неизвестный инструмент: {call.Name}";
        }
        try
        {
            var (bytes, fileName, contentType) = documentGeneration.Generate(call.Input);
            generatedFiles.Add(new GeneratedFileData(Guid.NewGuid(), fileName, contentType, bytes));
            return $"Файл «{fileName}» успешно создан и приложен к ответу. Кратко подтверди это пользователю.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось сгенерировать файл через create_file (чат)");
            return $"Не удалось создать файл: {ex.Message}. Проверь входные данные и попробуй ещё раз.";
        }
    }

    private static List<ContentBlockParam> BuildUserContent(string text, IReadOnlyList<byte[]>? images)
    {
        var parts = new List<ContentBlockParam> { new TextBlockParam { Text = text } };
        if (images is not null)
        {
            parts.AddRange(
                images.Select(img => (ContentBlockParam)new ImageBlockParam
                {
                    Source = new Base64ImageSource { MediaType = SkiaImages.DetectMediaType(img), Data = Convert.ToBase64String(img) },
                })
            );
        }
        return parts;
    }

    private AnthropicClient CreateClient()
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
            AuthToken = apiKey,
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            MaxRetries = MaxRetries,
        };
    }

    public async Task<List<ChatConversationDto>> ListConversationsAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.ChatConversations
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ChatConversationDto(c.Id, c.Title, c.UpdatedAt))
            .ToListAsync(cancellationToken);

    public async Task<bool> DeleteConversationAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await db.ChatConversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, cancellationToken);
        if (conversation is null)
        {
            return false;
        }
        // Сообщения удалятся каскадом (FK ChatMessage → ChatConversation, OnDelete Cascade).
        db.ChatConversations.Remove(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<List<ChatMessageDto>?> GetMessagesAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        var owned = await db.ChatConversations.AnyAsync(c => c.Id == conversationId && c.UserId == userId, cancellationToken);
        if (!owned)
        {
            return null;
        }

        return await db.ChatMessages
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatMessageDto(
                m.IsUser,
                m.Text,
                m.CreatedAt,
                db.GeneratedFiles
                    .Where(f => f.ChatMessageId == m.Id)
                    .OrderBy(f => f.CreatedAt)
                    .Select(f => new GeneratedFileDto(f.Id, f.FileName, f.Content != null))
                    .ToList()
            ))
            .ToListAsync(cancellationToken);
    }
}
