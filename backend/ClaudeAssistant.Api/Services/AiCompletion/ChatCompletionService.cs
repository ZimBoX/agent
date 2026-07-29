using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using ClaudeAssistant.Api.Services.FileGeneration;
using ClaudeAssistant.Api.Services.FileTextExtraction;
using ClaudeAssistant.Api.Services.ProjectFiles;
using OpenAI;
using OpenAI.Chat;

namespace ClaudeAssistant.Api.Services.AiCompletion;

// Учёт токенов и фактической стоимости за весь ход (сумма по всем вызовам модели,
// включая agentic tool-loop). InputTokens — «свежий» вход + запись кэша;
// CacheReadTokens — чтение из кэша; OutputTokens — ответ; ProviderCostUsd —
// стоимость от агрегатора (поле usage.cost, $).
public record struct TokenUsage(long InputTokens, long CacheReadTokens, long OutputTokens, double ProviderCostUsd)
{
    public static readonly TokenUsage Empty = default;

    public TokenUsage Add(long input, long cacheRead, long output, double costUsd) =>
        new(InputTokens + input, CacheReadTokens + cacheRead, OutputTokens + output, ProviderCostUsd + costUsd);
}

// GeneratedFiles — файлы, созданные моделью через инструмент create_file на этом
// ходу (docx/xlsx), которые клиент предложит скачать. Пусто, если модель файлов не
// делала. Usage — токены/стоимость за этот ход.
public record AgenticCompletionResult(string ResultText, List<string> AccessedFiles, List<GeneratedFileData> GeneratedFiles, TokenUsage Usage);

// Диалог не найден или истёк (окно кэша, 1 час). Контроллер отвечает 410 —
// продолжать нельзя, контекст уже выпал из кэша.
public class ConversationNotFoundException() : Exception("Диалог не найден или его время истекло");

