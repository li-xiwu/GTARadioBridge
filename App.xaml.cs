using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace GTARadioBridge;

public partial class App : Application
{
    private NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mainWindow = new MainWindow();

        BuildTrayIcon();

        // Show window on first launch
        _mainWindow.Show();
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

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            _trayIcon.Visible = false;
            Shutdown();
        };

        menu.Items.Add(openItem);
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
        // Use a built-in system icon as fallback if custom icon is missing
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
