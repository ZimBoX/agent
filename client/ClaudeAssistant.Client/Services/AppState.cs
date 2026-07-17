namespace ClaudeAssistant.Client.Services;

// Общий флаг "идёт запрос к модели" — обновление не применяется, пока он true
// (раздел 9 плана: никогда не рвём работу пользователя посреди запроса).
public static class AppState
{
    private static volatile bool _isBusy;

    public static event Action? BusyStateChanged;

    public static bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            BusyStateChanged?.Invoke();
        }
    }
}