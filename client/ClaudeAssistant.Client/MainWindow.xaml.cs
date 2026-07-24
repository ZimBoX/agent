using System.Collections.ObjectModel;
using ClaudeAssistant.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClaudeAssistant.Client;

// Второй элемент истории в сайдбаре расширенного вида — Title/Subtitle уже
// отформатированы в коде, чтобы не тащить форматирование дат в XAML-биндинги.
public record HistoryListItem(string Title, string Subtitle, HistoryEntry Entry);

// Сообщение в треде диалога (расширенный вид). IsUser — вопрос пользователя
// (follow-up), иначе ответ модели. Свойства видимости — чтобы в одном
// DataTemplate показывать нужный вариант пузыря без конвертеров/селектора.
public sealed class ChatMessage : System.ComponentModel.INotifyPropertyChanged
{
    public ChatMessage(bool isUser, string text, bool isPending = false)
    {
        IsUser = isUser;
        IsPending = isPending;
        _text = text;
    }

    public bool IsUser { get; }

    // Пузырь "печатает…": карточка ассистента с индикатором и живым статусом
    // ("Думаю…", "Читаю: договор.pdf"), пока идёт ответ. Текст обновляется по
    // SignalR (ShowLiveStatus), поэтому Text — с уведомлением; по готовности пузырь
    // убирается из треда и заменяется настоящим ответом.
    public bool IsPending { get; }

    private string _text;
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
            {
                return;
            }
            _text = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public Microsoft.UI.Xaml.Visibility UserVisibility =>
        IsUser ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility AssistantVisibility =>
        !IsUser && !IsPending ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility PendingVisibility =>
        IsPending ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    // Компактный вид имеет фиксированную ширину, а высота считается аддитивно:
    // базовая (обычный шаблон без доп. полей) плюс добавка за каждое видимое
    // необязательное поле — своё поле промпта и/или панель "объект" для поиска по
    // папке (они могут быть показаны одновременно, например для "Свой промпт").
    // См. ApplyCompactHeight / OnTemplateSelectionChanged.
    private const int CompactWidth = 380;
    private const int CompactHeightBase = 600;
    private const int CompactHeightCustomPromptExtra = 90;
    private const int CompactHeightObjectPanelExtra = 60;
    private static readonly SizeInt32 ExpandedSize = new(900, 640);

    private SizeInt32 CurrentCompactSize => new(CompactWidth, CurrentCompactHeight());

    private int CurrentCompactHeight()
    {
        var height = CompactHeightBase;
        if (CustomPromptBox.Visibility == Visibility.Visible)
        {
            height += CompactHeightCustomPromptExtra;
        }
        if (ObjectPanel.Visibility == Visibility.Visible)
        {
            height += CompactHeightObjectPanelExtra;
        }
        return height;
    }

    // Подгоняет высоту окна под текущий компактный контент — но только когда мы
    // действительно в компактном виде (в расширенном высоту трогать нельзя).
    private void ApplyCompactHeight()
    {
        if (_isExpanded)
        {
            return;
        }
        AppWindow.Resize(CurrentCompactSize);
    }

    private readonly BackendClient _backendClient = new();
    private readonly ObservableCollection<string> _filePaths = [];
    private readonly ObservableCollection<ChatMessage> _thread = [];
    // Пузырь "печатает…" в треде, пока идёт ответ (см. AddPendingBubble). null —
    // когда запрос не выполняется.
    private ChatMessage? _pending;
    private List<HistoryEntry>? _historyCache;
    private bool _allowClose;
    private bool _isExpanded;
    // true, пока идёт запрос к модели — по нему ShowLiveStatus решает, обновлять ли
    // статус приходящими по SignalR событиями.
    private bool _requestInFlight;

    // Диалог, который можно продолжить (получен из ответа execute). null —
    // продолжение недоступно (legacy-путь модели, просмотр из истории, или сессия
    // истекла). _lastAnswerText — текст последнего ответа модели для Копировать/Сохранить.
    private Guid? _conversationId;
    private string _lastAnswerText = string.Empty;

