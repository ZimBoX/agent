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

    public DateTimeOffset CreatedAt { get; set; }
}
