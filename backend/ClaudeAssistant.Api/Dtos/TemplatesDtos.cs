namespace ClaudeAssistant.Api.Dtos;

public record TemplateListItem(Guid Id, string Title, bool AllowFileSearch);

public record HistoryEntryResponse(
    Guid Id,
    Guid? TemplateId,
    string TemplateTitle,
    string FileNames,
    string Status,
    string? ResultText,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    string? AccessedFiles
);