    // Папка, с которой запустили/дозапустили клиент через ПКМ в Проводнике —
    // если задана, побеждает над папкой прикреплённого файла (см. GetCurrentObjectRoot).
    private string? _launchedFolderPath;

    // Поднимается по клику на кнопку выхода в самом клиенте (в отличие от
    // TokenRevoked, который приходит с сервера) — App.xaml.cs подписывается на
    // это событие точно так же, как на RealtimeService.TokenRevoked, и
    // переиспользует тот же переход на экран привязки.
    public event Action? LogoutRequested;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Claude Assistant";
        AppWindow.Resize(CurrentCompactSize);
        // ApplicationIcon в csproj даёт иконку exe (Проводник/панель задач/ярлыки),
        // но не иконку заголовка окна — AppWindow её не наследует автоматически,
        // нужно выставлять отдельно.
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico"));

        // Трей-приложение: закрытие окна сворачивает в трей, а не завершает процесс —
        // выход только через пункт "Выход" в трее или программный логаут
        // (см. CloseForReal, используется при отзыве токена админом).
        AppWindow.Closing += (_, args) =>
        {
            if (_allowClose)
            {
                return;
            }
            args.Cancel = true;

            // Список файлов и папка-объект относятся к конкретному запросу — при
            // закрытии окна (сворачивании в трей) сбрасываем их, чтобы следующий
            // открытый запрос не унаследовал файлы от предыдущего, не связанного
            // с ним.
            _filePaths.Clear();
            _launchedFolderPath = null;
            UpdateObjectRootDisplay();

            AppWindow.Hide();
        };

        FilesList.ItemsSource = _filePaths;
        ThreadList.ItemsSource = _thread;

        // Файлы и (максимум одна) папка, переданные при запуске — например, из shell
        // extension через ПКМ в Explorer. Папка — не файл, прикрепить её как документ
        // нельзя, вместо этого она становится "объектом" для поиска (см. HandleLaunchArgs).
        // App.LaunchPaths, а не argv напрямую: при перезапуске через explorer.exe
        // (обход package identity суррогата верхнего меню) пути приходят из temp-файла.
        HandleLaunchArgs(App.LaunchPaths);