// Два полных, независимых пути к модели в одном классе — намеренно, а не через
// стирание старого кода. Нативный (Anthropic SDK, напрямую /v1/messages) — новый,
// основной, даёт доступ к prompt caching, которого у OpenAI-совместимого маршрута
// нет (cache_control не выражается в OpenAI wire-формате). Legacy (OpenAI SDK) —
// прежняя, проверенная в бою реализация, оставлена нетронутой на случай, если
// нативный путь поведёт себя неожиданно на реальной нагрузке: переключение —
// это Aggregator:UseNativeSdk=false (можно через переменную окружения
// Aggregator__UseNativeSdk, без пересборки) и рестарт процесса, а не git revert
// и повторный деплой.
public class ChatCompletionService(
    IConfiguration configuration,
    SandboxedFileAccessService fileAccess,
    ConversationStore conversationStore,
    DocumentGenerationService documentGeneration,
    DrawingRenderService drawingRender,
    ToolImageDebugService toolImageDebug,
    ILogger<ChatCompletionService> logger
)
{
    // Ограничивает число обращений модели к инструментам за один запрос — иначе
    // модель теоретически может уйти в цикл list_files/read_document без остановки,
    // раздувая и время ответа, и стоимость запроса. 8 оказалось мало на практике:
    // реальный объект пользователя потребовал 18 read_document (договор, допсоглашение,
    // больше десятка АОСР/ведомостей) — упёрлись в лимит на середине, что и привело
    // к пустому ответу (см. IMPLEMENTATION_CHECKLIST.md, Этап 11).
    private const int MaxToolIterations = 20;

    // Сколько текста одного документа модель получает за ОДИН вызов read_document —
    // это размер "страницы" пагинации, а не жёсткая обрезка: если файл длиннее,
    // модель может дочитать остаток, вызвав read_document с тем же path и
    // offset=<конец предыдущего куска> (см. BuildPaginatedDocumentText). Так целый
    // договор читается целиком по частям, но только когда это осознанно нужно —
    // а не вываливается в контекст сразу. Общий предохранитель от злоупотребления —
    // MaxTotalDocumentChars ниже.
    private const int MaxDocumentCharsPerTool = 20_000;

    // Суммарный бюджет текста документов на весь запрос (across всех read_document) —
    // отдельная защита от объектов с большим числом файлов, где даже уменьшенный
    // лимит на файл в сумме всё равно раздувает контекст до неприемлемых размеров
    // (см. тот же инцидент — 18 файлов почти по лимиту каждый).
    private const int MaxTotalDocumentChars = 150_000;

    // Суммарный потолок отрендеренных за запрос картинок (сканы read_document +
    // превью document_overview + вырезки render_page) — защита от разгона стоимости.
    // Это НАКОПИТЕЛЬНЫЙ счётчик за весь запрос: он не связан с 8-МБ лимитом входа
    // (за него отвечает PruneOldImages — скользящее окно последних сообщений),
    // поэтому его можно держать щедрым. На ОГРОМНЫХ проектах (сотни листов, десятки
    // чертежей) модели нужно 2-3 обзорных монтажа + по вырезке на каждую таблицу
    // объёмов — при потолке 50 это выгорало за ~5 итераций и наглухо блокировало
    // vision до конца запроса (старые картинки к тому моменту уже выброшены пруном →
    // модель оставалась без глаз и возвращала пустой ответ). Вырезки региона лёгкие,
    // так что реальная стоимость упирается в MaxToolIterations, а не в это число.
    private const int MaxTotalToolImages = 200;

    // Сколько последних сообщений сохраняют картинки в запросе к модели. Картинки
    // (превью/вырезки чертежей) тяжёлые и накапливаются в истории — если слать их все,
    // запрос раздувается: помимо лимита входа Anthropic (8 МБ) у edge-воркера
    // агрегатора (Cloudflare) потолок ЖЁСТЧЕ — на ~5 МБ тела он падал с «error code
    // 1102: Worker exceeded resource limits» ещё до Anthropic. Держим окно узким: 2 =
    // последний ход с картинками (assistant tool_use + user tool_result) — модель
    // видит то, что только что запросила, а цифры со старых листов уже перенесла в
    // текст. Картинки из более старых сообщений заменяются текст-заглушкой.
    private const int KeepImageWindowMessages = 2;

    // Максимум обращений к web_search за один вызов модели (серверный инструмент
    // Anthropic сам себя ограничивает этим числом). Каждый поиск стоит денег
    // (~2 ₽ на этом агрегаторе), поэтому лимит скромный — модель обычно и так
    // ищет 1-2 раза, но потолок защищает от разгона стоимости.
    private const int MaxWebSearchUses = 3;

    // История этих двух констант — два противоположных реальных инцидента подряд:
    // 1) SDK по умолчанию ждёт 100с на попытку и делает несколько повторов — при
    //    настоящем затыке на стороне агрегатора это растягивало единственный вызов
    //    на 5-7 минут молчания без единой ошибки ("зависший" запрос).
    // 2) Первая правка (60с, 1 повтор) оказалась СЛИШКОМ короткой: на реальном
    //    большом объекте модель ответила успешно за 99с (68k входных токенов,
    //    подтверждено панелью агрегатора — токены и деньги списаны), но клиент
    //    получил таймаут раньше, чем пришёл ответ. Хуже того — автоповтор в таком
    //    случае не отменяет уже идущую на стороне агрегатора генерацию, а значит
    //    рискует задвоить списание за один и тот же дорогой запрос.
    // Баланс: таймаут увеличен с запасом под большие agentic-запросы (see
    // MaxTotalDocumentChars/MaxToolIterations выше), повторов больше нет —
    // лучше один раз честно подождать дольше, чем тихо платить дважды.
    // Поднято с 240с: теперь пользователь видит живой статус обработки
    // (см. IProgress<string> status → SignalR RequestStatus), поэтому долгий
    // запрос — это не "завис", а видимый прогресс. Таймаут оставлен как страховка
    // от реально мёртвого вызова, но щедрый (15 мин), чтобы не рвать легитимные
    // большие agentic-запросы (много файлов + веб-поиск). Повторов по-прежнему
    // нет — чтобы не задваивать оплату уже идущей генерации.
    private const int ModelCallTimeoutSeconds = 900;
    private const int ModelCallMaxRetries = 0;

    // Максимальный объём ответа модели (thinking сюда не входит — не запрашиваем
    // extended thinking для этих задач). 16000 — тот же порядок, что раньше
    // подразумевался у OpenAI-совместимого пути через параметры по умолчанию.
    private const int MaxOutputTokens = 16000;

    private bool UseNativeSdk => configuration.GetValue("Aggregator:UseNativeSdk", true);

    // Начинает запрос и, если работает нативный путь, делает диалог продолжаемым:
    // возвращает conversationId (иначе null — legacy-путь продолжение не
    // поддерживает, там нет prompt caching). sandboxRoot != null — режим поиска по
    // папке (инструменты list_files/read_document); null — прикреплённые файлы
    // (текст уже в systemPrompt).
    public async Task<(AgenticCompletionResult Result, Guid? ConversationId)> StartConversationAsync(
        string model,
        string systemPrompt,
        string userText,
        string? sandboxRoot,
        Guid userId,
        IReadOnlyList<byte[]>? images = null,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!UseNativeSdk)
        {
            var legacy = sandboxRoot is not null
                ? await CompleteWithFileSearchAsyncLegacy(model, systemPrompt, userText, sandboxRoot, userId, images, cancellationToken)
                : new AgenticCompletionResult(await CompleteAsyncLegacy(model, systemPrompt, userText, images, cancellationToken), [], [], TokenUsage.Empty);
            return (legacy, null);
        }

        // Блок дисциплины инструментов нужен только когда инструменты реально
        // подключены (режим поиска по папке). Он сохранится в ConversationState и
        // будет действовать и на продолжениях диалога.
        var systemBlocks = BuildSystemBlocksNative(systemPrompt, toolWorkflow: sandboxRoot is not null);
        List<MessageParam> messages = [new() { Role = Role.User, Content = BuildUserContentNative(userText, images) }];

        var result = await RunTurnNative(model, systemBlocks, messages, sandboxRoot, userId, status, cancellationToken);

        var conversationId = Guid.NewGuid();
        conversationStore.Save(
            conversationId,
            new ConversationState
            {
                Model = model,
                SystemBlocks = systemBlocks,
                Messages = messages,
                SandboxRoot = sandboxRoot,
                UserId = userId,
            }
        );
        return (result, conversationId);
    }

    // Продолжает существующий диалог (в пределах TTL). Дописывает вопрос
    // пользователя, гоняет тот же ход (с инструментами, если это поиск по папке),
    // дописывает ответ, обновляет TTL. Бросает ConversationNotFoundException, если
    // диалог истёк/не найден или принадлежит другому пользователю.
    public async Task<AgenticCompletionResult> ContinueConversationAsync(
        Guid conversationId,
        string followupText,
        Guid userId,
        IReadOnlyList<byte[]>? images = null,
        string? combinedFileText = null,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default
    )
    {
        // Продолжение — только на нативном пути (кэш). Если рантайм переключили на
        // legacy между стартом и продолжением, диалог считаем недоступным.
        var state = UseNativeSdk ? conversationStore.Get(conversationId) : null;
        if (state is null || state.UserId != userId)
        {
            throw new ConversationNotFoundException();
        }

        // Прикреплённые к follow-up файлы: их извлечённый текст дописываем к вопросу,
        // сканы (без текстового слоя) уходят изображениями (см. BuildUserContentNative).
        var userText = string.IsNullOrEmpty(combinedFileText) ? followupText : $"{followupText}\n\n{combinedFileText}";
        state.Messages.Add(
            new MessageParam { Role = Role.User, Content = BuildUserContentNative(userText, images) }
        );

        var result = await RunTurnNative(state.Model, state.SystemBlocks, state.Messages, state.SandboxRoot, userId, status, cancellationToken);

        conversationStore.Save(conversationId, state); // обновляет TTL
        return result;
    }

    // ============================================================
    // Native — официальный Anthropic .NET SDK, напрямую /v1/messages
    // агрегатора (не через OpenAI-совместимый маршрут). Даёт доступ к
    // cache_control (prompt caching), которого нет в OpenAI wire-формате —
    // на этом агрегаторе подтверждено эмпирически: повторное чтение
    // закэшированного system-промпта стоит ~1/11 от полной цены.
    // ============================================================

    // Один ход диалога на нативном пути. Мутирует messages (дописывает
    // assistant/tool-результаты/финальный ответ) — вызывающий сохраняет их в
    // ConversationStore для продолжения. Управляет скользящей точкой кэша (1h):
    // снимает пометку с previousCacheBlock (блок, помеченный на прошлом ходу), в
    // ходе tool-loop катит её по последнему блоку прочитанных документов, и
    // возвращает финально помеченный блок, чтобы его сняли на следующем ходу.
    // Так на каждый запрос ровно 2 точки кэша (system + один блок документов) —
    // с запасом в лимит из 4. Чтение кэша автоматическое: даже без пометки на
    // документах follow-up переиспользует уже записанный префикс.
    private async Task<AgenticCompletionResult> RunTurnNative(
        string model,
        List<TextBlockParam> systemBlocks,
        List<MessageParam> messages,
        string? sandboxRoot,
        Guid userId,
        IProgress<string>? status,
        CancellationToken cancellationToken
    )
    {
        var client = CreateNativeClient();

        // web_search — серверный инструмент Anthropic: агрегатор (aitunnel) его
        // поддерживает (проверено live-запросом — реально ходит в интернет и
        // возвращает результаты с источниками). Доступен ВЕЗДЕ: и при поиске по
        // папке, и при прикреплённых файлах, и в продолжении диалога — модель сама
        // решает, искать ли (тратит ~рубли только когда реально нужно). Выполняется
        // на стороне Anthropic внутри одного вызова (stop_reason=end_turn), поэтому
        // отдельной обработки в нашем tool-loop не требует — только объявления.
        // create_file — клиентский инструмент: модель просит собрать файл (docx/xlsx),
        // мы его генерируем (см. DocumentGenerationService) и прикладываем к ответу для
        // скачивания. Доступен ВЕЗДЕ (поиск по папке, прикреплённые файлы, продолжение).
        List<ToolUnion> tools =
        [
            new WebSearchTool20250305 { MaxUses = MaxWebSearchUses },
            DocumentGenerationService.BuildCreateFileTool(),
        ];
        if (sandboxRoot is not null)
        {
            tools.Add(BuildListFilesTool());
            tools.Add(BuildReadDocumentTool());
            tools.Add(BuildSearchDocumentTool());
            tools.Add(BuildDocumentOverviewTool());
            tools.Add(BuildRenderPageTool());
        }

        var accessedFiles = new List<string>();
        var generatedFiles = new List<GeneratedFileData>();
        var usage = TokenUsage.Empty;
        var totalDocumentChars = 0;
        var totalToolImages = 0;

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            // Пока ждём ответ модели — показываем "Думаю…" (между чтениями файлов
            // это же покрывает и веб-поиск, который идёт внутри вызова).
            status?.Report("Думаю…");

            var response = await CreateWithEdgeRetryAsync(
                client,
                new MessageCreateParams
                {
                    Model = model,
                    MaxTokens = MaxOutputTokens,
                    System = systemBlocks,
                    Tools = tools,
                    // Кэш на последнем блоке (см. WithCacheOnLastBlock): каждый вызов
                    // помечает конец текущего префикса, поэтому прочитанные документы
                    // записываются в кэш и переиспользуются и внутри tool-loop'а, и
                    // на follow-up'ах. Сами messages храним без пометок, чтобы точки
                    // кэша не накапливались сверх лимита (4).
                    // PruneOldImages выбрасывает картинки из старых сообщений, чтобы
                    // история не пробила лимит входа агрегатора (8 МБ) — превью/вырезки
                    // чертежей тяжёлые, а модель к этому моменту уже сняла с них цифры.
                    Messages = WithCacheOnLastBlock(PruneOldImages(messages, KeepImageWindowMessages)),
                },
                cancellationToken
            );
            LogUsageNative(response);
            usage = AddUsage(usage, response.Usage);

            if (response.StopReason != "tool_use")
            {
                var answer = RequireNonEmptyTextNative(response);
                AppendAssistantAnswer(messages, answer);
                StripAllStoredImages(messages);
                return new AgenticCompletionResult(answer, accessedFiles, generatedFiles, usage);
            }

            List<ContentBlockParam> assistantContent = [];
            List<ContentBlockParam> toolResults = [];

            foreach (var block in response.Content)
            {
                if (block.TryPickText(out TextBlock? text))
                {
                    assistantContent.Add(new TextBlockParam { Text = text.Text });
                }
                else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
                {
                    assistantContent.Add(new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });

                    status?.Report(DescribeToolActivity(toolUse));

                    var (content, addedChars, addedImages) = await ExecuteToolAsyncNative(
                        toolUse,
                        sandboxRoot,
                        userId,
                        accessedFiles,
                        generatedFiles,
                        totalDocumentChars,
                        totalToolImages
                    );
                    totalDocumentChars += addedChars;
                    totalToolImages += addedImages;

                    toolResults.Add(new ToolResultBlockParam { ToolUseID = toolUse.ID, Content = content });
                }
            }

            messages.Add(new() { Role = Role.Assistant, Content = assistantContent });
            messages.Add(new() { Role = Role.User, Content = toolResults });
        }

        // Лимит итераций исчерпан — просим финальный текстовый ответ без
        // инструментов, а не молча обрываем запрос без ответа пользователю (см.
        // ту же логику и комментарий в legacy-версии ниже).
        var finalResponse = await CreateWithEdgeRetryAsync(
            client,
            new MessageCreateParams
            {
                Model = model,
                MaxTokens = MaxOutputTokens,
                System = systemBlocks,
                Messages = WithCacheOnLastBlock(PruneOldImages(messages, KeepImageWindowMessages)),
            },
            cancellationToken
        );
        LogUsageNative(finalResponse);
        usage = AddUsage(usage, finalResponse.Usage);
        var finalAnswer = RequireNonEmptyTextNative(finalResponse);
        AppendAssistantAnswer(messages, finalAnswer);
        StripAllStoredImages(messages);
        return new AgenticCompletionResult(finalAnswer, accessedFiles, generatedFiles, usage);
    }

    // Прибавляет usage одного вызова модели к накопителю. Вход = «свежие» токены +
    // запись кэша; чтение кэша — отдельно (льготная ставка). ProviderCostUsd — из
    // сырого поля usage.cost, которое добавляет агрегатор (официальный SDK его не
    // типизирует, но сохраняет в RawData).
    private static TokenUsage AddUsage(TokenUsage acc, Usage usage) =>
        acc.Add(
            usage.InputTokens + (usage.CacheCreationInputTokens ?? 0),
            usage.CacheReadInputTokens ?? 0,
            usage.OutputTokens,
            ReadProviderCostUsd(usage)
        );

    private static double ReadProviderCostUsd(Usage usage)
    {
        if (usage.RawData is { } raw && raw.TryGetValue("cost", out var costElement) && costElement.ValueKind == JsonValueKind.Number)
        {
            return costElement.GetDouble();
        }
        return 0;
    }

    // Человекочитаемый статус для конкретного вызова инструмента — показывается
    // пользователю живьём (см. IProgress<string> status).
    private static string DescribeToolActivity(ToolUseBlock toolUse)
    {
        switch (toolUse.Name)
        {
            case "read_document":
                var path = toolUse.Input.TryGetValue("path", out var p) ? p.GetString() : null;
                var name = string.IsNullOrEmpty(path) ? "документ" : Path.GetFileName(path);
                return $"Читаю: {name}";
            case "list_files":
                return "Просматриваю файлы…";
            case "create_file":
                return "Готовлю файл…";
            case "search_document":
                return "Ищу в документе…";
            case "document_overview":
                return "Смотрю листы…";
            case "render_page":
                return "Читаю чертёж…";
            default:
                return "Работаю…";
        }
    }

    private static void AppendAssistantAnswer(List<MessageParam> messages, string answer) =>
        messages.Add(
            new MessageParam { Role = Role.Assistant, Content = new List<ContentBlockParam> { new TextBlockParam { Text = answer } } }
        );

    // Возвращает копию списка сообщений, где последний блок последнего сообщения
    // заменён на клон с пометкой кэша (1h). Сами хранимые сообщения остаются без
    // пометок — так точки кэша не накапливаются между вызовами (Anthropic: максимум
    // 4). CacheControl — init-only, поэтому пометить существующий блок нельзя,
    // только пересоздать. Чтение кэша автоматическое: пометка нужна лишь для
    // ЗАПИСИ; на follow-up'е уже записанный префикс переиспользуется даже без
    // пометки в середине.
    private static List<MessageParam> WithCacheOnLastBlock(List<MessageParam> messages)
    {
        if (messages.Count == 0)
        {
            return messages;
        }

        var last = messages[^1];
        if (!last.Content.TryPickContentBlockParams(out var blocks) || blocks is null || blocks.Count == 0)
        {
            return messages;
        }

        var newBlocks = new List<ContentBlockParam>(blocks);
        newBlocks[^1] = CloneWithCache(blocks[^1]);

        var copy = new List<MessageParam>(messages);
        copy[^1] = new MessageParam { Role = last.Role, Content = newBlocks };
        return copy;
    }

    // Возвращает копию messages, где картинки убраны из всех сообщений, кроме
    // последних keepLastMessages: результат-инструмента с картинками заменяется на
    // текст-заглушку с тем же ToolUseID (связка tool_use↔tool_result сохраняется).
    // Так история не пробивает лимит входа агрегатора (8 МБ на запрос).
    private static List<MessageParam> PruneOldImages(List<MessageParam> messages, int keepLastMessages)
    {
        var threshold = messages.Count - keepLastMessages;
        if (threshold <= 0)
        {
            return messages;
        }

        var result = new List<MessageParam>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (i >= threshold || !messages[i].Content.TryPickContentBlockParams(out var blocks) || blocks is null)
            {
                result.Add(messages[i]);
                continue;
            }

            var changed = false;
            var newBlocks = new List<ContentBlockParam>(blocks.Count);
            foreach (var block in blocks)
            {
                if (
                    block.TryPickToolResult(out ToolResultBlockParam? tr)
                    && tr.Content.TryPickBlocks(out var inner)
                    && inner is not null
                    && inner.Any(b => b.TryPickImageBlockParam(out _))
                )
                {
                    changed = true;
                    newBlocks.Add(
                        new ToolResultBlockParam
                        {
                            ToolUseID = tr.ToolUseID,
                            Content = new List<Block>
                            {
                                new TextBlockParam { Text = "[изображения этого шага показаны и учтены ранее — при необходимости запроси нужный лист/регион заново]" },
                            },
                        }
                    );
                }
                else
                {
                    newBlocks.Add(block);
                }
            }
            result.Add(changed ? new MessageParam { Role = messages[i].Role, Content = newBlocks } : messages[i]);
        }
        return result;
    }

    // Убирает картинки из ВСЕЙ хранимой истории (мутирует список) — вызывается перед
    // сохранением диалога в ConversationStore, чтобы follow-up'ы не тащили тяжёлые
    // картинки предыдущих ходов и не упирались в лимит входа.
    private static void StripAllStoredImages(List<MessageParam> messages)
    {
        var lean = PruneOldImages(messages, 0);
        if (!ReferenceEquals(lean, messages))
        {
            messages.Clear();
            messages.AddRange(lean);
        }
    }

    private static ContentBlockParam CloneWithCache(ContentBlockParam block)
    {
        if (block.TryPickText(out TextBlockParam? t))
        {
            return new TextBlockParam { Text = t.Text, CacheControl = OneHourCache };
        }
        if (block.TryPickToolResult(out ToolResultBlockParam? tr))
        {
            return new ToolResultBlockParam { ToolUseID = tr.ToolUseID, Content = tr.Content, CacheControl = OneHourCache };
        }
        if (block.TryPickImage(out ImageBlockParam? im))
        {
            return new ImageBlockParam { Source = im.Source, CacheControl = OneHourCache };
        }
        // Прочие типы (например, tool_use) как последний блок кэшируемого префикса
        // практически не встречаются — оставляем без пометки.
        return block;
    }

    // Единственная причина, по которой вообще затевалась миграция на нативный SDK —
    // возможность видеть попадания в prompt cache (у OpenAI-совместимого маршрута
    // usage.cache_read_input_tokens в принципе не существует). Логируем на каждый
    // вызов модели, а не только итогово — именно повторные вызовы внутри одного
    // agentic tool-loop и есть тот случай, где кэш реально экономит.
    private void LogUsageNative(Message response) =>
        logger.LogInformation(
            "Anthropic usage: input={InputTokens} cacheWrite={CacheCreationInputTokens} cacheRead={CacheReadInputTokens} output={OutputTokens}",
            response.Usage.InputTokens,
            response.Usage.CacheCreationInputTokens,
            response.Usage.CacheReadInputTokens,
            response.Usage.OutputTokens
        );

    private async Task<(List<Block> Content, int AddedChars, int AddedImages)> ExecuteToolAsyncNative(
        ToolUseBlock toolUse,
        string? sandboxRoot,
        Guid userId,
        List<string> accessedFiles,
        List<GeneratedFileData> generatedFiles,
        int totalDocumentCharsSoFar,
        int totalToolImagesSoFar
    )
    {
        // create_file не зависит от папки-объекта — обрабатываем до всего, что
        // требует sandboxRoot.
        if (toolUse.Name == "create_file")
        {
            return (GenerateFileResultNative(toolUse, userId, generatedFiles), 0, 0);
        }

        try
        {
            var path = "";
            if (toolUse.Input.TryGetValue("path", out var pathElement))
            {
                path = pathElement.GetString() ?? "";
            }

            var offset = 0;
            if (toolUse.Input.TryGetValue("offset", out var offsetElement) && offsetElement.ValueKind == JsonValueKind.Number)
            {
                offsetElement.TryGetInt32(out offset);
            }

            switch (toolUse.Name)
            {
                case "list_files":
                    var entries = fileAccess.ListFiles(sandboxRoot!, path, userId);
                    return (TextResultNative(entries.Count > 0 ? string.Join("\n", entries) : "(папка пуста)"), 0, 0);

                case "read_document":
                    if (totalDocumentCharsSoFar >= MaxTotalDocumentChars)
                    {
                        return (
                            TextResultNative(
                                "Достигнут суммарный лимит объёма прочитанных документов для одного запроса. Дальнейшие файлы не читаются — сформулируй ответ по уже прочитанным документам, при необходимости уточни у пользователя, что именно посмотреть отдельно."
                            ),
                            0,
                            0
                        );
                    }

                    var extraction = await fileAccess.ReadDocumentAsync(sandboxRoot!, path, userId);
                    if (!accessedFiles.Contains(path))
                    {
                        accessedFiles.Add(path);
                    }

                    if (extraction.Text is not null)
                    {
                        var (pageText, addedChars) = BuildPaginatedDocumentText(extraction.Text, offset);
                        return (TextResultNative(pageText), addedChars, 0);
                    }

                    if (totalToolImagesSoFar >= MaxTotalToolImages)
                    {
                        return (
                            TextResultNative(
                                "Достигнут суммарный лимит числа страниц-сканов для одного запроса. Этот файл (скан без текстового слоя) не читается — сформулируй ответ по уже прочитанным документам."
                            ),
                            0,
                            0
                        );
                    }

                    var images = extraction.Images;
                    toolImageDebug.Dump(userId, "read_document", Path.GetFileNameWithoutExtension(path), images);
                    var imageParts = new List<Block>
                    {
                        new TextBlockParam { Text = $"Файл — скан без текстового слоя, {images.Count} стр. приложены как изображения:" },
                    };
                    imageParts.AddRange(
                        images.Select(img => (Block)new ImageBlockParam
                        {
                            Source = new Base64ImageSource { MediaType = SkiaImages.DetectMediaType(img), Data = Convert.ToBase64String(img) },
                        })
                    );
                    return (imageParts, 0, images.Count);

                case "search_document":
                {
                    var pdfPath = fileAccess.ResolvePdfForRender(sandboxRoot!, path, userId);
                    if (!accessedFiles.Contains(path))
                    {
                        accessedFiles.Add(path);
                    }
                    var query = toolUse.Input.TryGetValue("query", out var queryElement) ? queryElement.GetString() ?? "" : "";
                    if (!drawingRender.HasTextLayer(pdfPath))
                    {
                        return (TextResultNative("У этого PDF нет текстового слоя (это скан). Используй document_overview, чтобы увидеть все листы, и render_page — чтобы прочитать таблицы."), 0, 0);
                    }
                    var hits = drawingRender.SearchText(pdfPath, query);
                    if (hits.Count == 0)
                    {
                        return (TextResultNative($"«{query}» не найдено в текстовом слое. Попробуй другой запрос или посмотри листы через document_overview."), 0, 0);
                    }
                    var found = "Найдено на листах (открой нужный через render_page):\n" + string.Join("\n", hits.Select(h => $"— лист {h.Page}: {h.Snippet}"));
                    return (TextResultNative(found), 0, 0);
                }

                case "document_overview":
                {
                    if (totalToolImagesSoFar >= MaxTotalToolImages)
                    {
                        return (TextResultNative("Достигнут лимит изображений за запрос — новые листы не рендерятся. Сформулируй ответ по уже увиденному."), 0, 0);
                    }
                    var pdfPath = fileAccess.ResolvePdfForRender(sandboxRoot!, path, userId);
                    if (!accessedFiles.Contains(path))
                    {
                        accessedFiles.Add(path);
                    }
                    var overviewFrom = 1;
                    if (toolUse.Input.TryGetValue("from", out var fromElement) && fromElement.ValueKind == JsonValueKind.Number)
                    {
                        fromElement.TryGetInt32(out overviewFrom);
                    }
                    var overview = drawingRender.RenderMontages(pdfPath, Math.Max(1, overviewFrom));
                    toolImageDebug.Dump(userId, "document_overview", $"{Path.GetFileNameWithoutExtension(pdfPath)}_p{overview.FromPage}-{overview.LastPageShown}", overview.Montages);
                    var rangeNote = overview.LastPageShown < overview.TotalPages || overview.FromPage > 1
                        ? $" Показаны листы {overview.FromPage}–{overview.LastPageShown} из {overview.TotalPages}. Чтобы посмотреть следующие — вызови document_overview с from={overview.LastPageShown + 1}."
                        : "";
                    var overviewBlocks = new List<Block>
                    {
                        new TextBlockParam
                        {
                            Text = $"Файл «{Path.GetFileName(pdfPath)}» — {overview.TotalPages} лист(ов).{rangeNote} Ниже превью листов с номерами. Найди листы с нужными таблицами (ведомости объёмов, спецификации) и открой их через render_page — при необходимости с region (вырезкой таблицы) для читаемых цифр.",
                        },
                    };
                    overviewBlocks.AddRange(
                        overview.Montages.Select(m => (Block)new ImageBlockParam
                        {
                            Source = new Base64ImageSource { MediaType = SkiaImages.DetectMediaType(m), Data = Convert.ToBase64String(m) },
                        })
                    );
                    return (overviewBlocks, 0, overview.Montages.Count);
                }

                case "render_page":
                {
                    if (totalToolImagesSoFar >= MaxTotalToolImages)
                    {
                        return (TextResultNative("Достигнут лимит изображений за запрос — новые листы не рендерятся."), 0, 0);
                    }
                    var pdfPath = fileAccess.ResolvePdfForRender(sandboxRoot!, path, userId);
                    if (!accessedFiles.Contains(path))
                    {
                        accessedFiles.Add(path);
                    }
                    var page = 1;
                    if (toolUse.Input.TryGetValue("page", out var pageElement) && pageElement.ValueKind == JsonValueKind.Number)
                    {
                        pageElement.TryGetInt32(out page);
                    }
                    var region = TryParseRegion(toolUse.Input);
                    var png = drawingRender.RenderPage(pdfPath, page, region);
                    var regionTag = region is { } rgn ? $"_x{rgn.X:0.##}y{rgn.Y:0.##}w{rgn.Width:0.##}h{rgn.Height:0.##}" : "_full";
                    toolImageDebug.Dump(userId, "render_page", $"{Path.GetFileNameWithoutExtension(pdfPath)}_p{page}{regionTag}", new[] { png });
                    var label = region is { } rg
                        ? $"Лист {page}, вырезка x={rg.X:0.##} y={rg.Y:0.##} w={rg.Width:0.##} h={rg.Height:0.##} (высокое разрешение):"
                        : $"Лист {page} целиком (для поиска таблицы; чтобы прочитать мелкие цифры — запроси region с долей листа):";
                    return (
                        new List<Block>
                        {
                            new TextBlockParam { Text = label },
                            new ImageBlockParam { Source = new Base64ImageSource { MediaType = SkiaImages.DetectMediaType(png), Data = Convert.ToBase64String(png) } },
                        },
                        0,
                        1
                    );
                }

                default:
                    return (TextResultNative($"Неизвестный инструмент: {toolUse.Name}"), 0, 0);
            }
        }
        catch (PathAccessDeniedException)
        {
            return (TextResultNative("Доступ запрещён: путь вне разрешённой папки объекта, либо файл не найден/не поддерживается."), 0, 0);
        }
        catch (Exception ex)
        {
            return (TextResultNative($"Ошибка при выполнении инструмента: {ex.Message}"), 0, 0);
        }
    }

    private static Tool BuildListFilesTool() =>
        new()
        {
            Name = "list_files",
            Description =
                "Возвращает список файлов и подпапок по относительному пути внутри папки объекта. Пустой path (или его отсутствие) — корень папки объекта. "
                + "Начинай работу с этого: сперва осмотри структуру папки (при необходимости — вложенные папки), чтобы по именам выбрать только те файлы, которые действительно нужны для ответа, и не читать всё подряд.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = "Относительный путь внутри папки объекта; пусто — корень" }
                    ),
                },
            },
        };

    private static Tool BuildReadDocumentTool() =>
        new()
        {
            Name = "read_document",
            Description =
                "Читает содержимое файла (.docx/.xlsx/.pdf) по относительному пути внутри папки объекта — текстом, либо, если это скан без текстового слоя, страницами-изображениями. "
                + "РЕСУРСЫ ОГРАНИЧЕНЫ: суммарный объём чтения за один запрос лимитирован, поэтому выбирай файлы с умом — сначала посмотри список через list_files и читай только релевантные. "
                + $"Длинные файлы отдаются частями примерно по {MaxDocumentCharsPerTool} символов: если файл не поместился целиком, в конце будет указано, сколько осталось и с каким offset продолжить. "
                + "Дочитывай следующий кусок ТОЛЬКО если это действительно нужно для ответа (например, надо вычитать договор целиком) — не листай большие файлы до конца без необходимости.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = "Относительный путь к файлу внутри папки объекта" }
                    ),
                    ["offset"] = JsonSerializer.SerializeToElement(
                        new
                        {
                            type = "integer",
                            description = "С какого символа начать чтение (по умолчанию 0). Для продолжения длинного файла передай offset, указанный в конце предыдущего куска.",
                        }
                    ),
                },
                Required = ["path"],
            },
        };

    private static Tool BuildSearchDocumentTool() =>
        new()
        {
            Name = "search_document",
            Description =
                "Ищет строку в ТЕКСТОВОМ слое PDF и возвращает номера листов + фрагменты — быстрый и бесплатный способ найти нужные листы в текстовых чертежах/документах "
                + "(например «Ведомость объёмов работ», «Спецификация», единицы «м3», «м2», «т»). У сканов текстового слоя нет — там используй document_overview.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Относительный путь к PDF внутри папки объекта" }),
                    ["query"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Что искать (подстрока, без учёта регистра)" }),
                },
                Required = ["path", "query"],
            },
        };

    private static Tool BuildDocumentOverviewTool() =>
        new()
        {
            Name = "document_overview",
            Description =
                "Показывает превью листов PDF сеткой с номерами — используй ПЕРВЫМ для чертежей и сканов: за пару картинок увидишь листы и найдёшь те, где нужные таблицы "
                + "(ведомости объёмов работ, спецификации), не читая всё подряд. Дёшево. Затем открывай нужные листы через render_page. "
                + "Если листов много, покажется первая часть и будет указано, с каким from вызвать повторно для следующих.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Относительный путь к PDF внутри папки объекта" }),
                    ["from"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "С какого листа показывать превью (по умолчанию 1) — для листания больших PDF" }),
                },
                Required = ["path"],
            },
        };

    private static Tool BuildRenderPageTool() =>
        new()
        {
            Name = "render_page",
            Description =
                "Рендерит лист PDF (или его РЕГИОН) в картинку для чтения глазами. Для больших/метровых чертежей работай в два шага: сначала посмотри лист целиком (без region), пойми где таблица, "
                + "потом запроси region — долю листа с таблицей — она отрендерится в высоком разрешении и цифры будут читаемы. НЕ пытайся прочитать мелкий текст со всего метрового листа: "
                + "при сжатии он теряется, всегда вырезай region вокруг таблицы.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Относительный путь к PDF внутри папки объекта" }),
                    ["page"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Номер листа, с 1" }),
                    ["region"] = JsonSerializer.SerializeToElement(
                        new
                        {
                            type = "object",
                            description = "Необязательно: вырезка листа в долях 0..1, начало отсчёта — верхний-левый угол листа. Без region отдаётся весь лист (для поиска таблицы).",
                            properties = new
                            {
                                x = new { type = "number", description = "левый край вырезки, доля 0..1" },
                                y = new { type = "number", description = "верхний край вырезки, доля 0..1" },
                                width = new { type = "number", description = "ширина вырезки, доля 0..1" },
                                height = new { type = "number", description = "высота вырезки, доля 0..1" },
                            },
                        }
                    ),
                },
                Required = ["path", "page"],
            },
        };

    // Разбирает region из входа render_page: {x,y,width,height} в долях 0..1.
    private static PageRegion? TryParseRegion(IReadOnlyDictionary<string, JsonElement> input)
    {
        if (!input.TryGetValue("region", out var r) || r.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        float Coord(string key) => r.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Number ? (float)e.GetDouble() : 0f;
        var region = new PageRegion(Coord("x"), Coord("y"), Coord("width"), Coord("height"));
        return region.Width <= 0 || region.Height <= 0 ? null : region;
    }

    // Готовит "страницу" текста документа начиная с offset и сообщение о том, как
    // (и стоит ли) читать дальше. Общая логика для native и legacy путей, чтобы
    // формат пагинации не разъехался. Возвращает готовый текст результата и
    // сколько символов реально прочитано (для суммарного бюджета — суффикс с
    // подсказкой в бюджет не считаем, он мал и служебный).
    private static (string Text, int AddedChars) BuildPaginatedDocumentText(string fullText, int offset)
    {
        var start = Math.Clamp(offset, 0, fullText.Length);
        var take = Math.Min(MaxDocumentCharsPerTool, fullText.Length - start);
        var slice = fullText.Substring(start, take);
        var end = start + take;
        var remaining = fullText.Length - end;

        string suffix;
        if (remaining > 0)
        {
            suffix =
                $"\n\n[...показаны символы {start}–{end} из {fullText.Length}. Осталось непрочитано {remaining}. "
                + $"Чтобы продолжить читать ЭТОТ ЖЕ файл, вызови read_document с тем же path и offset={end}. "
                + "Помни: суммарный бюджет чтения за один запрос ограничен — дочитывай дальше только если это действительно нужно для ответа.]";
        }
        else if (start > 0)
        {
            suffix = $"\n\n[Конец файла достигнут (всего {fullText.Length} символов).]";
        }
        else
        {
            suffix = "";
        }

        return (slice + suffix, slice.Length);
    }

    private static List<Block> TextResultNative(string text) => [new TextBlockParam { Text = text }];

    // Выполняет create_file: генерирует файл, кладёт в эфемерное хранилище и
    // регистрирует ссылку (её вернёт AgenticCompletionResult). Модели возвращаем
    // короткое подтверждение (без байтов). Ошибку генерации отдаём текстом —
    // модель сможет поправить входные данные и попробовать снова.
    private List<Block> GenerateFileResultNative(ToolUseBlock toolUse, Guid userId, List<GeneratedFileData> generatedFiles)
    {
        try
        {
            var (bytes, fileName, contentType) = documentGeneration.Generate(toolUse.Input);
            generatedFiles.Add(new GeneratedFileData(Guid.NewGuid(), fileName, contentType, bytes));
            return TextResultNative($"Файл «{fileName}» успешно создан и приложен к ответу. Кратко подтверди это пользователю.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось сгенерировать файл через create_file");
            return TextResultNative($"Не удалось создать файл: {ex.Message}. Проверь входные данные (format, filename, sheets/markdown) и попробуй ещё раз.");
        }
    }

    // Точка кэша с TTL 1 час — переиспользуется на system-блоке и на скользящей
    // точке документов. 1 час (а не дефолтные 5 минут) — чтобы продолжение диалога
    // человеком (читает длинный ответ, формулирует вопрос — это минуты) попадало в
    // кэш. Запись 1h стоит 2x против 1.25x у 5 минут, но чтение 0.1x, и дорогой
    // контекст (документы) переиспользуется на весь час — для этого инструмента
    // выгодно (см. ConversationStore, тот же 1 час).
    private static readonly CacheControlEphemeral OneHourCache = new() { Ttl = Ttl.Ttl1h };

    // cache_control на system-промпте: он один и тот же на протяжении всего диалога
    // (и tool-loop'а внутри хода, и всех follow-up'ов), поэтому каждое повторное
    // обращение читает его из кэша, а не оплачивает заново. Для режима
    // прикреплённых файлов здесь же лежит и текст документа — значит он тоже
    // кэшируется на весь диалог.
    private static List<TextBlockParam> BuildSystemBlocksNative(string systemPrompt, bool toolWorkflow) =>
        // Кэш-маркер только на ПОСЛЕДНЕМ блоке (лимит точек кэша — 4); CacheControl
        // init-only, поэтому ставим его в инициализаторе, а не постфактум.
        toolWorkflow
            ?
            [
                new() { Text = systemPrompt },
                new() { Text = ToolWorkflowDiscipline, CacheControl = OneHourCache },
            ]
            : [new() { Text = systemPrompt, CacheControl = OneHourCache }];

    // Дисциплина работы с инструментами по проектной документации. Лечит два
    // реальных сбоя: (1) модель заканчивала ход планом-нарративом («сейчас проверю
    // стр. 17-18…») вместо результата и вставала на end_turn; (2) просила
    // «перечитать» уже виденные листы, потому что картинки из истории удаляются
    // окном PruneOldImages, а цифры она в текст сразу не переносила.
    private const string ToolWorkflowDiscipline =
        "РЕЖИМ РАБОТЫ С ДОКУМЕНТАМИ ЧЕРЕЗ ИНСТРУМЕНТЫ.\n"
        + "1. Изображения листов (превью и вырезки) АВТОМАТИЧЕСКИ удаляются из истории после следующего шага — на них нельзя сослаться позже. Поэтому, открыв лист, СРАЗУ выписывай в текст все нужные данные (номер листа, наименование работ, единицы измерения, объём/количество). Не откладывай перенос цифр «на потом» и не рассчитывай увидеть тот же лист снова без нового render_page.\n"
        + "2. НИКОГДА не заканчивай ответ планом, обещанием или отчётом о процессе («сейчас проверю…», «соберу данные…», «уже видел листы…»). Пока сбор не завершён — вызывай инструмент и продолжай. Как только данные собраны — выдай ИТОГОВЫЙ полный результат (сводную ведомость/таблицу объёмов) целиком.\n"
        + "3. Твоё финальное сообщение — это конечный результат для пользователя, а не заметки о ходе работы. Если тебе кажется, что надо ещё что-то проверить — проверь это вызовом инструмента ПРЯМО СЕЙЧАС, а не завершай ход обещанием.";

    private static List<ContentBlockParam> BuildUserContentNative(string text, IReadOnlyList<byte[]>? images)
    {
        var parts = new List<ContentBlockParam> { new TextBlockParam { Text = text } };
        if (images is not null)
        {
            parts.AddRange(
                images.Select(img => (ContentBlockParam)new ImageBlockParam
                {
                    Source = new Base64ImageSource { MediaType = SkiaImages.DetectMediaType(img), Data = Convert.ToBase64String(img) },
                })
            );
        }
        return parts;
    }

    // См. комментарий у RequireNonEmptyText (legacy) — тот же самый реальный
    // инцидент с формально успешным, но пустым ответом.
    private static string RequireNonEmptyTextNative(Message response)
    {
        var text = ExtractTextNative(response);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                "Модель вернула пустой ответ — вероятно, объём данных объекта оказался слишком большим для одного запроса. Попробуйте сузить область поиска или задать более точный вопрос."
            );
        }
        return text;
    }

    private static string ExtractTextNative(Message response)
    {
        var sb = new StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text))
            {
                sb.Append(text.Text);
            }
        }
        return sb.ToString();
    }

    // Узкий, безопасный ретрай ТОЛЬКО на явные HTTP-ошибки сервера/лимита от
    // агрегатора (5xx — в т.ч. Cloudflare «error code 1102: Worker exceeded
    // resource limits» на edge — и 429). Такой ответ означает, что запрос
    // ОТКЛОНЁН до успешной генерации: до Anthropic он не дошёл, токены не списаны
    // (подтверждено — при 1102 в панели агрегатора запрос пустой), поэтому повтор
    // безопасен и не задваивает оплату. СОЗНАТЕЛЬНО не ретраим таймаут/отмену
    // (OperationCanceledException) и IO-обрывы: там генерация могла уже идти и
    // билля — именно этот сценарий когда-то и запретил автоповторы (см. коммент к
    // ModelCallMaxRetries). Общий built-in MaxRetries SDK остаётся 0.
    private async Task<Message> CreateWithEdgeRetryAsync(
        AnthropicClient client,
        MessageCreateParams parameters,
        CancellationToken cancellationToken
    )
    {
        const int maxAttempts = 3; // 1 обычная + 2 повтора
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await client.Messages.Create(parameters, cancellationToken);
            }
            catch (Exception ex)
                when (attempt < maxAttempts
                    && ex is Anthropic.Exceptions.Anthropic5xxException or Anthropic.Exceptions.AnthropicRateLimitException)
            {
                var delay = TimeSpan.FromSeconds(attempt * 4); // 4с, 8с
                logger.LogWarning(
                    ex,
                    "Агрегатор отклонил запрос ({Type}) — попытка {Attempt}/{Max}, повтор через {Delay}с",
                    ex.GetType().Name,
                    attempt,
                    maxAttempts,
                    delay.TotalSeconds
                );
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private AnthropicClient CreateNativeClient()
    {
        var baseUrl =
            configuration["Aggregator:NativeBaseUrl"]
            ?? throw new InvalidOperationException("Aggregator:NativeBaseUrl не задан в конфигурации");
        var apiKey =
            Environment.GetEnvironmentVariable("AITUNNEL_API_KEY")
            ?? throw new InvalidOperationException("Переменная окружения AITUNNEL_API_KEY не задана");

        return new AnthropicClient
        {
            BaseUrl = baseUrl,
            // Агрегатор ожидает Authorization: Bearer <token> даже на нативном
            // /v1/messages маршруте (подтверждено curl'ом) — это не OAuth-токен,
            // поэтому AuthToken (не ApiKey, который ушёл бы как x-api-key).
            AuthToken = apiKey,
            Timeout = TimeSpan.FromSeconds(ModelCallTimeoutSeconds),
            MaxRetries = ModelCallMaxRetries,
        };
    }

    // ============================================================
    // Legacy — прежняя реализация через OpenAI .NET SDK и OpenAI-совместимый
    // маршрут агрегатора. Оставлена без единого изменения логики — только
    // переименована (суффикс Legacy) — как путь для мгновенного отката через
    // Aggregator:UseNativeSdk=false, без пересборки и без git revert.
    // ============================================================

    private async Task<string> CompleteAsyncLegacy(
        string model,
        string systemPrompt,
        string userText,
        IReadOnlyList<byte[]>? images,
        CancellationToken cancellationToken
    )
    {
        var chatClient = CreateLegacyChatClient(model);

        List<ChatMessage> messages = [new SystemChatMessage(systemPrompt), BuildUserMessageLegacy(userText, images)];

        ChatCompletion completion = await chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        return RequireNonEmptyTextLegacy(completion);
    }

    private async Task<AgenticCompletionResult> CompleteWithFileSearchAsyncLegacy(
        string model,
        string systemPrompt,
        string userText,
        string sandboxRoot,
        Guid userId,
        IReadOnlyList<byte[]>? images,
        CancellationToken cancellationToken
    )
    {
        var chatClient = CreateLegacyChatClient(model);

        var listFilesTool = ChatTool.CreateFunctionTool(
            "list_files",
            "Возвращает список файлов и подпапок по относительному пути внутри папки объекта. Пустой path (или его отсутствие) — корень папки объекта. "
                + "Начинай работу с этого: сперва осмотри структуру папки, чтобы по именам выбрать только нужные файлы и не читать всё подряд.",
            BinaryData.FromString(
                """
                {"type":"object","properties":{"path":{"type":"string","description":"Относительный путь внутри папки объекта; пусто — корень"}}}
                """
            )
        );
        var readDocumentTool = ChatTool.CreateFunctionTool(
            "read_document",
            "Читает содержимое файла (.docx/.xlsx/.pdf) по относительному пути внутри папки объекта — текстом, либо, если это скан без текстового слоя, страницами-изображениями. "
                + "РЕСУРСЫ ОГРАНИЧЕНЫ: суммарный объём чтения за один запрос лимитирован — выбирай файлы с умом (сначала list_files), читай только релевантные. "
                + "Длинные файлы отдаются частями: если файл не поместился целиком, в конце будет указан offset для продолжения. Дочитывай следующий кусок только если это действительно нужно для ответа.",
            BinaryData.FromString(
                """
                {"type":"object","properties":{"path":{"type":"string","description":"Относительный путь к файлу внутри папки объекта"},"offset":{"type":"integer","description":"С какого символа начать чтение (по умолчанию 0). Для продолжения длинного файла передай offset из конца предыдущего куска."}},"required":["path"]}
                """
            )
        );

        var chatOptions = new ChatCompletionOptions();
        chatOptions.Tools.Add(listFilesTool);
        chatOptions.Tools.Add(readDocumentTool);

        List<ChatMessage> messages = [new SystemChatMessage(systemPrompt), BuildUserMessageLegacy(userText, images)];

        var accessedFiles = new List<string>();
        var totalDocumentChars = 0;
        var totalToolImages = 0;

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var completion = await chatClient.CompleteChatAsync(messages, chatOptions, cancellationToken);

            if (completion.Value.FinishReason != ChatFinishReason.ToolCalls)
            {
                return new AgenticCompletionResult(RequireNonEmptyTextLegacy(completion.Value), accessedFiles, [], TokenUsage.Empty);
            }

            messages.Add(new AssistantChatMessage(completion.Value));

            foreach (var toolCall in completion.Value.ToolCalls)
            {
                var (content, addedChars, addedImages) = await ExecuteToolAsyncLegacy(
                    toolCall,
                    sandboxRoot,
                    userId,
                    accessedFiles,
                    totalDocumentChars,
                    totalToolImages
                );
                totalDocumentChars += addedChars;
                totalToolImages += addedImages;
                messages.Add(new ToolChatMessage(toolCall.Id, content));
            }
        }

        // Лимит итераций исчерпан — просим финальный текстовый ответ без инструментов,
        // а не молча обрываем запрос без ответа пользователю. chatOptions сюда
        // намеренно не передаётся — тем не менее модель уже видела в истории
        // сообщений собственные предыдущие tool_calls, поэтому проверка на пустой
        // ответ ниже (RequireNonEmptyTextLegacy) обязательна: именно на этом пути и
        // случился реальный инцидент с пустым ответом без единой ошибки.
        var finalCompletion = await chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        return new AgenticCompletionResult(RequireNonEmptyTextLegacy(finalCompletion.Value), accessedFiles, [], TokenUsage.Empty);
    }

    // Модель иногда возвращает формально успешный ответ (200, без исключений) с
    // пустым содержимым — например, когда финальный вызов после исчерпания лимита
    // итераций происходит без объявленных инструментов, а модель всё ещё "хочет"
    // вызвать какой-то из них. Раньше такой пустой текст молча уходил
    // пользователю как успешный ответ (реальный инцидент — см. чеклист); теперь
    // это осознанная ошибка с понятным сообщением, а не тихая пустота.
    private static string RequireNonEmptyTextLegacy(ChatCompletion completion)
    {
        var text = ExtractTextLegacy(completion);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                "Модель вернула пустой ответ — вероятно, объём данных объекта оказался слишком большим для одного запроса. Попробуйте сузить область поиска или задать более точный вопрос."
            );
        }
        return text;
    }

    // Возвращает (содержимое результата инструмента, сколько символов текста и
    // сколько картинок этот вызов добавил в суммарные бюджеты) — последние два
    // нужны, чтобы вызывающий код мог накапливать totalDocumentChars/
    // totalToolImages самостоятельно (у async-методов не может быть ref/out
    // параметров). Content — всегда список content-частей (даже для обычного
    // текста, см. ниже) — единообразно и для текстовых результатов, и для
    // сканов без текстового слоя, у которых внутри картинки (подтверждено
    // smoke-тестом: агрегатор поддерживает изображения в результате tool-вызова).
    private async Task<(IReadOnlyList<ChatMessageContentPart> Content, int AddedChars, int AddedImages)> ExecuteToolAsyncLegacy(
        ChatToolCall toolCall,
        string sandboxRoot,
        Guid userId,
        List<string> accessedFiles,
        int totalDocumentCharsSoFar,
        int totalToolImagesSoFar
    )
    {
        try
        {
            var path = "";
            var offset = 0;
            if (toolCall.FunctionArguments is { } args && args.ToMemory().Length > 0)
            {
                using var argsDoc = JsonDocument.Parse(args);
                if (argsDoc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (argsDoc.RootElement.TryGetProperty("path", out var pathProp))
                    {
                        path = pathProp.GetString() ?? "";
                    }
                    if (argsDoc.RootElement.TryGetProperty("offset", out var offsetProp) && offsetProp.ValueKind == JsonValueKind.Number)
                    {
                        offsetProp.TryGetInt32(out offset);
                    }
                }
            }

            switch (toolCall.FunctionName)
            {
                case "list_files":
                    var entries = fileAccess.ListFiles(sandboxRoot, path, userId);
                    return (TextResultLegacy(entries.Count > 0 ? string.Join("\n", entries) : "(папка пуста)"), 0, 0);

                case "read_document":
                    if (totalDocumentCharsSoFar >= MaxTotalDocumentChars)
                    {
                        // Суммарный бюджет на запрос исчерпан — не открываем больше
                        // файлов, а прямо говорим об этом модели, чтобы она подвела
                        // итог по уже прочитанному вместо попытки прочитать всё подряд.
                        return (
                            TextResultLegacy(
                                "Достигнут суммарный лимит объёма прочитанных документов для одного запроса. Дальнейшие файлы не читаются — сформулируй ответ по уже прочитанным документам, при необходимости уточни у пользователя, что именно посмотреть отдельно."
                            ),
                            0,
                            0
                        );
                    }

                    var extraction = await fileAccess.ReadDocumentAsync(sandboxRoot, path, userId);
                    if (!accessedFiles.Contains(path))
                    {
                        accessedFiles.Add(path);
                    }

                    if (extraction.Text is not null)
                    {
                        var (pageText, addedChars) = BuildPaginatedDocumentText(extraction.Text, offset);
                        return (TextResultLegacy(pageText), addedChars, 0);
                    }

                    // Скан без текстового слоя — картинки страниц идут прямо в
                    // результат этого tool-вызова, а не отдельным сообщением.
                    if (totalToolImagesSoFar >= MaxTotalToolImages)
                    {
                        return (
                            TextResultLegacy(
                                "Достигнут суммарный лимит числа страниц-сканов для одного запроса. Этот файл (скан без текстового слоя) не читается — сформулируй ответ по уже прочитанным документам."
                            ),
                            0,
                            0
                        );
                    }

                    var images = extraction.Images;
                    var imageParts = new List<ChatMessageContentPart>
                    {
                        ChatMessageContentPart.CreateTextPart($"Файл — скан без текстового слоя, {images.Count} стр. приложены как изображения:"),
                    };
                    imageParts.AddRange(images.Select(img => ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(img), SkiaImages.DetectMediaType(img))));
                    return (imageParts, 0, images.Count);

                default:
                    return (TextResultLegacy($"Неизвестный инструмент: {toolCall.FunctionName}"), 0, 0);
            }
        }
        catch (PathAccessDeniedException)
        {
            return (TextResultLegacy("Доступ запрещён: путь вне разрешённой папки объекта, либо файл не найден/не поддерживается."), 0, 0);
        }
        catch (Exception ex)
        {
            return (TextResultLegacy($"Ошибка при выполнении инструмента: {ex.Message}"), 0, 0);
        }
    }

    private static IReadOnlyList<ChatMessageContentPart> TextResultLegacy(string text) => [ChatMessageContentPart.CreateTextPart(text)];

    // Сканы без текстового слоя приходят как готовые PNG-страницы (см.
    // FileTextExtractor.ExtractPdfAsync) — добавляем их как image-части того же
    // пользовательского сообщения, а не отдельными сообщениями: модели проще
    // связать текст запроса с картинками, когда они в одном UserChatMessage.
    private static UserChatMessage BuildUserMessageLegacy(string text, IReadOnlyList<byte[]>? images)
    {
        if (images is null || images.Count == 0)
        {
            return new UserChatMessage(text);
        }

        var parts = new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart(text) };
        parts.AddRange(images.Select(img => ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(img), SkiaImages.DetectMediaType(img))));
        return new UserChatMessage(parts);
    }

    private static string ExtractTextLegacy(ChatCompletion completion) => completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;

    private ChatClient CreateLegacyChatClient(string model)
    {
        var baseUrl =
            configuration["Aggregator:BaseUrl"]
            ?? throw new InvalidOperationException("Aggregator:BaseUrl не задан в конфигурации");
        var apiKey =
            Environment.GetEnvironmentVariable("AITUNNEL_API_KEY")
            ?? throw new InvalidOperationException("Переменная окружения AITUNNEL_API_KEY не задана");

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl),
            NetworkTimeout = TimeSpan.FromSeconds(ModelCallTimeoutSeconds),
            RetryPolicy = new ClientRetryPolicy(maxRetries: ModelCallMaxRetries),
        };
        return new ChatClient(model, new ApiKeyCredential(apiKey), options);
    }
}
