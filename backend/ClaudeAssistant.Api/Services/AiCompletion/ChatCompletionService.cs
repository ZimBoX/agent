using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using ClaudeAssistant.Api.Services.FileTextExtraction;
using ClaudeAssistant.Api.Services.ProjectFiles;
using OpenAI;
using OpenAI.Chat;

namespace ClaudeAssistant.Api.Services.AiCompletion;

public record AgenticCompletionResult(string ResultText, List<string> AccessedFiles);

public class ChatCompletionService(IConfiguration configuration, SandboxedFileAccessService fileAccess)
{
    // Ограничивает число обращений модели к инструментам за один запрос — иначе
    // модель теоретически может уйти в цикл list_files/read_document без остановки,
    // раздувая и время ответа, и стоимость запроса. 8 оказалось мало на практике:
    // реальный объект пользователя потребовал 18 read_document (договор, допсоглашение,
    // больше десятка АОСР/ведомостей) — упёрлись в лимит на середине, что и привело
    // к пустому ответу (см. IMPLEMENTATION_CHECKLIST.md, Этап 11).
    private const int MaxToolIterations = 20;

    // Ограничивает, сколько текста одного документа модель получает за один вызов
    // read_document — иначе один большой файл может занять весь бюджет контекста.
    // Снижено с 40 000: при действительно большом объекте (десятки файлов) даже
    // по 40к на файл суммарный контекст мог раздуться до сотен тысяч символов.
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
    private const int ModelCallTimeoutSeconds = 240;
    private const int ModelCallMaxRetries = 0;

    public async Task<string> CompleteAsync(
        string model,
        string systemPrompt,
        string userText,
        IReadOnlyList<byte[]>? images = null,
        CancellationToken cancellationToken = default
    )
    {
        var chatClient = CreateChatClient(model);

        List<ChatMessage> messages = [new SystemChatMessage(systemPrompt), BuildUserMessage(userText, images)];

        ChatCompletion completion = await chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        return RequireNonEmptyText(completion);
    }

