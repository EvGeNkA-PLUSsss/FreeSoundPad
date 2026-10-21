using System.Drawing;
using Forms = System.Windows.Forms;

namespace FreeSoundPad.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _micItem;
    private readonly Forms.ToolStripMenuItem _browserItem;
    private readonly Forms.ToolStripMenuItem _overlayItem;
    private readonly Forms.ToolStripMenuItem _monitorItem;

    public TrayService(Icon icon)
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = "FreeSoundPad",
        };

        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColorTable()),
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(232, 238, 249),
            BackColor = Color.FromArgb(20, 26, 38),
        };

        var openItem = new Forms.ToolStripMenuItem("Открыть FreeSoundPad");
        openItem.Click += (_, _) => OpenRequested?.Invoke();

        _overlayItem = new Forms.ToolStripMenuItem("Показать оверлей", null, (_, _) => OverlayRequested?.Invoke());

        var stopItem = new Forms.ToolStripMenuItem("Стоп всё", null, (_, _) => StopAllRequested?.Invoke())
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+X",
        };

        _micItem = new Forms.ToolStripMenuItem("Микрофон в эфир", null, (_, _) => MicToggleRequested?.Invoke());
        _browserItem = new Forms.ToolStripMenuItem("Браузер в микрофон", null, (_, _) => BrowserToggleRequested?.Invoke());
        _monitorItem = new Forms.ToolStripMenuItem("Мониторинг в наушники", null, (_, _) => MonitorToggleRequested?.Invoke());

        var exitItem = new Forms.ToolStripMenuItem("Выход", null, (_, _) => ExitRequested?.Invoke());

        menu.Items.Add(openItem);
        menu.Items.Add(_overlayItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(stopItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_micItem);
        menu.Items.Add(_browserItem);
        menu.Items.Add(_monitorItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        foreach (Forms.ToolStripItem item in menu.Items)
        {
            item.ForeColor = Color.FromArgb(232, 238, 249);
        }

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public event Action? OpenRequested;
    public event Action? OverlayRequested;
    public event Action? StopAllRequested;
    public event Action? MicToggleRequested;
    public event Action? BrowserToggleRequested;
    public event Action? MonitorToggleRequested;
    public event Action? ExitRequested;

    public void UpdateState(bool micOn, bool browserOn, bool monitorOn, bool overlayVisible)
    {
        _micItem.Checked = micOn;
        _browserItem.Checked = browserOn;
        _monitorItem.Checked = monitorOn;
        _overlayItem.Text = overlayVisible ? "Скрыть оверлей" : "Показать оверлей";
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.ShowBalloonTip(2500);
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private sealed class DarkColorTable : Forms.ProfessionalColorTable
    {
        private static readonly Color Background = Color.FromArgb(20, 26, 38);
        private static readonly Color Border = Color.FromArgb(45, 56, 74);
        private static readonly Color Hover = Color.FromArgb(32, 42, 58);

        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuStripGradientBegin => Background;
        public override Color MenuStripGradientEnd => Background;
        public override Color ToolStripBorder => Border;
        public override Color ToolStripGradientBegin => Background;
        public override Color ToolStripGradientMiddle => Background;
        public override Color ToolStripGradientEnd => Background;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
