using ClaudeAssistant.Api.Services.FileTextExtraction;

namespace ClaudeAssistant.Api.Services.ProjectFiles;

public class PathAccessDeniedException(string requestedPath) : Exception($"Доступ к пути запрещён: {requestedPath}");

// Единственная точка доступа к файлам "объекта" — папки, которую клиент передаёт
// как sandbox-корень конкретного запроса (либо папка, содержащая прикреплённый
// файл, либо папка, по которой кликнули правой кнопкой в Проводнике — выбор
// делает сам клиент, см. MainWindow.GetCurrentObjectRoot). Модель (через
// tool-calling, см. ChatCompletionService.CompleteWithFileSearchAsync) сама
// решает, что ей посмотреть внутри этого корня, поэтому весь контроль
// безопасности живёт здесь, а не в вызывающем коде. Только чтение, никогда —
// запись/удаление; выход за пределы корня (в т.ч. через "..", абсолютные пути) —
// отказ с логированием.
public class SandboxedFileAccessService(IConfiguration configuration, FileTextExtractor extractor, ILogger<SandboxedFileAccessService> logger)
{
    // Необязательная дополнительная защита: если задан, папка-корень обязана
    // физически лежать внутри него. Не задан — доверяем ровно тому, что уже
    // разрешил Windows (файл выбран через системный диалог или папка — через ПКМ
    // в Проводнике), без дополнительного сужения.
    private string? ConfiguredRoot => configuration["ProjectFiles:Root"];

    // Проверяет и канонизирует папку, которую клиент прислал как объект — и,
    // если задан ProjectFiles:Root, что она физически внутри него.
    public string? ValidateRoot(string absolutePath, Guid userId)
    {
        string full;
        try
        {
            full = Path.GetFullPath(absolutePath);
        }
        catch (Exception)
        {
            logger.LogWarning("Отказ: некорректный путь к папке-объекту {Path} (пользователь {UserId})", absolutePath, userId);
            return null;
        }

        if (!Directory.Exists(full))
        {
            logger.LogWarning("Отказ: папка-объект не найдена {Path} (пользователь {UserId})", full, userId);
            return null;
        }

        var configuredRoot = ConfiguredRoot;
        if (!string.IsNullOrWhiteSpace(configuredRoot) && Directory.Exists(configuredRoot))
        {
            var root = Path.GetFullPath(configuredRoot);
            var rootWithSeparator = root + Path.DirectorySeparatorChar;
            if (full != root && !full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Отказ: папка-объект вне ProjectFiles:Root. Запрошено={Path}, корень={Root} (пользователь {UserId})",
                    full,
                    root,
                    userId
                );
                return null;
            }
        }

        return full;
    }

    // relativePath — может быть пустым (корень объекта). Возвращает как подпапки
    // (с завершающим "/"), так и файлы, отдельно не различая — для модели это
    // просто список того, что можно передать дальше в list_files/read_document.
    public IReadOnlyList<string> ListFiles(string sandboxRoot, string relativePath, Guid userId)
    {
        var dir = ResolveDirectory(sandboxRoot, relativePath, userId);

        var entries = new List<string>();
        entries.AddRange(Directory.GetDirectories(dir).Select(d => Path.GetFileName(d) + "/"));
        entries.AddRange(Directory.GetFiles(dir).Select(Path.GetFileName)!);
        return entries;
    }

    // Возвращает ExtractionResult как есть (текст либо картинки сканов без
    // текстового слоя, см. FileTextExtractor) — вызывающий код (ChatCompletionService)
    // сам решает, как упаковать это в результат tool-вызова.
    public async Task<ExtractionResult> ReadDocumentAsync(string sandboxRoot, string relativePath, Guid userId)
    {
        var filePath = ResolveFile(sandboxRoot, relativePath, userId);

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (!FileUploadLimits.SupportedExtensions.Contains(extension))
        {
            logger.LogWarning("Отказ в чтении файла с неподдерживаемым расширением: {Path} (пользователь {UserId})", filePath, userId);
            throw new PathAccessDeniedException(relativePath);
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > FileUploadLimits.MaxFileSizeBytes)
        {
            logger.LogWarning("Отказ в чтении файла превышающего лимит размера: {Path} ({Size} байт, пользователь {UserId})", filePath, fileInfo.Length, userId);
            throw new PathAccessDeniedException(relativePath);
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await extractor.ExtractAsync(stream, filePath);

        logger.LogInformation("Прочитан файл объекта: {Path} (пользователь {UserId})", filePath, userId);
        return result;
    }

    private string ResolveDirectory(string sandboxRoot, string relativePath, Guid userId)
    {
        var resolved = Resolve(sandboxRoot, relativePath, userId);
        if (!Directory.Exists(resolved))
        {
            logger.LogWarning("Запрошенная папка не найдена: {Path} (пользователь {UserId})", resolved, userId);
            throw new PathAccessDeniedException(relativePath);
        }
        return resolved;
    }

    private string ResolveFile(string sandboxRoot, string relativePath, Guid userId)
    {
        var resolved = Resolve(sandboxRoot, relativePath, userId);
        if (!File.Exists(resolved))
        {
            logger.LogWarning("Запрошенный файл не найден: {Path} (пользователь {UserId})", resolved, userId);
            throw new PathAccessDeniedException(relativePath);
        }
        return resolved;
    }

    // Канонизирует относительный путь внутри sandboxRoot (уже провалидированного
    // ValidateRoot) и проверяет, что результат физически остаётся внутри него.
    // Сравнение — с завершающим разделителем на конце корня, иначе "C:\Root"
    // пропустил бы и "C:\Root-evil".
    private string Resolve(string sandboxRoot, string relativePath, Guid userId)
    {
        var rootWithSeparator = sandboxRoot + Path.DirectorySeparatorChar;
        var combined = string.IsNullOrEmpty(relativePath) ? sandboxRoot : Path.GetFullPath(Path.Combine(sandboxRoot, relativePath));

        if (combined != sandboxRoot && !combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Отказ: попытка выйти за пределы папки объекта. Корень={Root}, запрошенный путь={RelativePath}, разрешённый путь={Resolved} (пользователь {UserId})",
                sandboxRoot,
                relativePath,
                combined,
                userId
            );
            throw new PathAccessDeniedException(relativePath);
        }

        return combined;
    }
}
