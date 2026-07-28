using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ClaudeAssistant.Client.Services;

public record BindDeviceResult(bool Success, string? UserFullName, string? UserRole, string? ErrorMessage);

public record TemplateInfo(Guid Id, string Title, bool AllowFileSearch = false);

public record HistoryEntry(
    Guid Id,
    Guid? TemplateId,
    string TemplateTitle,
    string FileNames,
    string Status,
    string? ResultText,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    string? AccessedFiles,
    IReadOnlyList<GeneratedFile>? GeneratedFiles = null
);

// Файл, сгенерированный моделью (create_file): Id — скачать через
// GET /api/files/{id}, FileName — с расширением (как показать/сохранить).
// Available=false — байты зачищены по сроку хранения (30 дней): в истории
// показываем имя с пометкой «файл удалён», без кнопки скачивания.
public record GeneratedFile(Guid Id, string FileName, bool Available = true)
{
    public string Label => Available ? FileName : $"{FileName} — файл удалён";
}

// ConversationId — если не null, ответ можно продолжить (ContinueAsync) в
// пределах окна кэша (1 час). null — продолжение недоступно.
public record ExecuteResult(bool Success, string? ResultText, string? ErrorMessage, string? AccessedFiles = null, Guid? ConversationId = null, IReadOnlyList<GeneratedFile>? Files = null);

// Expired=true — сессия истекла (410): продолжать нельзя, нужен новый запрос.
public record ContinueResult(bool Success, string? ResultText, string? ErrorMessage, string? AccessedFiles = null, bool Expired = false, IReadOnlyList<GeneratedFile>? Files = null);

public record MeInfo(string? FullName, string? Role);

public record ChatConversationInfo(Guid Id, string Title, DateTimeOffset UpdatedAt);

public record ChatMessageInfo(bool IsUser, string Text, DateTimeOffset CreatedAt, IReadOnlyList<GeneratedFile>? GeneratedFiles = null);

public record ChatSendResult(bool Success, Guid ConversationId, string? Title, string? Reply, string? ErrorMessage, IReadOnlyList<GeneratedFile>? Files = null);

public class BackendClient
{
    // Таймаут чуть больше, чем backend даёт на один вызов модели (900с — см.
    // ChatCompletionService). Раньше было 260с, но с большими agentic-запросами
    // (много файлов + веб-поиск) этого иногда не хватало. Теперь во время
    // обработки клиент видит живой статус ("Думаю…", "Читаю: …") по SignalR,
    // поэтому долгое ожидание — это видимый прогресс, а не "завис"; таймаут
    // оставлен только как страховка и потому щедрый.
    private readonly HttpClient _http = new() { BaseAddress = new Uri(AppConfig.BackendBaseUrl), Timeout = TimeSpan.FromSeconds(960) };

