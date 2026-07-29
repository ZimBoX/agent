using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using ClaudeAssistant.Api.Services.FileGeneration;
using ClaudeAssistant.Api.Services.ProjectFiles;

namespace ClaudeAssistant.Api.Services.AiCompletion;

// OpenAI-совместимый путь агрегатора (/v1/chat/completions). Параллелен нативному
// (Anthropic) — выбор пути по модели (см. AiModel.UseAnthropicWire): claude идёт
// нативно (там кэш claude'а), остальные (gemini/qwen/gpt) — здесь. Формат сам
// чинит парсинг tool-аргументов (arguments — всегда JSON-строка, парсим сами) и
// даёт structured outputs. Отличие: tool-ответ (role:tool) — только текст, поэтому
// картинки инструментов (render_page и т.п.) идут отдельным user-сообщением.
public partial class ChatCompletionService
{
    // Один HttpClient на процесс (рекомендация .NET). Таймаут — как у нативного пути.
    private static readonly HttpClient OpenAiHttp = new() { Timeout = TimeSpan.FromSeconds(ModelCallTimeoutSeconds) };

    private string OpenAiEndpoint =>
        (configuration["Aggregator:NativeBaseUrl"]?.TrimEnd('/') ?? throw new InvalidOperationException("Aggregator:NativeBaseUrl не задан"))
        + "/v1/chat/completions";

    private string AggregatorKey =>
        Environment.GetEnvironmentVariable("AITUNNEL_API_KEY")
        ?? throw new InvalidOperationException("Переменная окружения AITUNNEL_API_KEY не задана");

    private sealed record OpenAiToolCall(string Id, string Name, string ArgumentsJson);

    private sealed record OpenAiTurn(string Text, List<OpenAiToolCall> ToolCalls, string? FinishReason, TokenUsage Usage);

    // Строит начальные OpenAI-сообщения: system (промпт + дисциплина инструментов,
    // с cache_control) + user (задача + прикреплённые картинки).
    private List<object> BuildInitialOpenAiMessages(string systemPrompt, bool toolWorkflow, string userText, IReadOnlyList<byte[]>? images)
    {
        var systemText = toolWorkflow ? systemPrompt + "\n\n" + ToolWorkflowDiscipline : systemPrompt;
        return
        [
            OpenAiSystemMessage(systemText),
            OpenAiUserMessage(userText, images),
        ];
    }

