using GTARadioBridge.Core;
using GTARadioBridge.Models;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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

        StartButton.IsEnabled = false;
        StopButton.IsEnabled  = true;

        _slotManager = new SlotManager(_settings);
        _slotManager.StatusChanged   += OnStatusChanged;
        _slotManager.NowPlayingChanged += OnNowPlayingChanged;

        Log("Starting bridge...");
        try
        {
            await _slotManager.StartAsync();
            Log("Bridge running. Open GTA V and select Self Radio.");
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
            Description = "Select GTA V User Music folder",
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
            // Status dot
            StatusDot.Fill = status.IsRunning ? BrushReady : BrushEmpty;
            StatusText.Text = status.IsRunning
                ? $"Running — capturing into slot {status.CaptureSlot}"
                : "Idle";

            CapturedText.Text = status.CapturedSoFar.TotalSeconds > 0
                ? $"Captured: {status.CapturedSoFar:mm\\:ss}"
                : "";

            // Rebuild slot indicators
            SlotList.Items.Clear();
            for (int i = 0; i < status.States.Length; i++)
            {
                var panel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Width = 60,
                    Margin = new Thickness(0, 0, 8, 0)
                };

                var dot = new Border
                {
                    Width = 40, Height = 40,
                    CornerRadius = new CornerRadius(6),
                    Background = SlotBrush(status.States[i]),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var label = new TextBlock
                {
                    Text = SlotLabel(status.States[i]),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                dot.Child = label;

                var slotLabel = new TextBlock
                {
                    Text = $"Slot {i}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0)
                };

                panel.Children.Add(dot);
                panel.Children.Add(slotLabel);
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
        PathBox.Text = _settings.UserMusicPath;
        AutoStartCheck.IsChecked = _settings.AutoStartCapture;

        SelectComboByContent(BitRateBox,   _settings.BitRate.ToString());
        SelectComboByContent(SlotCountBox, _settings.SlotCount.ToString());
    }

    private void ReadSettingsFromUI()
    {
        _settings.UserMusicPath = PathBox.Text.Trim();
        _settings.AutoStartCapture = AutoStartCheck.IsChecked == true;

        if (int.TryParse((BitRateBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int br))
            _settings.BitRate = br;
        if (int.TryParse((SlotCountBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int sc))
            _settings.SlotCount = sc;
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

    private static SolidColorBrush SlotBrush(SlotManager.SlotState state) => state switch
    {
        SlotManager.SlotState.Capturing => BrushCapturing,
        SlotManager.SlotState.Ready     => BrushReady,
        SlotManager.SlotState.Playing   => BrushPlaying,
        SlotManager.SlotState.Spent     => BrushSpent,
        _                               => BrushEmpty
    };

    private static string SlotLabel(SlotManager.SlotState state) => state switch
    {
        SlotManager.SlotState.Capturing => "REC",
        SlotManager.SlotState.Ready     => "RDY",
        SlotManager.SlotState.Playing   => "▶",
        SlotManager.SlotState.Spent     => "✓",
        _                               => "—"
    };

    // ── Window close → minimise to tray ──────────────────────────────────────

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
