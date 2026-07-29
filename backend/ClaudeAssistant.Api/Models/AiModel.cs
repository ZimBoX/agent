namespace ClaudeAssistant.Api.Models;

// Управляемый в админке список моделей агрегатора, доступных АДМИНУ для выбора в
// клиенте (подбор баланса цена/качество). Обычные пользователи селектор не видят
// и работают на дефолтной модели. ModelId — точный идентификатор у агрегатора
// (напр. "qwen3.7-flash", "gemini-3-flash-preview", "claude-sonnet-5").
public class AiModel
{
    public Guid Id { get; set; }
    public required string ModelId { get; set; }
    public required string DisplayName { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
