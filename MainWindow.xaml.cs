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

    public event Action<bool>? OnBridgeStateChanged;

    private static readonly SolidColorBrush BrushCapturing =
        new(Color.FromRgb(0xC7, 0xA1, 0x4C));
    private static readonly SolidColorBrush BrushReady =
        new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush BrushPlaying =
        new(Color.FromRgb(0x21, 0x96, 0xF3));
    private static readonly SolidColorBrush BrushEmpty =
        new(Color.FromRgb(0x44, 0x44, 0x44));
    private static readonly SolidColorBrush BrushSpent =
        new(Color.FromRgb(0x33, 0x33, 0x33));

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        LoadSettingsToUI();
        Log("Ready. Press Start Bridge to begin.");
        CheckVBCableOnStartup();
    }

    private void CheckVBCableOnStartup()
    {
        var devices = AudioCaptureService.GetOutputDevices();
        bool hasVBCable = devices.Any(d =>
            d.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase));

        if (!hasVBCable)
        {
            Log("⚠ 未检测到虚拟声卡（VB-Cable）。");
            Log("  建议安装以隔离游戏音效，点击 Start Bridge 了解详情。");
        }
        else
        {
            Log("✓ 检测到虚拟声卡，请在 Capture Device 中选择 CABLE Output。");
        }
    }

    // ── Bridge toggle (called from tray) ─────────────────────────────────────

    public void ToggleBridge()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_slotManager != null)
                Stop_Click(this, new RoutedEventArgs());
            else
                Start_Click(this, new RoutedEventArgs());
        });
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
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(
                        "https://vb-audio.com/Cable/")
                    { UseShellExecute = true });
                Log("已打开 VB-Cable 下载页面。");
                Log("安装完成后重启程序，在 Capture Device 中选择 CABLE Output。");
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

            OnBridgeStateChanged?.Invoke(true);
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

        void UpdateUI()
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled  = false;
            StatusText.Text       = "Idle";
            StatusDot.Fill        =
                new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }

        if (Dispatcher.CheckAccess())
            UpdateUI();
        else
            Dispatcher.Invoke(UpdateUI);

        Log("Bridge stopped.");
        OnBridgeStateChanged?.Invoke(false);
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

    private void GainSlider_ValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (GainLabel == null) return;
        GainLabel.Text = $"{e.NewValue:F1}x";
        if (_slotManager != null)
            _settings.GainFactor = (float)e.NewValue;
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
                    Width               = 40,
                    Height              = 40,
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
                    Foreground          =
                        new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin              = new Thickness(0, 4, 0, 0)
                });
                SlotList.Items.Add(panel);
            }
        });
    }

    private void OnNowPlayingChanged(NowPlayingWatcher.TrackInfo? track)
    {
        Dispatcher.Invoke(() =>
        {
            NowPlayingText.Text = track != null
                ? $"♪  {track.Artist} – {track.Title}"
                : "Not playing";
        });
    }

    // ── Settings helpers ──────────────────────────────────────────────────────

    private void LoadSettingsToUI()
    {
        PathBox.Text             = _settings.UserMusicPath;
        AutoStartCheck.IsChecked = _settings.AutoStartCapture;
        GainSlider.Value         = _settings.GainFactor;
        GainLabel.Text           = $"{_settings.GainFactor:F1}x";

        SelectComboByContent(BitRateBox,   _settings.BitRate.ToString());
        SelectComboByContent(SlotCountBox, _settings.SlotCount.ToString());

        DeviceBox.Items.Clear();
        var devices = AudioCaptureService.GetOutputDevices();
        foreach (var device in devices)
        {
            var item = new ComboBoxItem
                { Content = device.Name, Tag = device.Id };
            DeviceBox.Items.Add(item);
            if (device.Id == _settings.CaptureDeviceId)
                DeviceBox.SelectedItem = item;
        }
        if (DeviceBox.SelectedItem == null && DeviceBox.Items.Count > 0)
            DeviceBox.SelectedIndex = 0;
    }

    private void ReadSettingsFromUI()
    {
        _settings.UserMusicPath    = PathBox.Text.Trim();
        _settings.AutoStartCapture = AutoStartCheck.IsChecked == true;
        _settings.GainFactor       = (float)GainSlider.Value;

        if (int.TryParse(
            (BitRateBox.SelectedItem as ComboBoxItem)?.Content?.ToString(),
            out int br))
            _settings.BitRate = br;

        if (int.TryParse(
            (SlotCountBox.SelectedItem as ComboBoxItem)?.Content?.ToString(),
            out int sc))
            _settings.SlotCount = sc;

        if (DeviceBox.SelectedItem is ComboBoxItem deviceItem &&
            deviceItem.Tag is string deviceId)
            _settings.CaptureDeviceId = deviceId;
    }

    private static void SelectComboByContent(ComboBox box, string content)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (item.Content?.ToString() == content)
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    // ── Log ───────────────────────────────────────────────────────────────────

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            LogText.Text = _log.ToString();
            LogScroller.ScrollToEnd();
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SolidColorBrush SlotBrush(SlotManager.SlotState state) =>
        state switch
        {
            SlotManager.SlotState.Capturing => BrushCapturing,
            SlotManager.SlotState.Ready     => BrushReady,
            SlotManager.SlotState.Playing   => BrushPlaying,
            SlotManager.SlotState.Spent     => BrushSpent,
            _                               => BrushEmpty
        };

    private static string SlotLabel(SlotManager.SlotState state) =>
        state switch
        {
            SlotManager.SlotState.Capturing => "REC",
            SlotManager.SlotState.Ready     => "RDY",
            SlotManager.SlotState.Playing   => "▶",
            SlotManager.SlotState.Spent     => "✓",
            _                               => "—"
        };

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}