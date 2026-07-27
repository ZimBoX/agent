using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using ClaudeAssistant.Api.Data;
using ClaudeAssistant.Api.Dtos;
using ClaudeAssistant.Api.Hubs;
using ClaudeAssistant.Api.Models;
using ClaudeAssistant.Api.Services.FileGeneration;
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
    GeneratedFileStore generatedFileStore,
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

    private string Model => configuration["Chat:Model"] ?? "claude-haiku-4.5";

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

        var (reply, generatedFiles) = await GenerateAsync(userId, conversation.Id, conversation.Model, messages, cancellationToken);

        db.ChatMessages.Add(new ChatMessage
        {
            ConversationId = conversation.Id,
            IsUser = false,
            Text = reply,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // Сгенерированные файлы эфемерны (TTL 1 час, не в БД) — отдаём их только в
        // этом ответе; при перечитывании истории кнопок скачивания уже не будет.
        var files = generatedFiles.Count > 0
            ? generatedFiles.Select(f => new GeneratedFileDto(f.Id, f.FileName)).ToList()
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
    private async Task<(string Reply, List<GeneratedFileRef> Files)> GenerateAsync(
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
        var generatedFiles = new List<GeneratedFileRef>();
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

            var (turnText, toolCalls) = await StreamTurnAsync(client, parameters, userId, conversationId, cancellationToken);
            lastText = turnText;

            if (toolCalls.Count == 0)
            {
                // Финальный ход — модель ответила текстом без создания файлов.
                var text = string.IsNullOrWhiteSpace(turnText) ? "(пустой ответ)" : turnText;
                return (text, generatedFiles);
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
                var resultText = ExecuteCreateFile(call, userId, generatedFiles);
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
        return (string.IsNullOrWhiteSpace(lastText) ? "(пустой ответ)" : lastText, generatedFiles);
    }

    // Один ход: стримит текст по токенам (SignalR) и накапливает клиентские tool_use
    // (create_file) — их вход собирается из input_json_delta. Возвращает текст хода и
    // разобранные вызовы инструментов. При обрыве стрима после части контента —
    // отдаём накопленное; если не пришло вообще ничего — откат на нестримовый вызов.
    private async Task<(string Text, List<ChatToolCall> ToolCalls)> StreamTurnAsync(
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

        try
        {
            await foreach (var streamEvent in client.Messages.CreateStreaming(parameters, cancellationToken))
            {
                if (streamEvent.TryPickContentBlockStart(out var start))
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
            return (sb.ToString(), ParseToolCalls(accums));
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
            return (text, calls);
        }

        return (sb.ToString(), ParseToolCalls(accums));
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
    // регистрирует ссылку. Модели возвращаем короткое подтверждение или текст ошибки.
    private string ExecuteCreateFile(ChatToolCall call, Guid userId, List<GeneratedFileRef> generatedFiles)
    {
        if (call.Name != "create_file")
        {
            return $"Неизвестный инструмент: {call.Name}";
        }
        try
        {
            var (bytes, fileName, contentType) = documentGeneration.Generate(call.Input);
            var reference = generatedFileStore.Save(userId, fileName, contentType, bytes);
            generatedFiles.Add(reference);
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
                    Source = new Base64ImageSource { MediaType = "image/png", Data = Convert.ToBase64String(img) },
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
            .Select(m => new ChatMessageDto(m.IsUser, m.Text, m.CreatedAt))
            .ToListAsync(cancellationToken);
    }
}
