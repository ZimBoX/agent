using System.Text.Json;

namespace ClaudeAssistant.Client.Services;

public static class AppConfig
{
    public static string BackendBaseUrl { get; } = Load();

    private static string Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return "http://localhost:5149";
        }

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.TryGetProperty("BackendBaseUrl", out var value)
            ? value.GetString() ?? "http://localhost:5149"
            : "http://localhost:5149";
    }
}
