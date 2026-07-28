using System.Security.Cryptography;
using System.Text;

namespace ClaudeAssistant.Api.Services.ProjectFiles;

// Дисковый кэш результатов рендера/извлечения (PNG-вырезки, монтажи, постраничный
// текст) — чтобы повторное чтение того же объекта было мгновенным и не гоняло PDFium
// заново по метровым листам. Ключ включает путь+mtime файла, так что при изменении
// файла кэш инвалидируется сам. Лимит по размеру с вытеснением самых старых (LRU по
// времени записи) — диск на сервере скромный.
public class RenderCacheService(IConfiguration configuration, ILogger<RenderCacheService> logger)
{
    private readonly string _dir = ResolveDir(configuration);
    private readonly long _maxBytes = configuration.GetValue("Render:CacheMaxBytes", 1_500_000_000L);
    private readonly object _evictLock = new();

    private static string ResolveDir(IConfiguration configuration)
    {
        var dir = configuration["Render:CacheDir"];
        return string.IsNullOrWhiteSpace(dir) ? Path.Combine(Path.GetTempPath(), "claude-render-cache") : dir;
    }

    public byte[] GetOrCreate(string key, Func<byte[]> factory)
    {
        try
        {
            Directory.CreateDirectory(_dir);
        }
        catch
        {
            return factory(); // кэш недоступен — просто считаем без него
        }

        var file = Path.Combine(_dir, Hash(key) + ".dat");
        if (File.Exists(file))
        {
            try
            {
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // «касание» для LRU
                return File.ReadAllBytes(file);
            }
            catch
            {
                // Битый/недочитанный кэш — перегенерируем ниже.
            }
        }

        var bytes = factory();
        try
        {
            File.WriteAllBytes(file, bytes);
            _ = Task.Run(EvictIfNeeded);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось записать кэш рендера");
        }
        return bytes;
    }

    private void EvictIfNeeded()
    {
        lock (_evictLock)
        {
            try
            {
                var files = new DirectoryInfo(_dir).GetFiles("*.dat");
                var total = files.Sum(f => f.Length);
                if (total <= _maxBytes)
                {
                    return;
                }
                foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    try
                    {
                        total -= f.Length;
                        f.Delete();
                    }
                    catch
                    {
                        // файл мог быть удалён/занят — пропускаем
                    }
                    if (total <= _maxBytes * 8 / 10)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось очистить кэш рендера");
            }
        }
    }

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
