using System.Collections.ObjectModel;
using System.Linq;
using ClaudeAssistant.Client.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeAssistant.Client;

// Отдельное окно «режима чата» — общается с лёгкой моделью (backend Chat:Model),
// история в БД. Ответ печатается по токенам (SignalR ChatToken), финальный текст —
// авторитетный из HTTP-ответа. Переиспользует пузыри ChatMessage из MainWindow.
public sealed partial class ChatWindow : Window
{
    private readonly BackendClient _backend = new();
    private readonly RealtimeService _realtime;
    private readonly DispatcherQueue _dispatcher;

    private readonly ObservableCollection<ChatConversationInfo> _conversations = [];
    private readonly ObservableCollection<ChatMessage> _thread = [];

    private Guid? _conversationId;
    private ChatMessage? _pending;
    private bool _sending;
    private bool _suppressSelection;

    public ChatWindow(RealtimeService realtime)
    {
        InitializeComponent();
        Title = "Claude Assistant — чат";
        _realtime = realtime;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        ConversationsList.ItemsSource = _conversations;
        ThreadList.ItemsSource = _thread;

        // Токены ответа приходят не на UI-потоке — маршалим в окно.
        _realtime.ChatToken += OnChatToken;
        Closed += (_, _) => _realtime.ChatToken -= OnChatToken;

        _ = LoadConversationsAsync();
    }

    private async Task LoadConversationsAsync()
    {
        var list = await _backend.GetChatConversationsAsync();
        if (list is null)
        {
            return;
        }

        var selected = _conversationId;
        _suppressSelection = true;
        _conversations.Clear();
        foreach (var c in list)
        {
            _conversations.Add(c);
        }
        // Сохраняем выделение текущего диалога, если он в списке.
        if (selected is { } id)
        {
            var match = _conversations.FirstOrDefault(c => c.Id == id);
            if (match is not null)
            {
                ConversationsList.SelectedItem = match;
            }
        }
        _suppressSelection = false;
    }

    private void OnNewChatClick(object sender, RoutedEventArgs e)
    {
        _conversationId = null;
        _pending = null;
        _thread.Clear();
        _suppressSelection = true;
        ConversationsList.SelectedItem = null;
        _suppressSelection = false;
        InputBox.Focus(FocusState.Programmatic);
    }

    private async void OnConversationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _sending)
        {
            return;
        }
        if (ConversationsList.SelectedItem is ChatConversationInfo conversation)
        {
            await OpenConversationAsync(conversation.Id);
        }
    }

    private async Task OpenConversationAsync(Guid id)
    {
        _conversationId = id;
        _pending = null;
        _thread.Clear();

        var messages = await _backend.GetChatMessagesAsync(id);
        if (messages is null)
        {
            return;
        }
        foreach (var m in messages)
        {
            _thread.Add(new ChatMessage(isUser: m.IsUser, m.Text));
        }
        ScrollToBottom();
    }

    private void OnInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !_sending)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => _ = SendAsync();

    private async Task SendAsync()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _sending)
        {
            return;
        }

        _sending = true;
        SendButton.IsEnabled = false;
        InputBox.Text = string.Empty;

        _thread.Add(new ChatMessage(isUser: true, text));
        _pending = new ChatMessage(isUser: false, string.Empty, isPending: true);
        _thread.Add(_pending);
        ScrollToBottom();

        var result = await _backend.SendChatAsync(_conversationId, text);

        // Убираем пузырь стриминга.
        if (_pending is not null)
        {
            _thread.Remove(_pending);
            _pending = null;
        }

        if (!result.Success)
        {
            _thread.Add(new ChatMessage(isUser: false, $"⚠️ Ошибка: {result.ErrorMessage}"));
            ScrollToBottom();
        }
        else
        {
            // Финальный текст — авторитетный (на случай потери части стрим-токенов).
            _thread.Add(new ChatMessage(isUser: false, result.Reply ?? string.Empty));
            ScrollToBottom();

            var isNew = _conversationId is null;
            _conversationId = result.ConversationId;
            if (isNew)
            {
                // Новый диалог появился на сервере — обновляем сайдбар.
                await LoadConversationsAsync();
            }
        }

        _sending = false;
        SendButton.IsEnabled = true;
        InputBox.Focus(FocusState.Programmatic);
    }

    private void OnChatToken(Guid conversationId, string token)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_pending is not null)
            {
                _pending.Text += token;
                ScrollToBottom();
            }
        });
    }

    private void ScrollToBottom()
    {
        ThreadScroll.UpdateLayout();
        ThreadScroll.ChangeView(null, ThreadScroll.ScrollableHeight, null);
    }
}