        Activated += async (_, _) => await RefreshTemplatesAsync();
        Activated += async (_, _) => await LoadCurrentUserOnceAsync();
    }

    private bool _templatesRefreshing;
    private bool _currentUserLoaded;

    // Общая обработка путей, пришедших при первом запуске или переданных от
    // вторичного процесса через SingleInstanceService — оба сценария (правый клик,
    // пока клиент ещё не открыт, и повторный клик, пока уже открыт) ведут себя
    // одинаково. Папок за раз поддерживается не больше одной — берём первую.
    private void HandleLaunchArgs(IReadOnlyList<string> paths)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            AddFile(path);
        }

        var folder = paths.FirstOrDefault(Directory.Exists);
        if (folder is not null)
        {
            _launchedFolderPath = folder;
            UpdateObjectRootDisplay();
        }
    }

    // Папка, которую сможет обыскать модель через list_files/read_document (см.
    // ChatCompletionService.CompleteWithFileSearchAsync на backend) — либо папка,
    // из которой запустили клиент через ПКМ в Проводнике, либо, если такой не
    // было, папка первого отмеченного файла. Ничего не запрашивается у backend
    // заранее — путь провалидируется уже там (см. SandboxedFileAccessService),
    // это только вычисление "что показать/отправить", без сетевого похода.
    private string? GetCurrentObjectRoot()
    {
        if (_launchedFolderPath is not null)
        {
            return _launchedFolderPath;
        }

        var firstFile = FilesList.SelectedItems.Cast<string>().FirstOrDefault();
        return firstFile is not null ? Path.GetDirectoryName(firstFile) : null;
    }

    // Только подпись для подтверждения — реальное значение для запроса всегда
    // считается заново в OnExecuteClick через GetCurrentObjectRoot(), так что
    // устаревшая подпись (если где-то забыли её обновить) не может привести к
    // отправке не того пути.
    private void UpdateObjectRootDisplay()
    {
        var allowsFileSearch = TemplateCombo.SelectedItem is TemplateInfo { AllowFileSearch: true };
        if (!allowsFileSearch)
        {
            ObjectPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ObjectPanel.Visibility = Visibility.Visible;

        var root = GetCurrentObjectRoot();
        ObjectRootText.Text = root is not null
            ? Path.GetFileName(root)
            : "не определён — прикрепите файл или запустите из папки в Проводнике";
    }

    private async Task LoadCurrentUserOnceAsync()
    {
        if (_currentUserLoaded)
        {
            return;
        }

        try
        {
            var me = await _backendClient.GetMeAsync();
            if (me?.FullName is null)
            {
                // Не помечаем как загруженное — попробуем ещё раз при следующей
                // активации окна (например, при открытии из трея). Иначе один
                // сетевой сбой в момент старта навсегда оставляет подпись пустой
                // до перезапуска.
                return;
            }

            _currentUserLoaded = true;
            UserNameText.Text = me.FullName;
            UserRoleText.Text = me.Role ?? "";
            UserNameTextExpanded.Text = me.FullName;
            UserRoleTextExpanded.Text = me.Role ?? "";
        }
        catch (HttpRequestException)
        {
            // Сеть недоступна в момент запроса — попробуем ещё раз при следующей
            // активации окна.
        }
    }

    // Специальный пункт в самом начале списка шаблонов — не привязан ни к какой
    // должности, доступен всегда, даже если для должности пользователя вообще нет
    // шаблонов. Id == Guid.Empty используется как признак "это не настоящий шаблон"
    // везде ниже по коду.
    // AllowFileSearch: true всегда — у произвольного промпта нет своего шаблона в
    // БД, откуда брать этот флаг, а поиск по объекту для него нужен ничуть не
    // меньше, чем для готовых шаблонов с этим флагом.
    private static readonly TemplateInfo CustomPromptOption = new(Guid.Empty, "✏️ Свой промпт...", AllowFileSearch: true);

    // Перезапрашивается при каждом появлении окна (открытие из трея), а не только
    // один раз при старте — иначе шаблон, добавленный/изменённый в админке, не
    // появлялся бы в уже запущенном клиенте без полного перезапуска приложения.
    private async Task RefreshTemplatesAsync()
    {
        if (_templatesRefreshing)
        {
            return;
        }
        _templatesRefreshing = true;

        try
        {
            var selectedId = (TemplateCombo.SelectedItem as TemplateInfo)?.Id;

            var templates = await _backendClient.GetTemplatesAsync();
            if (templates is null)
            {
                StatusText.Text = "Не удалось загрузить список шаблонов";
                return;
            }

            var items = new List<TemplateInfo> { CustomPromptOption };
            items.AddRange(templates);
            TemplateCombo.ItemsSource = items;

            // Если запуск пришёл с папкой (см. HandleLaunchArgs) — это явное новое
            // намерение пользователя ("хочу искать по этому объекту"), поэтому шаблон
            // с AllowFileSearch выбирается в приоритете перед сохранённым выбором.
            // Без этого папку было бы физически нечем подхватить: панель объекта
            // показывается только под шаблоном с этим флагом.
            var preferredForPendingObject = _launchedFolderPath is null ? null : templates.FirstOrDefault(t => t.AllowFileSearch);

            // Иначе сохраняем текущий выбор, если этот шаблон всё ещё доступен — иначе
            // каждое переоткрытие окна сбрасывало бы выбранный пользователем шаблон.
            // По умолчанию — "Свой промпт": большинству реальных запросов проще
            // описать словами, чем подбирать подходящий готовый шаблон из списка.
            var stillAvailable = selectedId is null ? null : items.FirstOrDefault(t => t.Id == selectedId);
            TemplateCombo.SelectedItem = preferredForPendingObject ?? stillAvailable ?? CustomPromptOption;

            // На случай, если TemplateCombo.SelectedItem структурно не изменился (запись
            // с теми же полями) и SelectionChanged из-за этого не переисполнился —
            // подпись объекта всё равно должна быть актуальной после каждого обновления.
            UpdateObjectRootDisplay();
        }
        catch (HttpRequestException)
        {
            // Сеть недоступна в момент запроса — оставляем прежний список, попробуем
            // ещё раз при следующем открытии окна.
        }
        finally
        {
            _templatesRefreshing = false;
        }
    }

    private void OnTemplateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var isCustomPrompt = TemplateCombo.SelectedItem is TemplateInfo { Id: var id } && id == Guid.Empty;
        CustomPromptBox.Visibility = isCustomPrompt ? Visibility.Visible : Visibility.Collapsed;

        UpdateObjectRootDisplay();

        // Высота окна зависит от того, показано ли своё поле промпта / поле объекта —
        // подгоняем после того, как их видимость выставлена выше.
        ApplyCompactHeight();
    }

    private async void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".docx");
        picker.FileTypeFilter.Add(".xlsx");
        picker.FileTypeFilter.Add(".pdf");

        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files)
        {
            AddFile(file.Path);
        }
    }

    // Новый файл сразу считается включённым в запрос (чекбокс отмечен) — пользователь
    // сам снимает отметку с тех файлов, которые отправлять не нужно.
    private void AddFile(string path)
    {
        if (_filePaths.Contains(path))
        {
            return;
        }

        _filePaths.Add(path);
        FilesList.SelectedItems.Add(path);
        UpdateObjectRootDisplay();
    }

    private void OnRemoveFilesClick(object sender, RoutedEventArgs e)
    {
        var selected = FilesList.SelectedItems.Cast<string>().ToList();
        foreach (var path in selected)
        {
            _filePaths.Remove(path);
        }
        UpdateObjectRootDisplay();
    }

    private void OnFilesSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateObjectRootDisplay();

    private async void OnExecuteClick(object sender, RoutedEventArgs e)
    {
        if (TemplateCombo.SelectedItem is not TemplateInfo template)
        {
            StatusText.Text = "Выберите шаблон";
            return;
        }

        // Объект — необязателен, даже когда панель видна: можно по-прежнему просто
        // прикрепить файлы руками, не имея никакой определённой папки-объекта вовсе
        // (см. проверку "нужен хотя бы один источник" чуть ниже).
        // Путь сетевого диска (X:\) конвертируем в UNC — бэкенд в другой сессии
        // букв сетевых дисков не видит, а UNC от сессии не зависит (см.
        // NetworkPathResolver).
        var objectFolderRaw = template.AllowFileSearch ? GetCurrentObjectRoot() : null;
        var objectFolder = objectFolderRaw is not null ? NetworkPathResolver.ToUncIfMapped(objectFolderRaw) : null;

        // В запрос уходят только отмеченные чекбоксом файлы — раньше здесь ошибочно
        // использовался весь _filePaths целиком, из-за чего неотмеченные файлы
        // (например, оставшиеся от предыдущего запуска) всё равно попадали в запрос.
        // Если выбран объект — файлы не обязательны: модель сама найдёт нужное
        // внутри его папки (см. RequestsController.Execute на backend).
        var selectedPaths = FilesList.SelectedItems.Cast<string>().ToList();
        if (selectedPaths.Count == 0 && objectFolder is null)
        {
            StatusText.Text = "Отметьте хотя бы один файл галочкой";
            return;
        }

        Guid? templateId;
        string? customPrompt;
        if (template.Id == Guid.Empty)
        {
            var promptText = CustomPromptBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(promptText))
            {
                StatusText.Text = "Опишите, что нужно сделать с документом(ами)";
                return;
            }
            templateId = null;
            customPrompt = promptText;
        }
        else
        {
            templateId = template.Id;
            customPrompt = null;
        }

        ExecuteButton.IsEnabled = false;
        BusyIndicator.IsActive = true;
        StatusText.Text = "Обрабатываем документ(ы)...";

        // Пока идёт запрос — показываем живой статус, приходящий с backend по
        // SignalR ("Думаю…", "Читаю: договор.pdf" и т.п. — см. ShowLiveStatus).
        // Раньше здесь был таймер "всё ещё обрабатываем" через 15с — теперь его
        // заменил настоящий прогресс.
        _requestInFlight = true;

        // Пока идёт запрос, автообновление не применяется (см. UpdateService) —
        // никогда не рвём работу пользователя посреди обработки.
        AppState.IsBusy = true;
        ExecuteResult result;
        try
        {
            result = await _backendClient.ExecuteAsync(templateId, customPrompt, selectedPaths, objectFolder);
        }
        finally
        {
            AppState.IsBusy = false;
            _requestInFlight = false;
        }

        BusyIndicator.IsActive = false;
        ExecuteButton.IsEnabled = true;

        if (!result.Success)
        {
            StatusText.Text = $"Ошибка: {result.ErrorMessage}";
            return;
        }

        StatusText.Text = string.IsNullOrEmpty(result.AccessedFiles)
            ? "Готово"
            : $"Готово · также просмотрено: {result.AccessedFiles}";
        StartThreadWithAnswer(result.ResultText ?? string.Empty, result.ConversationId);

        // Выполненный запрос попал в историю на сервере — раньше кэш сбрасывался,
        // но сам список никогда не перезапрашивался повторно, поэтому сайдбар
        // истории оставался пустым до следующего клика по иконке истории.
        _historyCache = null;
        await ExpandAsync();
        await LoadHistoryAsync();
    }

    // Живой статус обработки, приходящий с backend по SignalR ("Думаю…",
    // "Читаю: договор.pdf", "Просматриваю файлы…"). Показываем только пока идёт
    // запрос — чтобы запоздавшее событие не перетёрло финальный "Готово".
    public void ShowLiveStatus(string status)
    {
        if (_requestInFlight && !string.IsNullOrWhiteSpace(status))
        {
            StatusText.Text = status;
            // Тот же живой статус — в пузыре "печатает…" внутри треда (расширенный
            // вид), где нижняя строка StatusText не видна.
            if (_pending is not null)
            {
                _pending.Text = status;
            }
        }
    }

    // Добавляет в тред пузырь "печатает…" (индикатор + живой статус) на время
    // ответа. Убирается через RemovePendingBubble по завершении запроса.
    private void AddPendingBubble()
    {
        _pending = new ChatMessage(isUser: false, "Думаю…", isPending: true);
        _thread.Add(_pending);
        ScrollThreadToBottom();
    }

    private void RemovePendingBubble()
    {
        if (_pending is not null)
        {
            _thread.Remove(_pending);
            _pending = null;
        }
    }

    // Начинает новый тред: первый элемент — ответ на исходный запрос. Если диалог
    // продолжаем (conversationId != null) — показываем поле ввода follow-up.
    private void StartThreadWithAnswer(string answer, Guid? conversationId)
    {
        _thread.Clear();
        _pending = null;
        _lastAnswerText = answer;
        _thread.Add(new ChatMessage(isUser: false, answer));
        _conversationId = conversationId;
        SetFollowUpAvailable(conversationId is not null, hint: null);
        ScrollThreadToBottom();
    }

    private void SetFollowUpAvailable(bool available, string? hint)
    {
        FollowUpPanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        FollowUpHint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrEmpty(hint))
        {
            FollowUpHint.Text = hint;
        }
    }

    private void ScrollThreadToBottom()
    {
        // Обновляем layout, чтобы ScrollableHeight учёл только что добавленное
        // сообщение, и прокручиваем к последнему.
        ThreadScroll.UpdateLayout();
        ThreadScroll.ChangeView(null, ThreadScroll.ScrollableHeight, null);
    }

    // Enter — отправить (без Shift). AcceptsReturn=False, так что перевод строки в
    // поле и так не вставляется; здесь просто вешаем на Enter отправку.
    private void OnFollowUpKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && FollowUpSend.IsEnabled)
        {
            e.Handled = true;
            OnFollowUpSendClick(FollowUpSend, new RoutedEventArgs());
        }
    }

    private async void OnFollowUpSendClick(object sender, RoutedEventArgs e)
    {
        var text = FollowUpBox.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _conversationId is not { } conversationId)
        {
            return;
        }

        _thread.Add(new ChatMessage(isUser: true, text));
        FollowUpBox.Text = string.Empty;
        // Пузырь "печатает…" в треде — чтобы было видно, что идёт обработка (нижняя
        // строка статуса в расширенном виде не видна).
        AddPendingBubble();

        FollowUpSend.IsEnabled = false;
        FollowUpBox.IsEnabled = false;
        BusyIndicator.IsActive = true;
        StatusText.Text = "Обрабатываем...";
        AppState.IsBusy = true;
        _requestInFlight = true;

        ContinueResult result;
        try
        {
            result = await _backendClient.ContinueAsync(conversationId, text);
        }
        finally
        {
            AppState.IsBusy = false;
            _requestInFlight = false;
            BusyIndicator.IsActive = false;
        }

        // Запрос завершён (любым исходом) — убираем пузырь "печатает…".
        RemovePendingBubble();

        if (result.Expired)
        {
            _conversationId = null;
            SetFollowUpAvailable(false, "Сессия истекла — начните новый запрос, чтобы снова работать с документами.");
            StatusText.Text = "Сессия истекла";
            return;
        }

        FollowUpSend.IsEnabled = true;
        FollowUpBox.IsEnabled = true;

        if (!result.Success)
        {
            StatusText.Text = $"Ошибка: {result.ErrorMessage}";
            // Дублируем ошибку пузырём в треде — в расширенном виде StatusText не виден.
            _thread.Add(new ChatMessage(isUser: false, $"⚠️ Ошибка: {result.ErrorMessage}"));
            ScrollThreadToBottom();
            return;
        }

        _lastAnswerText = result.ResultText ?? string.Empty;
        _thread.Add(new ChatMessage(isUser: false, _lastAnswerText));
        StatusText.Text = string.IsNullOrEmpty(result.AccessedFiles)
            ? "Готово"
            : $"Готово · также просмотрено: {result.AccessedFiles}";
        ScrollThreadToBottom();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastAnswerText))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(_lastAnswerText);
        Clipboard.SetContent(package);
        StatusText.Text = "Скопировано";
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastAnswerText))
        {
            return;
        }

        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        // Markdown первым — ответ модели действительно приходит в markdown (см.
        // переход ResultBox на MarkdownTextBlock), .txt оставлен как запасной вариант.
        picker.FileTypeChoices.Add("Markdown", [".md"]);
        picker.FileTypeChoices.Add("Текстовый файл", [".txt"]);
        picker.SuggestedFileName = "результат";

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await Windows.Storage.FileIO.WriteTextAsync(file, _lastAnswerText);
        StatusText.Text = "Сохранено";
    }

    private async void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        await ExpandAsync();
        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        _historyCache ??= await _backendClient.GetHistoryAsync();
        HistoryList.ItemsSource = _historyCache?
            .Select(h => new HistoryListItem(
                // Заголовок — по сути ответа (первая осмысленная строка), а не
                // общее "Свой промпт": так записи различимы. Категория (шаблон или
                // "Свой промпт") и дата уходят в подпись.
                DeriveHistoryTitle(h),
                $"{h.TemplateTitle} · {h.CreatedAt.LocalDateTime:dd.MM HH:mm}",
                h
            ))
            .ToList();
    }

    // Короткий заголовок для записи истории: первая непустая строка ответа,
    // очищенная от markdown-разметки и обрезанная. Для неуспешных/пустых —
    // название шаблона (или "Свой промпт") как запасной вариант.
    private static string DeriveHistoryTitle(HistoryEntry h)
    {
        var source = !string.IsNullOrWhiteSpace(h.ResultText) ? h.ResultText : h.ErrorMessage;
        if (string.IsNullOrWhiteSpace(source))
        {
            return h.TemplateTitle;
        }

        var firstLine = source
            .Split('\n')
            .Select(l => l.Trim().TrimStart('#', '*', '-', '>', ' ', '`').Trim())
            .FirstOrDefault(l => l.Length > 0);
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return h.TemplateTitle;
        }

        firstLine = firstLine.Replace("**", "").Replace("__", "");
        const int maxLen = 52;
        return firstLine.Length > maxLen ? firstLine[..maxLen].TrimEnd() + "…" : firstLine;
    }

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryListItem item)
        {
            return;
        }

        // Запись из истории продолжить нельзя (диалог живёт только в памяти сервера
        // в пределах окна кэша) — показываем как одиночный ответ без поля follow-up.
        var text = item.Entry.ResultText ?? item.Entry.ErrorMessage ?? string.Empty;
        StartThreadWithAnswer(text, conversationId: null);
        StatusText.Text = string.IsNullOrEmpty(item.Entry.AccessedFiles)
            ? $"Из истории: {item.Entry.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm}"
            : $"Из истории: {item.Entry.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm} · также просмотрено: {item.Entry.AccessedFiles}";
    }

    private async void OnCollapseClick(object sender, RoutedEventArgs e) => await CollapseAsync();

    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        HistoryButton.IsEnabled = false;
        LogoutButton.IsEnabled = false;
        LogoutButtonExpanded.IsEnabled = false;

        // Отзываем своё же устройство на сервере, а не только забываем токен
        // локально — иначе в админке привязка выглядела бы всё ещё активной
        // (см. Этап 9: статус привязки в списке сотрудников).
        await _backendClient.LogoutAsync();
        LogoutRequested?.Invoke();
    }

    // Плавный переход между компактным окном-композером и расширенным видом с
    // историей — кроссфейд содержимого одновременно с анимированным ресайзом
    // самого окна (Win32-окна не умеют плавно менять размер сами по себе).
    private async Task ExpandAsync()
    {
        if (_isExpanded)
        {
            return;
        }
        _isExpanded = true;

        var resizeTask = AnimateResizeAsync(ExpandedSize);
        await FadeAsync(CompactRoot, 1, 0, 120);
        CompactRoot.Visibility = Visibility.Collapsed;
        ExpandedRoot.Opacity = 0;
        ExpandedRoot.Visibility = Visibility.Visible;
        await resizeTask;
        await FadeAsync(ExpandedRoot, 0, 1, 150);
    }

    private async Task CollapseAsync()
    {
        if (!_isExpanded)
        {
            return;
        }
        _isExpanded = false;

        var resizeTask = AnimateResizeAsync(CurrentCompactSize);
        await FadeAsync(ExpandedRoot, 1, 0, 120);
        ExpandedRoot.Visibility = Visibility.Collapsed;
        CompactRoot.Opacity = 0;
        CompactRoot.Visibility = Visibility.Visible;
        await resizeTask;
        await FadeAsync(CompactRoot, 0, 1, 150);
    }

    private async Task AnimateResizeAsync(SizeInt32 target, int durationMs = 220)
    {
        var start = AppWindow.Size;
        if (start.Width == target.Width && start.Height == target.Height)
        {
            return;
        }

        const int steps = 16;
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
            var width = (int)(start.Width + (target.Width - start.Width) * eased);
            var height = (int)(start.Height + (target.Height - start.Height) * eased);
            AppWindow.Resize(new SizeInt32(width, height));
            await Task.Delay(durationMs / steps);
        }
        AppWindow.Resize(target);
    }

    private static Task FadeAsync(UIElement element, double from, double to, int durationMs)
    {
        element.Opacity = from;

        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);

        var tcs = new TaskCompletionSource();
        storyboard.Completed += (_, _) => tcs.TrySetResult();
        storyboard.Begin();
        return tcs.Task;
    }

    // Настоящее закрытие в обход сворачивания в трей — используется при программном
    // логауте (отзыв токена админом через SignalR, либо кнопкой выхода в клиенте).
    public void CloseForReal()
    {
        _allowClose = true;
        Close();
    }

    // Вызывается, когда пользователь второй раз кликает пункт меню в Проводнике,
    // пока клиент уже открыт (см. App.xaml.cs + SingleInstanceService) — вместо
    // того чтобы плодить второй процесс/трей-значок, новый набор файлов просто
    // добавляется в уже открытое окно, которое выводится на передний план.
    public async void HandleExternalLaunch(IReadOnlyList<string> paths)
    {
        var hasFolder = paths.Any(Directory.Exists);
        HandleLaunchArgs(paths);

        if (_isExpanded)
        {
            await CollapseAsync();
        }

        AppWindow.Show();
        Activate();

        if (hasFolder)
        {
            // Окно уже открыто, и Activated в этот раз не сработает (оно и так
            // активно) — без явного вызова подходящий шаблон для новой папки
            // никогда бы не подхватился.
            await RefreshTemplatesAsync();
        }
    }
}