    public async Task<BindDeviceResult> BindDeviceAsync(string code, string token)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(
                "/api/enrollment/bind",
                new { code, token }
            );

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                return new BindDeviceResult(false, null, null, errorText);
            }

            var body = await response.Content.ReadFromJsonAsync<BindResponseBody>();
            return new BindDeviceResult(true, body?.UserFullName, body?.UserRole, null);
        }
        catch (HttpRequestException ex)
        {
            return new BindDeviceResult(false, null, null, $"Сервер недоступен: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return new BindDeviceResult(false, null, null, "Сервер долго не отвечает — попробуйте повторить чуть позже.");
        }
    }

    public async Task<List<TemplateInfo>?> GetTemplatesAsync()
    {
        AttachAuth();
        var response = await _http.GetAsync("/api/templates");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        return await response.Content.ReadFromJsonAsync<List<TemplateInfo>>();
    }

    public async Task<MeInfo?> GetMeAsync()
    {
        AttachAuth();
        var response = await _http.GetAsync("/api/me");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        return await response.Content.ReadFromJsonAsync<MeInfo>();
    }

    public async Task LogoutAsync()
    {
        AttachAuth();
        try
        {
            await _http.PostAsync("/api/me/logout", null);
        }
        catch (HttpRequestException)
        {
            // Сервер недоступен — не блокируем локальный выход из-за этого,
            // токен на клиенте всё равно будет забыт.
        }
    }

    public async Task<List<HistoryEntry>?> GetHistoryAsync()
    {
        AttachAuth();
        var response = await _http.GetAsync("/api/requests/history");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        return await response.Content.ReadFromJsonAsync<List<HistoryEntry>>();
    }

    public async Task<bool> DeleteHistoryAsync(Guid id)
    {
        AttachAuth();
        try
        {
            var response = await _http.DeleteAsync($"/api/requests/history/{id}");
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<bool> DeleteChatConversationAsync(Guid id)
    {
        AttachAuth();
        try
        {
            var response = await _http.DeleteAsync($"/api/chat/conversations/{id}");
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    // Ровно один из двух: templateId (готовый шаблон) или customPrompt (свой текст).
    // objectFolder — необязательный, только для шаблонов с AllowFileSearch: реальный
    // абсолютный путь к папке (папка прикреплённого файла или папка, из которой
    // запустили клиент через Проводник — см. MainWindow.GetCurrentObjectRoot),
    // внутри которой модель сама сможет искать и читать файлы.
    public async Task<ExecuteResult> ExecuteAsync(Guid? templateId, string? customPrompt, IReadOnlyList<string> filePaths, string? objectFolder = null, CancellationToken cancellationToken = default)
    {
        AttachAuth();

        using var content = new MultipartFormDataContent();
        if (templateId is { } id)
        {
            content.Add(new StringContent(id.ToString()), "templateId");
        }
        if (customPrompt is not null)
        {
            content.Add(new StringContent(customPrompt), "customPrompt");
        }
        if (objectFolder is not null)
        {
            content.Add(new StringContent(objectFolder), "objectFolder");
        }

        var fileStreams = new List<FileStream>();
        try
        {
            foreach (var path in filePaths)
            {
                var stream = File.OpenRead(path);
                fileStreams.Add(stream);
                var fileContent = new StreamContent(stream);
                content.Add(fileContent, "files", Path.GetFileName(path));
            }

            var response = await _http.PostAsync("/api/requests/execute", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                return new ExecuteResult(false, null, errorText);
            }

            var body = await response.Content.ReadFromJsonAsync<ExecuteResponseBody>();
            return new ExecuteResult(true, body?.ResultText, null, body?.AccessedFiles, body?.ConversationId, body?.Files);
        }
        catch (HttpRequestException ex)
        {
            return new ExecuteResult(false, null, $"Сервер недоступен: {ex.Message}");
        }
        // Пользователь нажал «Прервать» — наш токен отменён. Отличаем от таймаута
        // HttpClient (тот тоже TaskCanceledException, но наш токен НЕ отменён).
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ExecuteResult(false, null, "Прервано пользователем");
        }
        catch (TaskCanceledException)
        {
            // HttpClient.Timeout бросает именно это исключение (не
            // HttpRequestException) — без отдельного catch оно бы осталось
            // необработанным и не показало бы пользователю понятного сообщения.
            return new ExecuteResult(false, null, "Сервер долго не отвечает — попробуйте повторить запрос чуть позже.");
        }
        finally
        {
            foreach (var stream in fileStreams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    // Продолжает диалог по conversationId (получен из ExecuteAsync). В пределах
    // окна кэша (1 час) — иначе backend отвечает 410 и Expired=true.
    public async Task<ContinueResult> ContinueAsync(Guid conversationId, string message, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        AttachAuth();

        using var content = new MultipartFormDataContent { { new StringContent(message), "message" } };
        var fileStreams = new List<FileStream>();
        try
        {
            foreach (var path in filePaths)
            {
                var stream = File.OpenRead(path);
                fileStreams.Add(stream);
                content.Add(new StreamContent(stream), "files", Path.GetFileName(path));
            }

            var response = await _http.PostAsync($"/api/requests/{conversationId}/continue", content, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Gone)
            {
                return new ContinueResult(false, null, "Сессия истекла — начните новый запрос", Expired: true);
            }
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                return new ContinueResult(false, null, errorText);
            }

            var body = await response.Content.ReadFromJsonAsync<ContinueResponseBody>();
            return new ContinueResult(true, body?.ResultText, null, body?.AccessedFiles, Files: body?.Files);
        }
        catch (HttpRequestException ex)
        {
            return new ContinueResult(false, null, $"Сервер недоступен: {ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ContinueResult(false, null, "Прервано пользователем");
        }
        catch (TaskCanceledException)
        {
            return new ContinueResult(false, null, "Сервер долго не отвечает — попробуйте повторить запрос чуть позже.");
        }
        finally
        {
            foreach (var stream in fileStreams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    // ===== Режим чата =====

    public async Task<List<ChatConversationInfo>?> GetChatConversationsAsync()
    {
        AttachAuth();
        var response = await _http.GetAsync("/api/chat/conversations");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        return await response.Content.ReadFromJsonAsync<List<ChatConversationInfo>>();
    }

    public async Task<List<ChatMessageInfo>?> GetChatMessagesAsync(Guid conversationId)
    {
        AttachAuth();
        var response = await _http.GetAsync($"/api/chat/conversations/{conversationId}/messages");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        return await response.Content.ReadFromJsonAsync<List<ChatMessageInfo>>();
    }

    // Ответ приходит целиком по завершении (авторитетный текст); токены по мере
    // генерации — отдельно по SignalR (RealtimeService.ChatToken).
    public async Task<ChatSendResult> SendChatAsync(Guid? conversationId, string text, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        AttachAuth();

        using var content = new MultipartFormDataContent { { new StringContent(text), "text" } };
        if (conversationId is { } id)
        {
            content.Add(new StringContent(id.ToString()), "conversationId");
        }

        var fileStreams = new List<FileStream>();
        try
        {
            foreach (var path in filePaths)
            {
                var stream = File.OpenRead(path);
                fileStreams.Add(stream);
                content.Add(new StreamContent(stream), "files", Path.GetFileName(path));
            }

            var response = await _http.PostAsync("/api/chat/send", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                return new ChatSendResult(false, Guid.Empty, null, null, errorText);
            }
            var body = await response.Content.ReadFromJsonAsync<ChatSendResponseBody>();
            return new ChatSendResult(true, body!.ConversationId, body.Title, body.Reply, null, body.Files);
        }
        catch (HttpRequestException ex)
        {
            return new ChatSendResult(false, Guid.Empty, null, null, $"Сервер недоступен: {ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ChatSendResult(false, Guid.Empty, null, null, "Прервано пользователем");
        }
        catch (TaskCanceledException)
        {
            return new ChatSendResult(false, Guid.Empty, null, null, "Сервер долго не отвечает — попробуйте повторить чуть позже.");
        }
        finally
        {
            foreach (var stream in fileStreams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    // Скачивает сгенерированный моделью файл по Id. Возвращает байты или null
    // (файл истёк/недоступен). Имя файла у клиента уже есть из GeneratedFile.
    public async Task<byte[]?> DownloadGeneratedFileAsync(Guid id)
    {
        AttachAuth();
        try
        {
            var response = await _http.GetAsync($"/api/files/{id}");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private void AttachAuth()
    {
        if (TokenStorage.TryLoad(out var token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private record ChatSendResponseBody(Guid ConversationId, string Title, string Reply, List<GeneratedFile>? Files);

    private record BindResponseBody(Guid DeviceId, string UserFullName, string UserRole);

    private record ExecuteResponseBody(string ResultText, string? AccessedFiles, Guid? ConversationId, List<GeneratedFile>? Files);

    private record ContinueResponseBody(string ResultText, string? AccessedFiles, List<GeneratedFile>? Files);
}
