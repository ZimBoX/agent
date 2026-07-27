namespace ClaudeAssistant.Api.Models;

// Одно сообщение в диалоге режима чата. IsUser=true — реплика пользователя,
// иначе ответ модели.
public class ChatMessage
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }
    public ChatConversation? Conversation { get; set; }

    public bool IsUser { get; set; }

    public required string Text { get; set; }

    // Учёт токенов и стоимости заполняется только у ответов модели (IsUser=false).
    // Модель берётся из диалога (ChatConversation.Model). Смысл полей — как в
    // RequestHistoryEntry. ProviderCostUsd — фактическая стоимость от агрегатора ($).
    public int InputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int OutputTokens { get; set; }
    public double ProviderCostUsd { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
