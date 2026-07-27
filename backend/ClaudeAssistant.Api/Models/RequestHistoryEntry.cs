namespace ClaudeAssistant.Api.Models;

public enum RequestStatus
{
    Success,
    Failed,
}

public class RequestHistoryEntry
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }

    // Null — запрос со своим произвольным промптом, не по шаблону (см. CustomPromptText).
    public Guid? TemplateId { get; set; }
    public Template? Template { get; set; }

    // Заполнено только когда TemplateId == null — сам текст промпта, который ввёл
    // пользователь. Хранится специально: история подскажет, какие произвольные
    // промпты реально нужны людям, чтобы со временем осознанно оформить
    // популярные варианты в шаблоны (см. PROJECT_PLAN.md, раздел 5).
    public string? CustomPromptText { get; set; }

    // Имена обработанных файлов, через запятую
    public required string FileNames { get; set; }

    // Заполнено только для запросов с поиском по объекту (Template.AllowFileSearch) —
    // относительные пути файлов, которые модель реально открыла через read_document,
    // через запятую. Нужно для прозрачности: пользователь видит, что именно
    // просмотрела модель помимо явно прикреплённых файлов.
    public string? AccessedFiles { get; set; }

    public RequestStatus Status { get; set; }
    public string? ResultText { get; set; }
    public string? ErrorMessage { get; set; }

    // Учёт токенов и стоимости (статистика в админке). Копится по всему запросу,
    // включая продолжения диалога (continue дописывает в ту же запись). Model —
    // фактически использованная модель (для рублёвой ставки). InputTokens — «свежий»
    // вход + запись кэша; CacheReadTokens — чтение из кэша (льготная ставка);
    // OutputTokens — сгенерированный ответ. ProviderCostUsd — фактическая стоимость
    // от агрегатора (поле usage.cost, $), сумма по всем вызовам модели в запросе.
    public string? Model { get; set; }
    public int InputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int OutputTokens { get; set; }
    public double ProviderCostUsd { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