    // Прогоняет OpenAI-цикл. messages уже содержит system + историю (растёт по месту).
    private async Task<AgenticCompletionResult> RunTurnOpenAi(
        string model,
        List<object> messages,
        string? sandboxRoot,
        Guid userId,
        IProgress<string>? status,
        CancellationToken cancellationToken
    )
    {
        toolImageDebug.BeginRun(userId);

        // Инструменты OpenAI-формата (без web_search — это серверный тул Anthropic).
        List<object>? tools = null;
        if (sandboxRoot is not null)
        {
            tools =
            [
                ToOpenAiFunction(BuildListFilesTool()),
                ToOpenAiFunction(BuildReadDocumentTool()),
                ToOpenAiFunction(BuildSearchDocumentTool()),
                ToOpenAiFunction(BuildDocumentOverviewTool()),
                ToOpenAiFunction(BuildRenderPageTool()),
                ToOpenAiFunction(DocumentGenerationService.BuildCreateFileTool()),
                ToOpenAiFunction(ToolHelp.BuildTool()),
            ];
        }
        else
        {
            tools =
            [
                ToOpenAiFunction(DocumentGenerationService.BuildCreateFileTool()),
                ToOpenAiFunction(ToolHelp.BuildTool()),
            ];
        }

        var accessedFiles = new List<string>();
        var generatedFiles = new List<GeneratedFileData>();
        var usage = TokenUsage.Empty;
        var totalDocumentChars = 0;
        var totalToolImages = 0;
        var finalizeNudges = 0;

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            status?.Report("Думаю…");

            var turn = await PostChatCompletionAsync(model, PruneOpenAiImages(messages, KeepImageWindowMessages), tools, cancellationToken);
            usage = usage.Add(turn.Usage.InputTokens, turn.Usage.CacheReadTokens, turn.Usage.OutputTokens, turn.Usage.ProviderCostUsd);

            if (turn.ToolCalls.Count == 0)
            {
                var answer = turn.Text;
                var isTruncated = turn.FinishReason == "length";
                if (!isTruncated && finalizeNudges < MaxFinalizeNudges && LooksUnfinished(answer))
                {
                    finalizeNudges++;
                    messages.Add(OpenAiAssistantText(string.IsNullOrWhiteSpace(answer) ? "…" : answer));
                    messages.Add(OpenAiUserMessage(FinalizeNudgeText, null));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(answer))
                {
                    throw new InvalidOperationException(
                        "Модель вернула пустой ответ — вероятно, объём данных объекта оказался слишком большим для одного запроса. Попробуйте сузить область поиска или задать более точный вопрос."
                    );
                }
                messages.Add(OpenAiAssistantText(answer));
                return new AgenticCompletionResult(answer, accessedFiles, generatedFiles, usage);
            }

            // Ассистентский ход с tool_calls (текст, если был, тоже сохраняем).
            messages.Add(OpenAiAssistantWithToolCalls(turn.Text, turn.ToolCalls));

            // Картинки от инструментов копим, чтобы после всех tool-ответов отдать
            // одним user-сообщением (в role:tool картинки класть нельзя).
            var pendingImages = new List<(string Label, byte[] Bytes)>();
            foreach (var tc in turn.ToolCalls)
            {
                status?.Report(DescribeToolActivityByName(tc.Name));
                var args = ParseArgs(tc.ArgumentsJson);
                var exec = await ExecuteToolAsync(tc.Name, args, sandboxRoot, userId, accessedFiles, generatedFiles, totalDocumentChars, totalToolImages);
                totalDocumentChars += exec.AddedChars;
                totalToolImages += exec.AddedImages;

                var toolText = exec.Images.Count > 0
                    ? exec.Text + $"\n({exec.Images.Count} изобр. приложено следующим сообщением.)"
                    : exec.Text;
                messages.Add(OpenAiToolMessage(tc.Id, toolText));
                foreach (var img in exec.Images)
                {
                    pendingImages.Add((exec.Text, img));
                }
            }

            if (pendingImages.Count > 0)
            {
                messages.Add(OpenAiUserImages(pendingImages));
            }
        }

