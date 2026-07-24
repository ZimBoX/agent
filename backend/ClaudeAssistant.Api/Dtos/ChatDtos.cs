namespace ClaudeAssistant.Api.Dtos;

// conversationId == null — начать новый диалог; иначе продолжить существующий.
public record ChatSendRequest(Guid? ConversationId, string Text);

// Итоговый ответ (авторитетный текст — на случай, если часть стрим-токенов
// потерялась). conversationId/title нужны клиенту, чтобы привязать новый диалог.
public record ChatSendResponse(Guid ConversationId, string Title, string Reply);

public record ChatConversationDto(Guid Id, string Title, DateTimeOffset UpdatedAt);

public record ChatMessageDto(bool IsUser, string Text, DateTimeOffset CreatedAt);
