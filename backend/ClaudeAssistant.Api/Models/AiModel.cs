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

    // true — запросы этой модели идут по НАТИВНОМУ пути Anthropic (/v1/messages);
    // false — по OpenAI-совместимому (/v1/chat/completions). По умолчанию для
    // моделей семейства claude — true, для остальных — false (см. AdminService).
    public bool UseAnthropicWire { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
