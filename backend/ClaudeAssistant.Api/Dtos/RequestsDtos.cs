namespace ClaudeAssistant.Api.Dtos;

// ConversationId — если не null, диалог можно продолжить (POST
// /api/requests/{conversationId}/continue) в течение окна кэша (1 час). null —
// продолжение недоступно (legacy-путь модели без prompt caching).
public record ExecuteResponse(string ResultText, string? AccessedFiles = null, Guid? ConversationId = null);

// Ответ на продолжение диалога. AccessedFiles — файлы, прочитанные именно на этом
// ходу (модель могла дочитать новые документы).
public record ContinueRequest(string Message);

public record ContinueResponse(string ResultText, string? AccessedFiles = null);
