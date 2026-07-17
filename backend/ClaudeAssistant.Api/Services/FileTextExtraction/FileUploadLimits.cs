namespace ClaudeAssistant.Api.Services.FileTextExtraction;

public static class FileUploadLimits
{
    public const long MaxFileSizeBytes = 20 * 1024 * 1024;
    public const long MaxTotalSizeBytes = 50 * 1024 * 1024;
    public const int MaxFileCount = 10;

    public static readonly string[] SupportedExtensions = [".docx", ".xlsx", ".pdf"];
}
