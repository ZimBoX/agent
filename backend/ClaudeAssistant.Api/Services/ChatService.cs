using System.Text;
using Anthropic;
using Anthropic.Models.Messages;
using ClaudeAssistant.Api.Data;
using ClaudeAssistant.Api.Dtos;
using ClaudeAssistant.Api.Hubs;
using ClaudeAssistant.Api.Models;
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
    ILogger<ChatService> logger
)
{
    private const int MaxOutputTokens = 4096;
    private const int TimeoutSeconds = 300;
    private const int MaxRetries = 2;
    private const int TitleMaxLength = 48;
    private const int MaxWebSearchUses = 3;

    private const string SystemPrompt =
        "Ты — дружелюбный помощник в режиме чата внутреннего корпоративного приложения. "
        + "Отвечай по-русски, ясно и по делу. Если вопрос из строительной/подрядной сферы "
        + "(договоры, сметы, ТЗ, акты) — учитывай эту специфику. "
        + "У тебя есть инструмент веб-поиска (web_search) — используй его, когда нужна "
        + "свежая или актуальная информация из интернета (погода, новости, курсы и т.п.), "
        + "а не отвечай, что доступа к интернету нет.";

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

        var reply = await GenerateAsync(userId, conversation.Id, conversation.Model, messages, cancellationToken);

        db.ChatMessages.Add(new ChatMessage
        {
            ConversationId = conversation.Id,
            IsUser = false,
            Text = reply,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return new ChatSendResponse(conversation.Id, conversation.Title, reply);
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

    // Стримит ответ модели по токенам в SignalR и возвращает полный текст. При
    // сбое стриминга (агрегатор мог не поддержать SSE на нативном маршруте)
    // откатывается на обычный запрос-ответ и отдаёт весь текст одним куском.
    private async Task<string> GenerateAsync(
        Guid userId,
        Guid conversationId,
        string model,
        List<MessageParam> messages,
        CancellationToken cancellationToken
    )
    {
        var client = CreateClient();
        // web_search — серверный инструмент Anthropic: агрегатор его поддерживает
        // (как и в основном потоке, см. ChatCompletionService.RunTurnNative).
        // Выполняется внутри одного вызова, отдельного tool-loop не требует.
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxOutputTokens,
            System = new List<TextBlockParam> { new() { Text = SystemPrompt } },
            Tools = new List<ToolUnion> { new WebSearchTool20250305 { MaxUses = MaxWebSearchUses } },
            Messages = messages,
        };

        var sb = new StringBuilder();
        try
        {
            await foreach (var streamEvent in client.Messages.CreateStreaming(parameters, cancellationToken))
            {
                if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var textDelta))
                {
                    sb.Append(textDelta.Text);
                    // Токены отправляем последовательно (await) — так сохраняется порядок.
                    await hub.Clients
                        .User(userId.ToString())
                        .SendAsync(UpdatesHubEvents.ChatToken, conversationId, textDelta.Text, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (sb.Length > 0)
        {
            // Стрим оборвался ПОСЛЕ того, как часть/весь текст уже пришёл (например,
            // редкий upstream api_error в самом конце ответа с web_search) — отдаём
            // накопленное как ответ, не роняя запрос.
            logger.LogWarning(ex, "Стрим чата оборвался после части текста — отдаём накопленное");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            // Совсем ничего не пришло (агрегатор не отдал SSE) — откат на обычный
            // запрос-ответ, ответ отдаём одним "токеном".
            logger.LogWarning(ex, "Стриминг чата не удался — откат на обычный запрос-ответ");
            var response = await client.Messages.Create(parameters, cancellationToken);
            var full = ExtractText(response);
            await hub.Clients
                .User(userId.ToString())
                .SendAsync(UpdatesHubEvents.ChatToken, conversationId, full, cancellationToken);
            return full;
        }

        var text = sb.ToString();
        return string.IsNullOrWhiteSpace(text) ? "(пустой ответ)" : text;
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

    private static string ExtractText(Message response)
    {
        var sb = new StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? textBlock))
            {
                sb.Append(textBlock.Text);
            }
        }
        var text = sb.ToString();
        return string.IsNullOrWhiteSpace(text) ? "(пустой ответ)" : text;
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
