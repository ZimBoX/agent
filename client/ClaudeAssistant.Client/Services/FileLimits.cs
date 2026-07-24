using System.IO;
using System.Linq;

namespace ClaudeAssistant.Client.Services;

// Те же лимиты, что и на бэкенде (FileUploadLimits). Проверяем на клиенте ДО
// загрузки: иначе при превышении RequestSizeLimit сервер рвёт соединение и
// пользователь видит невнятную «ошибку потока» вместо понятного сообщения.
public static class FileLimits
{
    public const long MaxFileSizeBytes = 20L * 1024 * 1024;
    public const long MaxTotalSizeBytes = 50L * 1024 * 1024;
    public static readonly string[] SupportedExtensions = [".docx", ".xlsx", ".pdf"];

    // Возвращает читаемую ошибку или null, если всё в порядке.
    public static string? Validate(IReadOnlyList<string> paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!SupportedExtensions.Contains(ext))
            {
                return $"Неподдерживаемый файл: {Path.GetFileName(path)} — нужны .docx, .xlsx или .pdf";
            }

            long length;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch
            {
                continue;
            }

            if (length > MaxFileSizeBytes)
            {
                return $"Файл слишком большой: {Path.GetFileName(path)} (макс {MaxFileSizeBytes / 1024 / 1024} МБ)";
            }
            total += length;
        }

        if (total > MaxTotalSizeBytes)
        {
            return $"Суммарный размер файлов превышает лимит ({MaxTotalSizeBytes / 1024 / 1024} МБ)";
        }
        return null;
    }
}
