using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace GTARadioBridge.Core;

public class GTAMonitor : IDisposable
{
    private TraceEventSession? _session;
    private Thread? _thread;
    private bool _running;
    private readonly string _userMusicPath;

    public event Action<string, long>? SlotReadProgress;
    public event Action<string>? SlotStartedPlaying;

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

    private void RunETW()
    {
        try
        {
            TraceEventSession.GetActiveSession(SessionName)?.Stop();
            using (_session = new TraceEventSession(SessionName))
            {
                _session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.FileIO |
                    KernelTraceEventParser.Keywords.FileIOInit);

                _session.Source.Kernel.FileIORead   += OnFileIORead;
                _session.Source.Kernel.FileIOCreate += OnFileIOCreate;
                _session.Source.Process();
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[ETW] Error: {ex.Message}"); }
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
