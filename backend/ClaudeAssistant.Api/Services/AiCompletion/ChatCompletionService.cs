using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using ClaudeAssistant.Api.Services.FileTextExtraction;
using ClaudeAssistant.Api.Services.ProjectFiles;
using OpenAI;
using OpenAI.Chat;

namespace ClaudeAssistant.Api.Services.AiCompletion;

public record AgenticCompletionResult(string ResultText, List<string> AccessedFiles);

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

    // Аналогичный суммарный лимит для сканов без текстового слоя, найденных через
    // read_document (не для файлов, прикреплённых вручную — там своего протокола
    // нет) — картинки тяжелее текста, поэтому бюджет отдельный и куда скромнее.
    private const int MaxTotalToolImages = 30;

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
                : new AgenticCompletionResult(await CompleteAsyncLegacy(model, systemPrompt, userText, images, cancellationToken), []);
            return (legacy, null);
        }

        var systemBlocks = BuildSystemBlocksNative(systemPrompt);
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
        List<ToolUnion> tools = [new WebSearchTool20250305 { MaxUses = MaxWebSearchUses }];
        if (sandboxRoot is not null)
        {
            tools.Add(BuildListFilesTool());
            tools.Add(BuildReadDocumentTool());
        }

        var accessedFiles = new List<string>();
        var totalDocumentChars = 0;
        var totalToolImages = 0;

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            // Пока ждём ответ модели — показываем "Думаю…" (между чтениями файлов
            // это же покрывает и веб-поиск, который идёт внутри вызова).
            status?.Report("Думаю…");

            var response = await client.Messages.Create(
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
                    Messages = WithCacheOnLastBlock(messages),
                },
                cancellationToken
            );
            LogUsageNative(response);

            if (response.StopReason != "tool_use")
            {
                var answer = RequireNonEmptyTextNative(response);
                AppendAssistantAnswer(messages, answer);
                return new AgenticCompletionResult(answer, accessedFiles);
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
                        sandboxRoot!,
                        userId,
                        accessedFiles,
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
        var finalResponse = await client.Messages.Create(
            new MessageCreateParams
            {
                Model = model,
                MaxTokens = MaxOutputTokens,
                System = systemBlocks,
                Messages = WithCacheOnLastBlock(messages),
            },
            cancellationToken
        );
        LogUsageNative(finalResponse);
        var finalAnswer = RequireNonEmptyTextNative(finalResponse);
        AppendAssistantAnswer(messages, finalAnswer);
        return new AgenticCompletionResult(finalAnswer, accessedFiles);
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
                    var entries = fileAccess.ListFiles(sandboxRoot, path, userId);
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

                    var extraction = await fileAccess.ReadDocumentAsync(sandboxRoot, path, userId);
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
                    var imageParts = new List<Block>
                    {
                        new TextBlockParam { Text = $"Файл — скан без текстового слоя, {images.Count} стр. приложены как изображения:" },
                    };
                    imageParts.AddRange(
                        images.Select(img => (Block)new ImageBlockParam
                        {
                            Source = new Base64ImageSource { MediaType = "image/png", Data = Convert.ToBase64String(img) },
                        })
                    );
                    return (imageParts, 0, images.Count);

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
    private static List<TextBlockParam> BuildSystemBlocksNative(string systemPrompt) =>
        [new() { Text = systemPrompt, CacheControl = OneHourCache }];

    private static List<ContentBlockParam> BuildUserContentNative(string text, IReadOnlyList<byte[]>? images)
    {
        var parts = new List<ContentBlockParam> { new TextBlockParam { Text = text } };
        if (images is not null)
        {
            parts.AddRange(
                images.Select(img => (ContentBlockParam)new ImageBlockParam
                {
                    Source = new Base64ImageSource { MediaType = "image/png", Data = Convert.ToBase64String(img) },
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
                return new AgenticCompletionResult(RequireNonEmptyTextLegacy(completion.Value), accessedFiles);
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
        return new AgenticCompletionResult(RequireNonEmptyTextLegacy(finalCompletion.Value), accessedFiles);
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
                    imageParts.AddRange(images.Select(img => ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(img), "image/png")));
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
        parts.AddRange(images.Select(img => ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(img), "image/png")));
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
