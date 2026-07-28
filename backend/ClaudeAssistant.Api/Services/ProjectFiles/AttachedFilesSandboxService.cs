using ClaudeAssistant.Api.Services.FileTextExtraction;
using Microsoft.AspNetCore.Http;

namespace ClaudeAssistant.Api.Services.ProjectFiles;

// Раскладывает вложенные (загруженные пользователем) файлы во временную папку на
// сервере, чтобы модель читала их теми же инструментами, что и файлы объекта
// (list_files/read_document по частям + render_page с зумом), — вместо инлайна
// всего содержимого в промпт. Нужно для больших пачек: инлайн раздувает запрос
// сверх лимита входа (8 МБ у Anthropic / жёстче у edge-воркера агрегатора).
//
// Папка — sandbox-корень запроса; SandboxedFileAccessService не даёт модели выйти
// за её пределы. Живёт дольше окна диалога (кэш 1 ч), потом чистится свипом.
public class AttachedFilesSandboxService(IConfiguration configuration, ILogger<AttachedFilesSandboxService> logger)
{
    // Порог: пачки не тяжелее этого читаются по-старому инлайном (одним ходом, без
    // латентности tool-loop'а); тяжелее — через инструменты. Держим заметно ниже
    // лимита входа модели, т.к. текст+картинки после извлечения могут ещё подрасти.
    public long InlineMaxBytes => configuration.GetValue("AttachedFiles:InlineMaxBytes", 8L * 1024 * 1024);

    private string CacheDir => configuration["AttachedFiles:CacheDir"] ?? Path.Combine(Path.GetTempPath(), "attached-cache");

    private int RetentionHours => configuration.GetValue("AttachedFiles:RetentionHours", 3);

    // Записывает файлы в новую папку {CacheDir}/{userId}/{guid}/ и возвращает её
    // абсолютный путь (sandbox-корень). Имена санируются и разводятся при коллизии.
    public async Task<string> MaterializeAsync(IReadOnlyList<IFormFile> files, Guid userId, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(CacheDir, userId.ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var name = UniqueSafeName(file.FileName, used);
            var target = Path.Combine(dir, name);
            await using var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await file.CopyToAsync(fs, cancellationToken);
        }

        logger.LogInformation("Вложения ({Count}) разложены во временную песочницу {Dir} (пользователь {UserId})", files.Count, dir, userId);
        return dir;
    }

    // true — путь ведёт внутрь нашего кэша вложений (используется на продолжении
    // диалога, чтобы дописать новые файлы в ту же песочницу, а не в папку объекта).
    public bool IsAttachedSandbox(string sandboxRoot)
    {
        var root = Path.GetFullPath(CacheDir);
        var full = Path.GetFullPath(sandboxRoot);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // Дописывает файлы в уже существующую песочницу (продолжение диалога с новыми
    // вложениями). Имена разводятся с теми, что уже лежат в папке.
    public async Task AddToAsync(string sandboxRoot, IReadOnlyList<IFormFile> files, CancellationToken cancellationToken = default)
    {
        var used = new HashSet<string>(
            Directory.EnumerateFiles(sandboxRoot).Select(Path.GetFileName)!,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var file in files)
        {
            var name = UniqueSafeName(file.FileName, used);
            var target = Path.Combine(sandboxRoot, name);
            await using var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await file.CopyToAsync(fs, cancellationToken);
        }
    }

    // Удаляет песочницы старше RetentionHours (вызывается фоновым свипом). Диалог
    // живёт 1 ч, ретеншен с запасом — чтобы follow-up'ы не наткнулись на пустоту.
    public void SweepExpired()
    {
        var root = CacheDir;
        if (!Directory.Exists(root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddHours(-RetentionHours);
        foreach (var userDir in Directory.EnumerateDirectories(root))
        {
            foreach (var sessionDir in Directory.EnumerateDirectories(userDir))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(sessionDir) < cutoff)
                    {
                        Directory.Delete(sessionDir, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Не удалось удалить устаревшую песочницу вложений {Dir}", sessionDir);
                }
            }
        }
    }

    private static string UniqueSafeName(string original, HashSet<string> used)
    {
        var name = Path.GetFileName(original);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "file";
        }

        // Расширение важно: read_document/render_page решают по нему, как читать.
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = name;
        var i = 1;
        while (!used.Add(candidate))
        {
            candidate = $"{stem} ({i}){ext}";
            i++;
        }
        return candidate;
    }
}
