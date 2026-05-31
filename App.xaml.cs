using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace GTARadioBridge;

public partial class App : Application
{
    private NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private ToolStripMenuItem? _bridgeToggleItem;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mainWindow = new MainWindow();
        BuildTrayIcon();
        _mainWindow.Show();

        // 监听 bridge 状态变化，同步更新托盘菜单文字
        _mainWindow.OnBridgeStateChanged += isRunning =>
        {
            if (_bridgeToggleItem != null)
                _bridgeToggleItem.Text = isRunning
                    ? "■  Stop Bridge"
                    : "▶  Start Bridge";
        };
    }

    private void BuildTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Text    = "GTA Radio Bridge",
            Visible = true,
            Icon    = LoadIcon()
        };

        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("Open");
        openItem.Click += (_, _) => ShowWindow();

        _bridgeToggleItem = new ToolStripMenuItem("▶  Start Bridge");
        _bridgeToggleItem.Click += (_, _) =>
        {
            ShowWindow();
            _mainWindow?.ToggleBridge();
        };

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            _trayIcon.Visible = false;
            Shutdown();
        };

        menu.Items.Add(openItem);
        menu.Items.Add(_bridgeToggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var iconPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Resources", "tray_icon.ico");
            if (System.IO.File.Exists(iconPath))
                return new System.Drawing.Icon(iconPath);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}