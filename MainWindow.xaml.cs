using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GTARadioBridge.Core;
using GTARadioBridge.Models;

namespace GTARadioBridge;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private SlotManager? _slotManager;
    private readonly StringBuilder _log = new();

    private static readonly SolidColorBrush BrushCapturing = new(Color.FromRgb(0xC7, 0xA1, 0x4C));
    private static readonly SolidColorBrush BrushReady     = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush BrushPlaying   = new(Color.FromRgb(0x21, 0x96, 0xF3));
    private static readonly SolidColorBrush BrushEmpty     = new(Color.FromRgb(0x44, 0x44, 0x44));
    private static readonly SolidColorBrush BrushSpent     = new(Color.FromRgb(0x33, 0x33, 0x33));

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        LoadSettingsToUI();
        Log("Ready. Press Start Bridge to begin.");
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        ReadSettingsFromUI();

        if (!Directory.Exists(_settings.UserMusicPath))
        {
            MessageBox.Show(
                "User Music path not found:\n" + _settings.UserMusicPath,
                "Path Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 检测虚拟声卡
        var devices = AudioCaptureService.GetOutputDevices();
        bool hasVBCable = devices.Any(d =>
            d.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase));

        if (!hasVBCable && _settings.CaptureDeviceId == "default")
        {
            var result = MessageBox.Show(
                "未检测到虚拟声卡（VB-Cable）。\n\n" +
                "使用默认设备时，GTA 游戏音效会混入录音。\n" +
                "安装 VB-Cable 后可将 Apple Music 与游戏音效完全隔离。\n\n" +
                "是否前往下载页面？（免费，约 5 MB）\n\n" +
                "点击「否」继续使用默认设备。",
                "推荐安装虚拟声卡",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://vb-audio.com/Cable/") { UseShellExecute = true });
                Log("已打开 VB-Cable 下载页面。安装完成后重启程序，在 Capture Device 中选择 CABLE Output。");
                return;
            }
        }

        StartButton.IsEnabled = false;
        StopButton.IsEnabled  = true;

        _slotManager = new SlotManager(_settings);
        _slotManager.StatusChanged     += OnStatusChanged;
        _slotManager.NowPlayingChanged += OnNowPlayingChanged;

        Log("Starting bridge...");
        try
        {
            await _slotManager.StartAsync();
            Log("Bridge running. Open GTA V and select Self Radio.");

            if (hasVBCable && _settings.CaptureDeviceId != "default")
                Log("✓ 已使用虚拟声卡隔离，录音不含游戏音效。");
            else
                Log("⚠ 使用默认设备，游戏音效可能混入录音。");
        }
        catch (UnauthorizedAccessException)
        {
            Log("ERROR: ETW monitoring requires Administrator privileges.");
            Log("Right-click GTARadioBridge.exe → Run as administrator.");
            StopBridge();
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            StopBridge();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopBridge();

    private void StopBridge()
    {
        _slotManager?.Stop();
        _slotManager?.Dispose();
        _slotManager = null;

        Dispatcher.Invoke(() =>
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled  = false;
            StatusText.Text       = "Idle";
            StatusDot.Fill        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        });
        Log("Bridge stopped.");
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        ReadSettingsFromUI();
        _settings.Save();
        Log("Settings saved.");
    }

    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description  = "Select GTA V User Music folder",
            SelectedPath = PathBox.Text
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            PathBox.Text = dialog.SelectedPath;
    }

    // ── Status updates ────────────────────────────────────────────────────────

    private void OnStatusChanged(SlotManager.BridgeStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            StatusDot.Fill  = status.IsRunning ? BrushReady : BrushEmpty;
            StatusText.Text = status.IsRunning
                ? $"Running — capturing into slot {status.CaptureSlot}"
                : "Idle";

            CapturedText.Text = status.CapturedSoFar.TotalSeconds > 0
                ? $"Captured: {status.CapturedSoFar:mm\\:ss}"
                : "";

            SlotList.Items.Clear();
            for (int i = 0; i < status.States.Length; i++)
            {
                var panel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Width       = 60,
                    Margin      = new Thickness(0, 0, 8, 0)
                };
                var dot = new Border
                {
                    Width               = 40, Height = 40,
                    CornerRadius        = new CornerRadius(6),
                    Background          = SlotBrush(status.States[i]),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                dot.Child = new TextBlock
                {
                    Text                = SlotLabel(status.States[i]),
                    FontSize            = 10,
                    Foreground          = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment       = TextAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center
                };
                panel.Children.Add(dot);
                panel.Children.Add(new TextBlock
                {
                    Text                = $"Slot {i}",
                    FontSize            = 10,
                    Foreground          = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),