namespace ClaudeAssistant.Api.Models;

// Диалог «режима чата» — отдельного от основного потока запросов по шаблону.
// Общается с лёгкой моделью (Chat:Model, по умолчанию claude-haiku-4.5), история
// хранится в БД (в отличие от основных диалогов, которые живут в памяти по TTL
// prompt-кэша). Сообщения подгружаются целиком при каждом ходе — для короткого
// чата этого достаточно.
public class ChatConversation
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    // Заголовок для списка — берётся из первого сообщения пользователя (обрезанный).
    public required string Title { get; set; }

    // Модель, которой отвечает этот чат (на случай смены дефолта — старые диалоги
    // сохраняют свою).
    public required string Model { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ChatMessage> Messages { get; set; } = [];
}
