using ClaudeAssistant.Client.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace ClaudeAssistant.Client;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private EnrollmentWindow? _enrollmentWindow;
    private TrayIconService? _trayIcon;
    private RealtimeService? _realtime;
    private UpdateService? _updateService;
    private SingleInstanceService? _singleInstance;
    private DispatcherQueue? _dispatcherQueue;

    public App()
    {
        // Должно быть самой первой операцией — до InitializeComponent() и вообще
        // до любой другой логики. Velopack перехватывает служебные запуски
        // (install/uninstall/после-обновления хуки) и в этих случаях завершает
        // процесс сам, не доходя до обычного старта приложения.
        VelopackApp.Build().Run();

        // Второй важный ранний выход: если клиент уже запущен, этот процесс не
        // должен создавать вообще ничего (ни окна, ни трей-значка) — раньше
        // каждый клик по пункту меню в Проводнике плодил новый трей-значок.
        // Вместо этого передаём пути файлов уже работающему процессу через
        // именованный канал и сразу завершаемся.
        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimaryInstance)
        {
            var paths = Environment.GetCommandLineArgs().Skip(1).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            SingleInstanceService.ForwardToPrimaryAsync(paths).GetAwaiter().GetResult();
            Environment.Exit(0);
            return;
        }

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        if (TokenStorage.TryLoad(out _))
        {
            StartMainApp();
        }
        else
        {
            ShowEnrollment();
        }
    }

    private void ShowEnrollment()
    {
        _enrollmentWindow = new EnrollmentWindow();
        _enrollmentWindow.Bound += () =>
        {
            // Важно: сначала поднимаем MainWindow (создаёт реальный HWND, держит
            // процесс живым), и только потом закрываем окно привязки — иначе между
            // закрытием одного окна и появлением другого HWND-ов нет вообще, и
            // WinUI 3 завершает процесс как "закрыто последнее окно".
            StartMainApp();
            _enrollmentWindow?.Close();
            _enrollmentWindow = null;
        };
        _enrollmentWindow.Activate();
    }

    private void StartMainApp()
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();

        // Если приложение запущено с файлами или папкой в аргументах (так будет вызывать
        // shell extension через ПКМ в Explorer — Этап 8) — сразу показываем окно, это и
        // есть весь смысл сценария. Без аргументов — обычный тихий старт в трее.
        var launchedWithPaths = Environment.GetCommandLineArgs().Skip(1).Any(p => File.Exists(p) || Directory.Exists(p));
        if (!launchedWithPaths)
        {
            _mainWindow.AppWindow.Hide();
        }

        _trayIcon = new TrayIconService();
        _trayIcon.OpenRequested += () => _mainWindow?.AppWindow.Show();
        _trayIcon.ExitRequested += () =>
        {
            _trayIcon?.Dispose();
            Environment.Exit(0);
        };

        _mainWindow.LogoutRequested += OnTokenRevoked;

        // Второй и последующие запуски (из Проводника, пока клиент уже открыт)
        // приходят сюда через именованный канал вместо создания нового процесса
        // (см. App() и SingleInstanceService).
        _singleInstance?.StartListening();
        if (_singleInstance is not null)
        {
            _singleInstance.FilesReceived += files =>
                _dispatcherQueue?.TryEnqueue(() => _mainWindow?.HandleExternalLaunch(files));
        }

        _realtime = new RealtimeService();
        _realtime.TokenRevoked += OnTokenRevoked;
        _realtime.UpdateAvailable += OnUpdateAvailable;
        _ = _realtime.StartAsync();

        _updateService = new UpdateService();
        NotifyIfJustUpdated();

        // На случай, если push о новом релизе пришёл, пока приложение было закрыто —
        // не полагаемся только на SignalR-событие (раздел 9 плана: fallback-проверка
        // при запуске).
        _ = _updateService.CheckAndDownloadAsync();
    }

    // Сравнивает текущую версию с последней запомненной — если отличается, значит
    // приложение только что обновилось (само или вручную), показываем тост один раз.
    private void NotifyIfJustUpdated()
    {
        if (_updateService is null || !_updateService.IsInstalled)
        {
            return;
        }

        var currentVersion = _updateService.CurrentVersion;
        if (currentVersion is null)
        {
            return;
        }

        var versionFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeAssistant",
            "last-known-version.txt"
        );

        var lastVersion = File.Exists(versionFilePath) ? File.ReadAllText(versionFilePath).Trim() : null;
        if (lastVersion is not null && lastVersion != currentVersion)
        {
            _trayIcon?.ShowNotification("Claude Assistant", $"Обновлено до v{currentVersion}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(versionFilePath)!);
        File.WriteAllText(versionFilePath, currentVersion);
    }

    private void OnTokenRevoked()
    {
        // Обработчики SignalR вызываются не на UI-потоке — обязательно маршалим,
        // прежде чем трогать окна.
        _dispatcherQueue?.TryEnqueue(async () =>
        {
            TokenStorage.Clear();

            _trayIcon?.Dispose();
            _trayIcon = null;

            if (_realtime is not null)
            {
                await _realtime.DisposeAsync();
                _realtime = null;
            }

            // Та же ловушка, что и при привязке: нельзя сначала закрыть последнее
            // окно с HWND — WinUI 3 тут же завершит процесс. Сначала поднимаем
            // EnrollmentWindow (новый HWND уже есть), и только потом закрываем старое.
            var oldMainWindow = _mainWindow;
            _mainWindow = null;

            ShowEnrollment();

            oldMainWindow?.CloseForReal();
        });
    }

    private void OnUpdateAvailable()
    {
        // Скачивание идёт в фоне и не трогает UI — не обязательно маршалить на
        // UI-поток, но применение (через AppState.BusyStateChanged) само разберётся,
        // когда безопасно перезапуститься.
        _ = _updateService?.CheckAndDownloadAsync();
    }
}
