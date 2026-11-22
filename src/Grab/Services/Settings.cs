using System.Text.Json;

namespace Grab.Services;

public class AppSettings
{
    public string Dir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public string Mode { get; set; } = "video";        // video | audio
    public string VideoFormat { get; set; } = "mp4";   // mp4 | mkv | webm
    public string AudioFormat { get; set; } = "mp3";   // mp3 | m4a | opus | wav
    public int AudioBitrate { get; set; } = 320;
    public string Cookies { get; set; } = "";          // "" | chrome | edge | firefox | brave
    public bool AutoUpdateEngine { get; set; } = true;  // check for a new yt-dlp once a day
    public long LastEngineCheck { get; set; }           // ticks of the last check
    public bool AutoPreview { get; set; } = true;      // load the small player copy automatically
    public string CookiesFile { get; set; } = "";      // optional cookies.txt (wins over the browser)
}

public static class SettingsStore
{
    public static AppSettings Current { get; private set; } = new AppSettings();

    /// <summary>"file:path", "browser:name" or "" - what yt-dlp should use for cookies.</summary>
    public static string CookieSpec()
    {
        var c = Current;
        if (!string.IsNullOrEmpty(c.CookiesFile) && File.Exists(c.CookiesFile)) return "file:" + c.CookiesFile;
        if (!string.IsNullOrEmpty(c.Cookies)) return "browser:" + c.Cookies;
        return "";
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) return;
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile));
            if (s != null) Current = s;
        }
        catch
        {
            // keep defaults
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.SettingsFile,
                JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // not critical
        }
    }
}
