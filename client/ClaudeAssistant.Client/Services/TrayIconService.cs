using System.Drawing;
using System.Windows.Forms;

namespace ClaudeAssistant.Client.Services;

public class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Claude Assistant",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    // Маленький ненавязчивый toast (раздел 9 плана) — balloon tip через уже имеющийся
    // NotifyIcon, без отдельной интеграции с Windows.AppNotifications (для unpackaged
    // WinUI 3 приложения та требует AUMID/регистрацию — усложнение ради того же
    // визуального результата).
    public void ShowNotification(string title, string message)
    {
        _notifyIcon.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        GC.SuppressFinalize(this);
    }
}
