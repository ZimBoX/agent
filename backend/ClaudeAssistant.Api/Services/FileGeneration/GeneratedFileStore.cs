using Microsoft.Extensions.Caching.Memory;

namespace ClaudeAssistant.Api.Services.FileGeneration;

// Ссылка на сгенерированный моделью файл, отдаваемая клиенту: по Id он скачает
// содержимое через GET /api/files/{id}. FileName — с расширением, как показать и
// как сохранить.
public record GeneratedFileRef(Guid Id, string FileName);

// Сгенерированный файл в памяти: содержимое + метаданные + владелец (скачать
// может только тот же пользователь). Держится с TTL — файл эфемерный, скачивается
// разово в рамках ответа, не хранится в БД.
public record StoredGeneratedFile(Guid UserId, string FileName, string ContentType, byte[] Bytes);

// Временное хранилище сгенерированных файлов (docx/xlsx). Ровно как ConversationStore:
// Singleton поверх IMemoryCache со скользящим TTL. Файл живёт час после создания —
// достаточно, чтобы пользователь успел нажать «Скачать» на пришедшем ответе; дальше
// он не нужен (при желании модель сгенерирует заново).
public class GeneratedFileStore(IMemoryCache cache)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private static string Key(Guid id) => $"genfile:{id}";

    public GeneratedFileRef Save(Guid userId, string fileName, string contentType, byte[] bytes)
    {
        var id = Guid.NewGuid();
        cache.Set(
            Key(id),
            new StoredGeneratedFile(userId, fileName, contentType, bytes),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl }
        );
        return new GeneratedFileRef(id, fileName);
    }

    // Возвращает файл только владельцу — чужой Id (даже угаданный) не отдаётся.
    public StoredGeneratedFile? Get(Guid userId, Guid id) =>
        cache.TryGetValue(Key(id), out StoredGeneratedFile? file) && file is not null && file.UserId == userId
            ? file
            : null;
}
