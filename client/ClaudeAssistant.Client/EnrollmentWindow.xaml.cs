using ClaudeAssistant.Client.Services;
using Microsoft.UI.Xaml;

namespace ClaudeAssistant.Client;

public partial class EnrollmentWindow : Window
{
    private readonly BackendClient _backendClient = new();

    public event Action? Bound;

    public EnrollmentWindow()
    {
        InitializeComponent();
        Title = "Claude Assistant — привязка устройства";
    }

    private async void OnBindClick(object sender, RoutedEventArgs e)
    {
        var code = CodeInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            StatusText.Text = "Введите код привязки";
            return;
        }

        BindButton.IsEnabled = false;
        StatusText.Text = "Привязка...";

        var token = TokenStorage.GenerateNewToken();
        var result = await _backendClient.BindDeviceAsync(code, token);

        if (!result.Success)
        {
            StatusText.Text = $"Не удалось привязать устройство: {result.ErrorMessage}";
            BindButton.IsEnabled = true;
            return;
        }

        TokenStorage.Save(token);
        StatusText.Text = $"Готово. Добро пожаловать, {result.UserFullName}!";
        Bound?.Invoke();
    }
}
