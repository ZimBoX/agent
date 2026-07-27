namespace ClaudeAssistant.Api.Dtos;

// Файл, созданный моделью (create_file) и предлагаемый к скачиванию: Id — для
// GET /api/files/{id}, FileName — с расширением (как показать/сохранить).
// Available=false — байты уже зачищены по сроку хранения (30 дней), в истории
// показываем имя с пометкой «файл удалён», без кнопки скачивания.
public record GeneratedFileDto(Guid Id, string FileName, bool Available = true);

// ConversationId — если не null, диалог можно продолжить (POST
// /api/requests/{conversationId}/continue) в течение окна кэша (1 час). null —
// продолжение недоступно (legacy-путь модели без prompt caching).
// Files — файлы, сгенерированные моделью на этом запросе (обычно пусто).
public record ExecuteResponse(
    string ResultText,
    string? AccessedFiles = null,
    Guid? ConversationId = null,
    IReadOnlyList<GeneratedFileDto>? Files = null
);

// Ответ на продолжение диалога. AccessedFiles — файлы, прочитанные именно на этом
// ходу (модель могла дочитать новые документы).
public record ContinueRequest(string Message);

public record ContinueResponse(string ResultText, string? AccessedFiles = null, IReadOnlyList<GeneratedFileDto>? Files = null);
