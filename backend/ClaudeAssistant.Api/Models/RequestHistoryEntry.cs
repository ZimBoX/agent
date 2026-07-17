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

    public DateTimeOffset CreatedAt { get; set; }
}