        // Лимит итераций — просим финальный ответ без инструментов.
        var finalTurn = await PostChatCompletionAsync(model, PruneOpenAiImages(messages, KeepImageWindowMessages), null, cancellationToken);
        usage = usage.Add(finalTurn.Usage.InputTokens, finalTurn.Usage.CacheReadTokens, finalTurn.Usage.OutputTokens, finalTurn.Usage.ProviderCostUsd);
        if (string.IsNullOrWhiteSpace(finalTurn.Text))
        {
            throw new InvalidOperationException("Модель вернула пустой ответ — вероятно, объём данных объекта слишком большой. Сузьте область поиска.");
        }
        messages.Add(OpenAiAssistantText(finalTurn.Text));
        return new AgenticCompletionResult(finalTurn.Text, accessedFiles, generatedFiles, usage);
    }

    // ===== HTTP =====

    private async Task<OpenAiTurn> PostChatCompletionAsync(string model, List<object> messages, List<object>? tools, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = MaxOutputTokens,
            ["messages"] = messages,
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }

        var json = JsonSerializer.Serialize(body);

        // Узкий безопасный ретрай на 429/5xx (как у нативного пути): запрос отклонён
        // до генерации — повтор безопасен. Таймаут/отмену не ретраим.
        const int maxAttempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, OpenAiEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AggregatorKey);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            try
            {
                resp = await OpenAiHttp.SendAsync(req, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 3, 15)), cancellationToken);
                continue;
            }

            if ((resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500) && attempt < maxAttempts)
            {
                resp.Dispose();
                var delay = TimeSpan.FromSeconds(Math.Min(attempt * 3, 15));
                logger.LogWarning("OpenAI-путь: агрегатор отклонил ({Code}) — попытка {Attempt}/{Max}, повтор через {Delay}с", (int)resp.StatusCode, attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            var text = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                resp.Dispose();
                throw new InvalidOperationException($"Агрегатор вернул {(int)resp.StatusCode}: {Trim(text, 300)}");
            }
            resp.Dispose();
            return ParseTurn(text);
        }
    }

    private OpenAiTurn ParseTurn(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;

        var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";

        var toolCalls = new List<OpenAiToolCall>();
        if (message.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcs.EnumerateArray())
            {
                var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var fn = tc.GetProperty("function");
                var name = fn.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var argsRaw = fn.TryGetProperty("arguments", out var aEl)
                    ? (aEl.ValueKind == JsonValueKind.String ? aEl.GetString() ?? "{}" : aEl.GetRawText())
                    : "{}";
                if (!string.IsNullOrEmpty(name))
                {
                    toolCalls.Add(new OpenAiToolCall(id, name, argsRaw));
                }
            }
        }

        var usage = ParseUsage(root);
        LogOpenAiUsage(usage);
        return new OpenAiTurn(content, toolCalls, finish, usage);
    }

    private static TokenUsage ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var u))
        {
            return TokenUsage.Empty;
        }
        long prompt = u.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out var p) ? p : 0;
        long completion = u.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt64(out var comp) ? comp : 0;
        long cached = 0;
        if (u.TryGetProperty("prompt_tokens_details", out var ptd) && ptd.ValueKind == JsonValueKind.Object
            && ptd.TryGetProperty("cached_tokens", out var cel) && cel.TryGetInt64(out var cv))
        {
            cached = cv;
        }
        double costUsd = u.TryGetProperty("cost", out var costEl) && costEl.ValueKind == JsonValueKind.Number ? costEl.GetDouble() : 0;
        // InputTokens — «свежие» (не из кэша) + запись; CacheRead — отдельно.
        var freshInput = Math.Max(0, prompt - cached);
        return new TokenUsage(freshInput, cached, completion, costUsd);
    }

    private void LogOpenAiUsage(TokenUsage u) =>
        logger.LogInformation("OpenAI usage: input={Input} cacheRead={CacheRead} output={Output}", u.InputTokens, u.CacheReadTokens, u.OutputTokens);

    // ===== Сборка сообщений (OpenAI wire) =====

    private static object OpenAiSystemMessage(string text) => new Dictionary<string, object?>
    {
        ["role"] = "system",
        // Массив-контент с cache_control — расширение агрегатора: кэширует system у
        // gemini/qwen (у OpenAI-моделей кэш и так авто). Для claude этот путь не
        // используется.
        ["content"] = new object[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text, ["cache_control"] = new Dictionary<string, object?> { ["type"] = "ephemeral" } } },
    };

    private static object OpenAiUserMessage(string text, IReadOnlyList<byte[]>? images)
    {
        if (images is null || images.Count == 0)
        {
            return new Dictionary<string, object?> { ["role"] = "user", ["content"] = text };
        }
        var parts = new List<object> { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text } };
        parts.AddRange(images.Select(ImagePart));
        return new Dictionary<string, object?> { ["role"] = "user", ["content"] = parts };
    }

    private static object OpenAiUserImages(List<(string Label, byte[] Bytes)> images)
    {
        var parts = new List<object> { new Dictionary<string, object?> { ["type"] = "text", ["text"] = "Изображения по инструментам выше:" } };
        foreach (var (label, bytes) in images)
        {
            parts.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = label });
            parts.Add(ImagePart(bytes));
        }
        return new Dictionary<string, object?> { ["role"] = "user", ["content"] = parts };
    }

    private static object ImagePart(byte[] img) => new Dictionary<string, object?>
    {
        ["type"] = "image_url",
        ["image_url"] = new Dictionary<string, object?> { ["url"] = $"data:{SkiaImages.DetectMediaType(img)};base64,{Convert.ToBase64String(img)}" },
    };

    private static object OpenAiAssistantText(string text) =>
        new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = text };

    private static object OpenAiAssistantWithToolCalls(string? text, List<OpenAiToolCall> toolCalls) => new Dictionary<string, object?>
    {
        ["role"] = "assistant",
        ["content"] = string.IsNullOrEmpty(text) ? null : text,
        ["tool_calls"] = toolCalls.Select(tc => (object)new Dictionary<string, object?>
        {
            ["id"] = tc.Id,
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?> { ["name"] = tc.Name, ["arguments"] = tc.ArgumentsJson },
        }).ToList(),
    };

    private static object OpenAiToolMessage(string toolCallId, string content) =>
        new Dictionary<string, object?> { ["role"] = "tool", ["tool_call_id"] = toolCallId, ["content"] = content };

    // ===== Инструменты: Anthropic Tool -> OpenAI function =====

    private static object ToOpenAiFunction(Tool t) => new Dictionary<string, object?>
    {
        ["type"] = "function",
        ["function"] = new Dictionary<string, object?>
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["parameters"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = t.InputSchema.Properties,
                ["required"] = t.InputSchema.Required ?? [],
            },
        },
    };

    // ===== Прочее =====

    // Парсит arguments (JSON-строка) в словарь клонированных JsonElement (живут
    // после закрытия документа). Пустой/битый JSON -> пустой словарь.
    private static IReadOnlyDictionary<string, JsonElement> ParseArgs(string argumentsJson)
    {
        var dict = new Dictionary<string, JsonElement>();
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (d.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in d.RootElement.EnumerateObject())
                {
                    dict[p.Name] = p.Value.Clone();
                }
            }
        }
        catch (JsonException)
        {
            // битый JSON — вернём пустой словарь; исполнитель отдаст понятную ошибку
        }
        return dict;
    }

    // Выбрасывает картинки (image_url) из user-сообщений старше окна, заменяя контент
    // текст-заглушкой — как PruneOldImages у нативного пути (защита от раздувания
    // запроса). Возвращает НОВЫЙ список (исходный не мутируем — он в состоянии диалога).
    private static List<object> PruneOpenAiImages(List<object> messages, int keepLastMessages)
    {
        var cutoff = messages.Count - keepLastMessages;
        var result = new List<object>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (i >= cutoff || messages[i] is not IDictionary<string, object?> m || !MessageHasImage(m))
            {
                result.Add(messages[i]);
                continue;
            }
            result.Add(new Dictionary<string, object?>
            {
                ["role"] = m.TryGetValue("role", out var r) ? r : "user",
                ["content"] = "[изображения листов убраны из истории — нужные цифры уже перенесены в текст]",
            });
        }
        return result;
    }

    private static bool MessageHasImage(IDictionary<string, object?> m) =>
        m.TryGetValue("content", out var c) && c is IEnumerable<object> parts
        && parts.Any(p => p is IDictionary<string, object?> part && part.TryGetValue("type", out var t) && (t as string) == "image_url");

    private static string DescribeToolActivityByName(string name) => name switch
    {
        "list_files" => "Смотрю файлы…",
        "read_document" => "Читаю документ…",
        "search_document" => "Ищу в документе…",
        "document_overview" => "Смотрю листы…",
        "render_page" => "Рендерю лист…",
        "create_file" => "Готовлю файл…",
        _ => "Думаю…",
    };

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
