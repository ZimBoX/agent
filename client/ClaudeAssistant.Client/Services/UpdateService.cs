using Velopack;
using Velopack.Sources;

namespace ClaudeAssistant.Client.Services;

public class UpdateService
{
    private readonly UpdateManager _manager;
    private UpdateInfo? _pendingUpdate;

    public UpdateService()
    {
        // AllowVersionDowngrade нужен для отката: если админ отзывает версию с
        // сервера ("retract"), фид снова указывает на более старую версию как на
        // "последнюю" — без этого флага UpdateManager считает более старую версию
        // не обновлением и игнорирует её.
        _manager = new UpdateManager(
            new SimpleWebSource($"{AppConfig.BackendBaseUrl}/releases"),
            new UpdateOptions { AllowVersionDowngrade = true }
        );
        AppState.BusyStateChanged += () => TryApplyIfIdle();
    }

    public bool IsInstalled => _manager.IsInstalled;

    public string? CurrentVersion => _manager.IsInstalled ? _manager.CurrentVersion?.ToString() : null;

    public async Task CheckAndDownloadAsync()
    {
        if (!_manager.IsInstalled)
        {
            // Запущено не из установленной через Velopack копии (например, из
            // bin/Debug при разработке) — самообновление здесь бессмысленно.
            return;
        }

        try
        {
            var updateInfo = await _manager.CheckForUpdatesAsync();
            if (updateInfo is null)
            {
                return;
            }

            await _manager.DownloadUpdatesAsync(updateInfo);
            _pendingUpdate = updateInfo;
            TryApplyIfIdle();
        }
        catch
        {
            // Сервер недоступен/сбой сети — просто не обновляемся сейчас, попробуем
            // на следующее событие "доступно обновление".
        }
    }

    // Вызывается и сразу после скачки, и при каждой смене AppState.IsBusy — так
    // отложенное обновление подхватывается сразу, как только пользователь освобождает
    // приложение, без отдельного опроса.
    public void TryApplyIfIdle()
    {
        if (_pendingUpdate is null || AppState.IsBusy)
        {
            return;
        }

        var update = _pendingUpdate;
        _pendingUpdate = null;
        try
        {
            _manager.ApplyUpdatesAndRestart(update);
        }
        catch
        {
            // Вызывается и из AppState.BusyStateChanged (без своего try/catch) —
            // сбой применения не должен уронить процесс необработанным исключением,
            // попробуем ещё раз на следующее событие "доступно обновление".
        }
    }
}
