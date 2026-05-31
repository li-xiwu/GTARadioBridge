using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using System.Diagnostics;

namespace GTARadioBridge.Core;

/// <summary>
/// Monitors GTA5.exe file I/O via ETW (Event Tracing for Windows) kernel events.
/// Raises events when GTA starts reading a new slot file so the SlotManager
/// can update its state without touching the game process.
///
/// NOTE: Requires the process to run as Administrator (ETW kernel provider needs elevation).
/// </summary>
public class GTAMonitor : IDisposable
{
    private TraceEventSession? _session;
    private Thread? _thread;
    private bool _running;
    private readonly string _userMusicPath;

    public event Action<string, long>? SlotReadProgress;  // (slotFileName, byteOffset)
    public event Action<string>? SlotStartedPlaying;      // slotFileName
    public event Action? GTAExited;

    private string? _currentSlot;
    private const string SessionName = "GTARadioBridgeETW";

    public GTAMonitor(string userMusicPath)
    {
        _userMusicPath = userMusicPath.ToLowerInvariant();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        _thread = new Thread(RunETW) { IsBackground = true, Name = "ETWMonitor" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _session?.Stop(); } catch { }
        _thread?.Join(2000);
    }

    // ── Private ──────────────────────────────────────────────────────────────

    private void RunETW()
    {
        try
        {
            // Clean up any stale session from a previous crash
            TraceEventSession.GetActiveSession(SessionName)?.Stop();

            using (_session = new TraceEventSession(SessionName))
            {
                _session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.FileIO |
                    KernelTraceEventParser.Keywords.FileIOInit);

                _session.Source.Kernel.FileIORead += OnFileIORead;
                _session.Source.Kernel.FileIOCreate += OnFileIOCreate;

                // Process() blocks until Stop() is called
                _session.Source.Process();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ETW] Error: {ex.Message}");
        }
    }

    private void OnFileIORead(FileIOReadWriteTraceData data)
    {
        if (!IsGTA(data.ProcessName)) return;
        if (!IsSlotFile(data.FileName, out string slotName)) return;

        SlotReadProgress?.Invoke(slotName, data.Offset);

        if (slotName != _currentSlot)
        {
            _currentSlot = slotName;
            SlotStartedPlaying?.Invoke(slotName);
        }
    }

    private void OnFileIOCreate(FileIOCreateTraceData data)
    {
        // GTA opening a slot file for the first time in this session
        if (!IsGTA(data.ProcessName)) return;
        if (!IsSlotFile(data.FileName, out string slotName)) return;

        if (slotName != _currentSlot)
        {
            _currentSlot = slotName;
            SlotStartedPlaying?.Invoke(slotName);
        }
    }

    private bool IsGTA(string processName) =>
        string.Equals(processName, "GTA5", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(processName, "GTA5.exe", StringComparison.OrdinalIgnoreCase);

    private bool IsSlotFile(string fullPath, out string slotName)
    {
        slotName = string.Empty;
        if (string.IsNullOrEmpty(fullPath)) return false;

        var lower = fullPath.ToLowerInvariant();
        if (!lower.Contains(_userMusicPath)) return false;
        if (!lower.Contains("gbridge_slot_")) return false;

        slotName = Path.GetFileName(fullPath);
        return true;
    }

    public void Dispose() => Stop();
}
