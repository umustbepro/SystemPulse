using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SystemPulse.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIconService(Action showWindow, Action exitApplication)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open SystemPulse", null, (_, _) => showWindow());
        menu.Items.Add("Exit", null, (_, _) => exitApplication());
        _icon = new Forms.NotifyIcon
        {
            Text = "SystemPulse v.06",
            Visible = true,
            ContextMenuStrip = menu,
            Icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
        };
        _icon.DoubleClick += (_, _) => showWindow();
    }

    public void Notify(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Warning;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
