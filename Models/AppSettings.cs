using System;
using System.IO;
using Newtonsoft.Json;

namespace GTARadioBridge.Models;

public class AppSettings
{
    public string UserMusicPath { get; set; } = GetDefaultUserMusicPath();
    public int SlotCount { get; set; } = 6;
    public int PreloadThresholdSeconds { get; set; } = 20;
    public int BitRate { get; set; } = 192;
    public bool AutoStartCapture { get; set; } = false;
    public string CaptureDeviceId { get; set; } = "default";
    public float GainFactor { get; set; } = 2.0f;

    private static string GetDefaultUserMusicPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Rockstar Games", "GTA V", "User Music");
    }

    private static string SettingsFilePath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch { }
    }
}