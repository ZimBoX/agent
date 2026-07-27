namespace ClaudeAssistant.Api.Models;

// Файл, созданный моделью через инструмент create_file (xlsx/docx) и сохранённый
// для скачивания из истории. Байты хранятся в БД и через 30 дней зачищаются
// (Content → null) фоновой службой — метаданные (имя) остаются, чтобы в истории
// показать «файл удалён». Привязан к записи основного потока ИЛИ к сообщению чата
// (ровно одно из двух); при удалении родителя запись каскадно удаляется.
public class GeneratedFileRecord
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public required string FileName { get; set; }
    public required string ContentType { get; set; }

    // Содержимое файла. null — байты зачищены по истечении срока хранения (30 дней),
    // но метаданные сохранены для показа в истории.
    public byte[]? Content { get; set; }

    public int SizeBytes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Ровно один из двух родителей заполнен.
    public Guid? RequestHistoryEntryId { get; set; }
    public RequestHistoryEntry? RequestHistoryEntry { get; set; }

    public Guid? ChatMessageId { get; set; }
    public ChatMessage? ChatMessage { get; set; }
}