    // Как CompleteAsync, но модель дополнительно может сама вызывать list_files/
    // read_document, чтобы просмотреть содержимое указанной папки объекта —
    // строго в границах, которые обеспечивает SandboxedFileAccessService. Нужен
    // отдельный метод (а не флаг у CompleteAsync), потому что здесь принципиально
    // другой протокол — многошаговый цикл с ToolChatMessage, а не один вызов.
    public async Task<AgenticCompletionResult> CompleteWithFileSearchAsync(
        string model,
        string systemPrompt,
        string userText,
        string sandboxRoot,
        Guid userId,
        IReadOnlyList<byte[]>? images = null,
        CancellationToken cancellationToken = default
    )
    {
        var chatClient = CreateChatClient(model);

        var listFilesTool = ChatTool.CreateFunctionTool(
            "list_files",
            "Возвращает список файлов и подпапок по относительному пути внутри папки объекта. Пустой path (или его отсутствие) — корень папки объекта.",
            BinaryData.FromString(
                """
                {"type":"object","properties":{"path":{"type":"string","description":"Относительный путь внутри папки объекта; пусто — корень"}}}
                """
            )
        );
        var readDocumentTool = ChatTool.CreateFunctionTool(
            "read_document",
            "Читает и возвращает содержимое файла (.docx/.xlsx/.pdf) по относительному пути внутри папки объекта — текстом, либо, если это скан без текстового слоя, страницами-изображениями.",
            BinaryData.FromString(
                """
                {"type":"object","properties":{"path":{"type":"string","description":"Относительный путь к файлу внутри папки объекта"}},"required":["path"]}
                """
            )
        );

        var chatOptions = new ChatCompletionOptions();
        chatOptions.Tools.Add(listFilesTool);
        chatOptions.Tools.Add(readDocumentTool);

        List<ChatMessage> messages = [new SystemChatMessage(systemPrompt), BuildUserMessage(userText, images)];

        var accessedFiles = new List<string>();
        var totalDocumentChars = 0;
        var totalToolImages = 0;

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var completion = await chatClient.CompleteChatAsync(messages, chatOptions, cancellationToken);

            if (completion.Value.FinishReason != ChatFinishReason.ToolCalls)
            {
                return new AgenticCompletionResult(RequireNonEmptyText(completion.Value), accessedFiles);
            }

            messages.Add(new AssistantChatMessage(completion.Value));

            foreach (var toolCall in completion.Value.ToolCalls)
            {
                var (content, addedChars, addedImages) = await ExecuteToolAsync(
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
        // ответ ниже (RequireNonEmptyText) обязательна: именно на этом пути и
        // случился реальный инцидент с пустым ответом без единой ошибки.
        var finalCompletion = await chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        return new AgenticCompletionResult(RequireNonEmptyText(finalCompletion.Value), accessedFiles);
    }

    // Модель иногда возвращает формально успешный ответ (200, без исключений) с
    // пустым содержимым — например, когда финальный вызов после исчерпания лимита
    // итераций происходит без объявленных инструментов, а модель всё ещё "хочет"
    // вызвать какой-то из них. Раньше такой пустой текст молча уходил
    // пользователю как успешный ответ (реальный инцидент — см. чеклист); теперь
    // это осознанная ошибка с понятным сообщением, а не тихая пустота.
    private static string RequireNonEmptyText(ChatCompletion completion)
    {
        var text = ExtractText(completion);
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
    private async Task<(IReadOnlyList<ChatMessageContentPart> Content, int AddedChars, int AddedImages)> ExecuteToolAsync(
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
            if (toolCall.FunctionArguments is { } args && args.ToMemory().Length > 0)
            {
                using var argsDoc = JsonDocument.Parse(args);
                if (argsDoc.RootElement.ValueKind == JsonValueKind.Object && argsDoc.RootElement.TryGetProperty("path", out var pathProp))
                {
                    path = pathProp.GetString() ?? "";
                }
            }

            switch (toolCall.FunctionName)
            {
                case "list_files":
                    var entries = fileAccess.ListFiles(sandboxRoot, path, userId);
                    return (TextResult(entries.Count > 0 ? string.Join("\n", entries) : "(папка пуста)"), 0, 0);

                case "read_document":
                    if (totalDocumentCharsSoFar >= MaxTotalDocumentChars)
                    {
                        // Суммарный бюджет на запрос исчерпан — не открываем больше
                        // файлов, а прямо говорим об этом модели, чтобы она подвела
                        // итог по уже прочитанному вместо попытки прочитать всё подряд.
                        return (
                            TextResult(
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
                        var truncated = extraction.Text.Length > MaxDocumentCharsPerTool
                            ? extraction.Text[..MaxDocumentCharsPerTool] + "\n\n[...текст обрезан, документ слишком длинный...]"
                            : extraction.Text;
                        return (TextResult(truncated), truncated.Length, 0);
                    }

                    // Скан без текстового слоя — картинки страниц идут прямо в
                    // результат этого tool-вызова, а не отдельным сообщением.
                    if (totalToolImagesSoFar >= MaxTotalToolImages)
                    {
                        return (
                            TextResult(
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
                    return (TextResult($"Неизвестный инструмент: {toolCall.FunctionName}"), 0, 0);
            }
        }
        catch (PathAccessDeniedException)
        {
            return (TextResult("Доступ запрещён: путь вне разрешённой папки объекта, либо файл не найден/не поддерживается."), 0, 0);
        }
        catch (Exception ex)
        {
            return (TextResult($"Ошибка при выполнении инструмента: {ex.Message}"), 0, 0);
        }
    }

    private static IReadOnlyList<ChatMessageContentPart> TextResult(string text) => [ChatMessageContentPart.CreateTextPart(text)];

    // Сканы без текстового слоя приходят как готовые PNG-страницы (см.
    // FileTextExtractor.ExtractPdfAsync) — добавляем их как image-части того же
    // пользовательского сообщения, а не отдельными сообщениями: модели проще
    // связать текст запроса с картинками, когда они в одном UserChatMessage.
    private static UserChatMessage BuildUserMessage(string text, IReadOnlyList<byte[]>? images)
    {
        if (images is null || images.Count == 0)
        {
            return new UserChatMessage(text);
        }

        var parts = new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart(text) };
        parts.AddRange(images.Select(img => ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(img), "image/png")));
        return new UserChatMessage(parts);
    }

    private static string ExtractText(ChatCompletion completion) => completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;

    private ChatClient CreateChatClient(string model)
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
