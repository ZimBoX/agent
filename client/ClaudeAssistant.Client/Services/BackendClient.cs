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
    string? AccessedFiles
);

public record ExecuteResult(bool Success, string? ResultText, string? ErrorMessage, string? AccessedFiles = null);

public record MeInfo(string? FullName, string? Role);

public class BackendClient
{
    // Явный таймаут чуть больше, чем backend сам себе даёт на один вызов модели
    // (240с, без повторов — см. ChatCompletionService, там же история, почему
    // именно так). Без этого использовался бы стандартный HttpClient.Timeout =
    // 100с — клиент сдавался бы раньше backend, причём именно на больших
    // agentic-запросах, которым честно нужно больше времени (реальный инцидент —
    // см. чеклист, Этап 11: 99с на ответ, 68k токенов, а клиент ничего не получил).
    private readonly HttpClient _http = new() { BaseAddress = new Uri(AppConfig.BackendBaseUrl), Timeout = TimeSpan.FromSeconds(260) };

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

    // Ровно один из двух: templateId (готовый шаблон) или customPrompt (свой текст).
    // objectFolder — необязательный, только для шаблонов с AllowFileSearch: реальный
    // абсолютный путь к папке (папка прикреплённого файла или папка, из которой
    // запустили клиент через Проводник — см. MainWindow.GetCurrentObjectRoot),
    // внутри которой модель сама сможет искать и читать файлы.
    public async Task<ExecuteResult> ExecuteAsync(Guid? templateId, string? customPrompt, IReadOnlyList<string> filePaths, string? objectFolder = null)
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

            var response = await _http.PostAsync("/api/requests/execute", content);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                return new ExecuteResult(false, null, errorText);
            }

            var body = await response.Content.ReadFromJsonAsync<ExecuteResponseBody>();
            return new ExecuteResult(true, body?.ResultText, null, body?.AccessedFiles);
        }
        catch (HttpRequestException ex)
        {
            return new ExecuteResult(false, null, $"Сервер недоступен: {ex.Message}");
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

    private void AttachAuth()
    {
        if (TokenStorage.TryLoad(out var token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private record BindResponseBody(Guid DeviceId, string UserFullName, string UserRole);

    private record ExecuteResponseBody(string ResultText, string? AccessedFiles);
}
