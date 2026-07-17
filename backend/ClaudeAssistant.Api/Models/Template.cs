namespace ClaudeAssistant.Api.Models;

public class Template
{
    public Guid Id { get; set; }
    public required string Title { get; set; }

    // Один шаблон может относиться сразу к нескольким должностям/отделам —
    // например, "Проверка договора" нужна и юридическому, и сметному отделу.
    public List<Role> Roles { get; set; } = [];

    // Системный промпт с плейсхолдером {{document_text}}
    public required string SystemPrompt { get; set; }

    // Идентификатор модели у агрегатора (напр. "claude-sonnet-5", позже — "gpt-4o-mini", "qwen-..." и т.п.)
    public required string Model { get; set; }

    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }

    // Разрешает модели самостоятельно искать/читать файлы внутри выбранного
    // пользователем объекта (см. SandboxedFileAccessService) — для задач вида
    // "собери статистику по объекту", когда нужные документы не прикреплены
    // вручную. По умолчанию выключено для всех шаблонов.
    public bool AllowFileSearch { get; set; }
}
