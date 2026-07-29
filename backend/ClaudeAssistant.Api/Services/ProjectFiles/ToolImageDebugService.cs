using System.Text.RegularExpressions;

namespace ClaudeAssistant.Api.Services.ProjectFiles;

// Отладочный дамп КАРТИНОК, которые инструменты отправляют модели (превью
// document_overview, вырезки render_page, скан-страницы read_document). Когда
// включён флаг Debug:DumpToolImages, каждая такая картинка дублируется в папку
// Debug:DumpDir — чтобы «увидеть глазами модели» ровно то, что она видит.
// Папка отдаётся статикой по /tool-debug (см. Program.cs), только при включённом
// флаге. Файлов держим не больше DumpMaxFiles — старые подчищаем.
public class ToolImageDebugService(IConfiguration configuration, ILogger<ToolImageDebugService> logger)
{
    private const int DumpMaxFiles = 400;

    public bool Enabled => configuration.GetValue("Debug:DumpToolImages", false);

    public string Dir => configuration["Debug:DumpDir"] ?? Path.Combine(Path.GetTempPath(), "tool-image-debug");

    // Записывает картинки на диск с говорящим именем: время + короткий id
    // пользователя + инструмент + метка (лист/регион/файл) + порядковый номер.
    // Расширение — по реальным байтам (jpeg/png). No-op, если флаг выключен.
    public void Dump(Guid userId, string tool, string label, IReadOnlyList<byte[]> images)
    {
        if (!Enabled || images.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Dir);
            var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var user8 = userId.ToString("N")[..8];
            var safeLabel = Sanitize(label);
            for (var i = 0; i < images.Count; i++)
            {
                var ext = SkiaImages.DetectMediaType(images[i]) == "image/png" ? "png" : "jpg";
                var name = images.Count > 1
                    ? $"{ts}_{user8}_{tool}_{safeLabel}_{i + 1}.{ext}"
                    : $"{ts}_{user8}_{tool}_{safeLabel}.{ext}";
                File.WriteAllBytes(Path.Combine(Dir, name), images[i]);
            }

            PruneOld();
        }
        catch (Exception ex)
        {
            // Отладочный дамп никогда не должен ронять реальный запрос.
            logger.LogWarning(ex, "Не удалось сохранить отладочный дамп картинок инструмента {Tool}", tool);
        }
    }

    private void PruneOld()
    {
        var files = Directory.GetFiles(Dir);
        if (files.Length <= DumpMaxFiles)
        {
            return;
        }
        foreach (var f in files.OrderBy(File.GetLastWriteTimeUtc).Take(files.Length - DumpMaxFiles))
        {
            try
            {
                File.Delete(f);
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
