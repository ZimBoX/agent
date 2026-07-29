using Anthropic.Models.Messages;
using Microsoft.Extensions.Caching.Memory;

namespace ClaudeAssistant.Api.Services.AiCompletion;

// Живое состояние продолжаемого диалога. Хранит нативные Anthropic-типы
// (MessageParam/TextBlockParam) — это внутренняя деталь native-пути
// ChatCompletionService, наружу (в контроллер) не протекает: контроллер оперирует
// только conversationId.
public class ConversationState
{
    public required string Model { get; init; }

    // true — диалог идёт по OpenAI-совместимому пути (тогда история в OpenAiMessages,
    // а SystemBlocks/Messages пустые). false — нативный Anthropic путь.
    public bool UseOpenAiWire { get; init; }

    // System-блоки уже с cache_control (1h) — один и тот же префикс на весь диалог.
    public required List<TextBlockParam> SystemBlocks { get; init; }

    // Растущая история: user/assistant/tool-результаты. Прочитанные документы
    // лежат здесь как tool-результаты и переиспользуются из кэша на follow-up'ах.
    public required List<MessageParam> Messages { get; init; }

    // История OpenAI-пути (role/content/tool_calls как словари) — первый элемент
    // system-сообщение. Растёт на follow-up'ах, как и Messages у нативного пути.
    public List<object>? OpenAiMessages { get; set; }

    // Папка-объект для поиска по файлам (tool-loop). null — режим прикреплённых
    // файлов (текст документа уже в SystemBlocks, инструменты не нужны).
    public string? SandboxRoot { get; init; }

    public required Guid UserId { get; init; }

    // Запись истории (RequestHistory), к которой относится этот диалог — чтобы
    // на каждом follow-up дописывать в неё полный тред (диалог сохраняется в БД,
    // а не только в памяти). Проставляется контроллером после создания записи.
    public Guid? HistoryEntryId { get; set; }
}

// Держит продолжаемые диалоги в памяти со скользящим TTL = окно кэша Anthropic
// (1 час). По истечении диалог исчезает: продолжить нельзя, т.к. контекст уже
// выпал из кэша и дочитывать документы заново дорого — это осознанное
// "ограниченное время" сессии, а не баг. Singleton: состояние переживает
// отдельные HTTP-запросы (ChatCompletionService — Scoped, поэтому стор отдельно).
public class ConversationStore(IMemoryCache cache)
{
    // Совпадает с ttl:"1h" на cache_control (см. ChatCompletionService). Держать
    // диалог дольше кэша смысла нет — follow-up после протухания кэша всё равно
    // оплачивался бы по полной.
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private static string Key(Guid conversationId) => $"conversation:{conversationId}";

    // Каждый ход диалога перезаписывает состояние (Messages растёт) с новым
    // абсолютным TTL — то есть окно фактически продлевается при активности,
    // как и сам кэш Anthropic, который освежается при каждом чтении.
    public void Save(Guid conversationId, ConversationState state) =>
        cache.Set(Key(conversationId), state, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl });

    public ConversationState? Get(Guid conversationId) =>
        cache.TryGetValue(Key(conversationId), out ConversationState? state) ? state : null;
}
