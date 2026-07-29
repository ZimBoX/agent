using System.Text.RegularExpressions;

namespace ClaudeAssistant.Api.Services.ProjectFiles;

// Отладочный дамп КАРТИНОК, которые инструменты отправляют модели (превью
// document_overview, вырезки render_page, скан-страницы read_document). Когда
// включён флаг Debug:DumpToolImages, каждая такая картинка дублируется в папку
// Debug:DumpDir — чтобы «увидеть глазами модели» ровно то, что она видит.
// Картинки одного запроса группируются в ПОДПАПКУ прогона (см. BeginRun): в
// листинге /tool-debug каждый прогон — отдельная папка. Держим не больше
// KeepRuns последних прогонов — старые подчищаются.
public class ToolImageDebugService(IConfiguration configuration, ILogger<ToolImageDebugService> logger)
{
    private const int KeepRuns = 30;

    // Папка текущего прогона — своя на каждый асинхронный поток запроса (модель
    // singleton, запросы параллельны), поэтому AsyncLocal, а не поле.
    private readonly AsyncLocal<string?> _runDir = new();

    public bool Enabled => configuration.GetValue("Debug:DumpToolImages", false);

    public string Dir => configuration["Debug:DumpDir"] ?? Path.Combine(Path.GetTempPath(), "tool-image-debug");

    // Открывает новый прогон: создаёт подпапку {время}_{юзер}, куда лягут все
    // картинки этого запроса. Вызывается в начале хода (RunTurnNative). No-op при
    // выключенном флаге.
    public void BeginRun(Guid userId)
    {
        if (!Enabled)
        {
            return;
        }
        try
        {
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{userId.ToString("N")[..8]}";
            var runDir = Path.Combine(Dir, name);
            Directory.CreateDirectory(runDir);
            _runDir.Value = runDir;
            PruneOldRuns();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось создать папку отладочного прогона");
        }
    }

    // Записывает картинки текущего прогона с говорящим именем: время + инструмент +
    // метка (лист/регион/файл) + номер. Расширение — по реальным байтам. No-op,
    // если флаг выключен.
    public void Dump(Guid userId, string tool, string label, IReadOnlyList<byte[]> images)
    {
        if (!Enabled || images.Count == 0)
        {
            return;
        }

        try
        {
            // Если BeginRun почему-то не вызывался — кладём в корень (не теряем дамп).
            var target = _runDir.Value ?? Dir;
            Directory.CreateDirectory(target);
            var ts = DateTime.Now.ToString("HHmmss-fff");
            var safeLabel = Sanitize(label);
            for (var i = 0; i < images.Count; i++)
            {
                var ext = SkiaImages.DetectMediaType(images[i]) == "image/png" ? "png" : "jpg";
                var name = images.Count > 1
                    ? $"{ts}_{tool}_{safeLabel}_{i + 1}.{ext}"
                    : $"{ts}_{tool}_{safeLabel}.{ext}";
                File.WriteAllBytes(Path.Combine(target, name), images[i]);
            }
        }
        catch (Exception ex)
        {
            // Отладочный дамп никогда не должен ронять реальный запрос.
            logger.LogWarning(ex, "Не удалось сохранить отладочный дамп картинок инструмента {Tool}", tool);
        }
    }

    private void PruneOldRuns()
    {
        if (!Directory.Exists(Dir))
        {
            return;
        }
        var runs = Directory.GetDirectories(Dir);
        if (runs.Length <= KeepRuns)
        {
            return;
        }
        foreach (var d in runs.OrderBy(Directory.GetLastWriteTimeUtc).Take(runs.Length - KeepRuns))
        {
            try
            {
                Directory.Delete(d, recursive: true);
            }
            catch
            {
                // не критично
            }
        }
    }

    private static string Sanitize(string label)
    {
        var s = Regex.Replace(label, @"[^\p{L}\p{N}._-]+", "-").Trim('-');
        return s.Length > 60 ? s[..60] : (s.Length == 0 ? "img" : s);
    }
}
