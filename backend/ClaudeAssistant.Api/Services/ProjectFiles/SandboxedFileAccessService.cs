using ClaudeAssistant.Api.Services.FileTextExtraction;

namespace ClaudeAssistant.Api.Services.ProjectFiles;

public class PathAccessDeniedException(string requestedPath) : Exception($"Доступ к пути запрещён: {requestedPath}");

// Одно правило трансляции UNC-пути (как его шлёт Windows-клиент) в точку
// монтирования шары на Linux-хосте бэкенда. Задаётся в конфиге
// ProjectFiles:UncMappings (см. docker-compose на сервере).
public sealed record UncMapping
{
    public string Unc { get; init; } = "";
    public string Local { get; init; } = "";
}

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

    private IReadOnlyList<UncMapping> UncMappings =>
        configuration.GetSection("ProjectFiles:UncMappings").Get<List<UncMapping>>() ?? [];

    // Клиент (Windows) присылает папку-объект в UNC-форме (\\сервер\шара\...,
    // см. NetworkPathResolver на клиенте). Бэкенд на Linux такой путь напрямую не
    // откроет — транслируем известные UNC-префиксы в точки монтирования шары. Если
    // ни один префикс не подошёл (в т.ч. на Windows-dev, где маппингов нет) — путь
    // возвращается как есть; там нативный UNC работает сам.
    private string TranslateUncPath(string path)
    {
        foreach (var mapping in UncMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Unc) || string.IsNullOrWhiteSpace(mapping.Local))
            {
                continue;
            }

            var prefix = mapping.Unc.TrimEnd('\\', '/');
            var localRoot = mapping.Local.TrimEnd('/');

            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return localRoot;
            }

            // Сравнение с завершающим "\" — чтобы префикс "\\host\share" не
            // проглотил "\\host\share-other".
            if (path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var rest = path[prefix.Length..].Replace('\\', '/');
                return localRoot + rest;
            }
        }

        return path;
    }

    // Проверяет и канонизирует папку, которую клиент прислал как объект — и,
    // если задан ProjectFiles:Root, что она физически внутри него.
    public string? ValidateRoot(string absolutePath, Guid userId)
    {
        // Сначала UNC→mount (см. TranslateUncPath) — иначе Path.GetFullPath на Linux
        // примет "\\host\share\..." за странный относительный путь (обратный слэш там
        // обычный символ имени, а не разделитель).
        var translated = TranslateUncPath(absolutePath);

        string full;
        try
        {
            full = Path.GetFullPath(translated);
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
        // Рядом с файлом показываем размер — модель по нему прикидывает объём и решает,
        // что читать (у чтения есть суммарный бюджет, см. ChatCompletionService).
        entries.AddRange(
            Directory.GetFiles(dir).Select(f => $"{Path.GetFileName(f)} ({FormatFileSize(new FileInfo(f).Length)})")
        );
        return entries;
    }

    // Отрезает хвостовую аннотацию размера вида « (2.3 МБ)», которую добавляет
    // ListFiles — на случай если модель скопировала её в путь. Строго по формату
    // ListFiles, чтобы не задеть легитимные имена со скобками.
    private static string StripSizeAnnotation(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path.Trim(), @"\s*\(\d[\d\s.,]*(?:Б|КБ|МБ)\)$", "").TrimEnd();

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} Б";
        }
        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} КБ";
        }
        return $"{bytes / (1024.0 * 1024):0.#} МБ";
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

    // Валидирует, что путь ведёт к существующему .pdf внутри папки объекта, и
    // возвращает абсолютный путь для рендера/поиска (PDFium/PdfPig читают лениво по
    // пути — весь файл в память не грузим, поэтому лимита размера здесь нет).
    public string ResolvePdfForRender(string sandboxRoot, string relativePath, Guid userId)
    {
        var path = ResolveFile(sandboxRoot, relativePath, userId);
        if (Path.GetExtension(path).ToLowerInvariant() != ".pdf")
        {
            logger.LogWarning("Рендер запрошен для не-PDF: {Path} (пользователь {UserId})", path, userId);
            throw new PathAccessDeniedException(relativePath);
        }
        return path;
    }

    private string ResolveFile(string sandboxRoot, string relativePath, Guid userId)
    {
        // list_files показывает файлы с размером в скобках («договор.pdf (2.3 МБ)»);
        // модель иногда копирует путь целиком — отрезаем аннотацию размера, чтобы
        // файл всё равно нашёлся.
        relativePath = StripSizeAnnotation(relativePath);
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
