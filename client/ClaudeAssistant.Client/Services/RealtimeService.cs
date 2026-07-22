using Microsoft.AspNetCore.SignalR.Client;

namespace ClaudeAssistant.Client.Services;

public class RealtimeService : IAsyncDisposable
{
    private HubConnection? _connection;

    public event Action? TokenRevoked;
    public event Action? UpdateAvailable;
    // Живой статус обработки запроса ("Думаю…", "Читаю: …") — приходит, пока идёт
    // agentic-запрос. Best-effort, для отображения прогресса.
    public event Action<string>? RequestStatus;

    public async Task StartAsync()
    {
        if (!TokenStorage.TryLoad(out var token))
        {
            return;
        }

        _connection = new HubConnectionBuilder()
            .WithUrl(
                $"{AppConfig.BackendBaseUrl}/hubs/updates",
                options => options.Headers.Add("Authorization", $"Bearer {token}")
            )
            .WithAutomaticReconnect(new BackoffRetryPolicy())
            .Build();

        _connection.On("TokenRevoked", () => TokenRevoked?.Invoke());
        _connection.On("UpdateAvailable", () => UpdateAvailable?.Invoke());
        _connection.On<string>("RequestStatus", text => RequestStatus?.Invoke(text));

        try
        {
            await _connection.StartAsync();
        }
        catch
        {
            // Сервер недоступен при старте — переподключение подхватит автоматический
            // реконнект после первого успешного соединения; при полном отсутствии связи
            // на старте это не должно ронять приложение.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}
