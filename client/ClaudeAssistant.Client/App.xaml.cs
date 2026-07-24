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

    // Пути (файлы/папка), с которыми запущен клиент — вычисляются один раз при
    // старте. Обычно это аргументы командной строки, но при перезапуске через
    // explorer.exe (см. RelaunchIfForeignIdentity) аргументы теряются, и пути
    // берутся из временного файла.
    private static List<string>? _launchPaths;

    internal static IReadOnlyList<string> LaunchPaths => _launchPaths ??= ComputeLaunchPaths();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, System.Text.StringBuilder? packageFullName);

    private static string PendingLaunchFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeAssistant",
            "pending-launch.txt"
        );

    // Если процесс запущен с чужой package identity (это бывает при активации
    // через пункт верхнего меню Windows 11 — CLSID поднимается в packaged-COM
    // суррогате dllhost с identity нашего sparse-пакета ShellExtension, и
    // дочерний процесс наследует её через токен; documented desktop-app breakaway
    // эту identity НЕ снимает — проверено), приложение падает: WinUI резолвит
    // ms-appx:///-ресурсы и MRT относительно чужого пакета, где ресурсов клиента
    // нет, и MainWindow.xaml не загружается (0xc000027b в Microsoft.ui.xaml.dll).
    //
    // Explorer.exe — уже запущенный процесс оболочки БЕЗ package identity; когда
    // он запускает наш exe, тот стартует чистым. Аргументы explorer не пробрасывает,
    // поэтому пути передаём через временный файл. Возвращает true, если перезапуск
    // инициирован и текущий процесс должен немедленно завершиться.
    private static bool RelaunchIfForeignIdentity()
    {
        try
        {
            int len = 0;
            // 15700 = APPMODEL_ERROR_NO_PACKAGE — identity нет, всё нормально,
            // перезапуск не нужен (обычный запуск или из legacy-меню).
            if (GetCurrentPackageFullName(ref len, null) == 15700)
            {
                return false;
            }

            var exePath = Environment.ProcessPath;
            if (exePath is null)
            {
                return false;
            }

            var paths = Environment
                .GetCommandLineArgs()
                .Skip(1)
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(PendingLaunchFilePath)!);
            File.WriteAllLines(PendingLaunchFilePath, paths);

            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{exePath}\"") { UseShellExecute = true }
            );
            return true;
        }
        catch
        {
            // Если перезапуск не удался — лучше продолжить обычным путём (пусть
            // даже с риском падения), чем молча ничего не сделать.
            return false;
        }
    }

    private static List<string> ComputeLaunchPaths()
    {
        var cmdPaths = Environment
            .GetCommandLineArgs()
            .Skip(1)
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToList();
        if (cmdPaths.Count > 0)
        {
            return cmdPaths;
        }

        // Аргументов нет — возможно, это перезапуск через explorer.exe; забираем
        // пути из временного файла (только если он свежий — чтобы не подхватить
        // случайно оставшийся от прошлого раза).
        try
        {
            var file = PendingLaunchFilePath;
            if (File.Exists(file) && DateTimeOffset.Now - File.GetLastWriteTime(file) < TimeSpan.FromSeconds(30))
            {
                var paths = File.ReadAllLines(file).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                File.Delete(file);
                return paths;
            }
        }
        catch
        {
            // Отсутствие/ошибка чтения файла — просто нет путей запуска.
        }

        return [];
    }

    public App()
    {
        // Самая первая проверка: запуск с чужой package identity (из
        // packaged-COM суррогата верхнего меню Win11) гарантированно уронит
        // WinUI при загрузке MainWindow.xaml — вместо этого перезапускаемся
        // чистым процессом через explorer.exe (см. RelaunchIfForeignIdentity).
        if (RelaunchIfForeignIdentity())
        {
            Environment.Exit(0);
            return;
        }

        // Должно быть самой первой операцией — до InitializeComponent() и вообще
        // до любой другой логики. Velopack перехватывает служебные запуски
        // (install/uninstall/после-обновления хуки) и в этих случаях завершает
        // процесс сам, не доходя до обычного старта приложения.
        // Регистрация пункта меню Проводника (см. ShellExtensionRegistrar) вешается
        // именно сюда, а не в отдельный post-install скрипт — у `vpk pack` нет
        // флага для произвольного пост-install хука, а у VelopackApp есть.
        // ShellExtensionRegistrar (HKCU, прав администратора не требует) —
        // срабатывает и на установку, и на каждое обновление (например, если
        // версия comhost.dll в поставке изменилась) — идемпотентно, повторный
        // вызов безопасен. MsixSparsePackageRegistrar.Register() — только на
        // install: требует LocalMachine-хранилище сертификатов + HKLM AppModelUnlock
        // и запрашивает свой UAC, а дёргать его при каждом тихом автообновлении было
        // бы неожиданно. Но на апдейте нужна ReRegister() — Velopack заменяет файлы,
        // и привязка sparse-пакета рвётся (пункт верхнего меню пропадает); ReRegister
        // делает только per-user Add-AppxPackage (сертификат/AppModelUnlock уже
        // выставлены при установке и переживают апдейты), поэтому UAC не требуется.
        // OnBeforeUninstallFastCallback — обратная операция: без неё удаление
        // через "Установка и удаление программ" сносило бы только то, что
        // поставил сам Velopack, а наши собственные ключи реестра и
        // зарегистрированный sparse-пакет оставались бы висеть в системе.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ =>
            {
                ShellExtensionRegistrar.Register();
                MsixSparsePackageRegistrar.Register();
            })
            .OnAfterUpdateFastCallback(_ =>
            {
                ShellExtensionRegistrar.Register();
                MsixSparsePackageRegistrar.ReRegister();
            })
            .OnBeforeUninstallFastCallback(_ =>
            {
                ShellExtensionRegistrar.Unregister();
                MsixSparsePackageRegistrar.Unregister();
            })
            .Run();

        // Второй важный ранний выход: если клиент уже запущен, этот процесс не
        // должен создавать вообще ничего (ни окна, ни трей-значка) — раньше
        // каждый клик по пункту меню в Проводнике плодил новый трей-значок.
        // Вместо этого передаём пути файлов уже работающему процессу через
        // именованный канал и сразу завершаемся. Пути — через LaunchPaths, а не
        // напрямую из argv: при перезапуске через explorer они лежат в temp-файле.
        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimaryInstance)
        {
            SingleInstanceService.ForwardToPrimaryAsync(LaunchPaths.ToList()).GetAwaiter().GetResult();
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

        // Если приложение запущено с файлами или папкой (аргументы или temp-файл
        // перезапуска через explorer — см. LaunchPaths) — сразу показываем окно,
        // это и есть весь смысл сценария. Без путей — обычный тихий старт в трее.
        var launchedWithPaths = LaunchPaths.Count > 0;
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
        // Живой статус обработки приходит не на UI-потоке — маршалим в окно.
        _realtime.RequestStatus += status =>
            _dispatcherQueue?.TryEnqueue(() => _mainWindow?.ShowLiveStatus(status));
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
