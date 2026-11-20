using System.Text.Json;

namespace Grab.Services;

public static class YtDlp
{
    static string Str(JsonElement e, string key)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }

    static double Num(JsonElement e, string key)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return 0;
    }

    static double FormatSize(JsonElement f)
    {
        var s = Num(f, "filesize");
        return s > 0 ? s : Num(f, "filesize_approx");
    }

    public static string Friendly(string err)
    {
        var e = (err ?? "").Trim();
        var low = e.ToLowerInvariant();
        var table = new (string Key, string Msg)[]
        {
            ("unsupported url", "This link isn't supported."),
            ("video unavailable", "This video is unavailable (it may be deleted or blocked in your region)."),
            ("private video", "This video is private."),
            ("sign in to confirm", "The site wants verification. Choose a browser in Settings > Browser cookies and try again."),
            ("sign in", "This video needs a login. Choose a browser in Settings > Browser cookies."),
            ("confirm your age", "Age verification is required. Choose a browser in Settings > Browser cookies."),
            ("ffmpeg", "ffmpeg not found. Put ffmpeg.exe and ffprobe.exe in the bin folder."),
            ("unable to download webpage", "Couldn't reach the site. Check your internet connection."),
            ("getaddrinfo", "You seem to be offline."),
            ("http error 429", "The site is temporarily rate-limiting you. Wait a bit and try again."),
            ("could not copy", "Couldn't read browser cookies. Close the browser and try again."),
        };
        foreach (var (key, msg) in table)
            if (low.Contains(key)) return msg;

        // strip the "ERROR: [site] id:" prefix
        var idx = e.IndexOf("ERROR:", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) e = e.Substring(idx + 6).Trim();
        if (e.StartsWith("[")) { var close = e.IndexOf(']'); if (close > 0) e = e.Substring(close + 1).Trim(); }
        if (e.Length == 0) return "Unknown error.";
        return e.Length > 220 ? e.Substring(0, 220) + "…" : e;
    }

    public static async Task<string> VersionAsync()
    {
        var exe = Tools.Find("yt-dlp");
        if (exe == null) return "";
        try
        {
            var r = await Proc.RunAsync(exe, new[] { "--version" });
            return r.Out.Trim();
        }
        catch { return ""; }
    }

    public static async Task<string> UpdateAsync()
    {
        var exe = Tools.Find("yt-dlp");
        if (exe == null) return "yt-dlp not found.";
        try
        {
            var r = await Proc.RunAsync(exe, new[] { "-U" });
            var lines = (r.Out + "\n" + r.Err).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length > 0 ? lines[lines.Length - 1] : "Done.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static async Task<(VideoInfo Info, string Error)> GetInfoAsync(string url, string cookies)
    {
        var exe = Tools.Find("yt-dlp");
        if (exe == null) return (null, "yt-dlp not found. Put yt-dlp.exe in the bin folder.");

        var args = new List<string> { "-J", "--no-playlist", "--no-warnings", "--encoding", "utf-8" };
        if (!string.IsNullOrEmpty(cookies)) { args.Add("--cookies-from-browser"); args.Add(cookies); }
        args.Add(url);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        (int Code, string Out, string Err) r;
        try { r = await Proc.RunAsync(exe, args, cts.Token); }
        catch (Exception ex) { return (null, ex.Message); }
        if (cts.IsCancellationRequested) return (null, "Timed out. Try again.");

        if (r.Code != 0)
        {
            var errLines = r.Err.Split('\n').Where(l => l.Contains("ERROR")).ToList();
            return (null, Friendly(errLines.Count > 0 ? errLines[errLines.Count - 1] : r.Err));
        }

        try
        {
            using var doc = JsonDocument.Parse(r.Out);
            var root = doc.RootElement;
            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0)
                root = entries[0];

            var info = new VideoInfo
            {
                Title = Str(root, "title"),
                Channel = Str(root, "uploader") != "" ? Str(root, "uploader") : Str(root, "channel"),
                Duration = Num(root, "duration"),
                Thumb = Str(root, "thumbnail"),
            };

            var heights = new Dictionary<int, double>();
            if (root.TryGetProperty("formats", out var fmts) && fmts.ValueKind == JsonValueKind.Array)
            {
                double bestAbr = -1, audioSize = 0;
                foreach (var f in fmts.EnumerateArray())
                {
                    var v = Str(f, "vcodec");
                    var a = Str(f, "acodec");
                    if (v == "none" && a != "" && a != "none")
                    {
                        var abr = Num(f, "abr");
                        if (abr <= 0) abr = Num(f, "tbr");
                        if (abr >= bestAbr) { bestAbr = abr; audioSize = FormatSize(f); }
                    }
                }
                foreach (var f in fmts.EnumerateArray())
                {
                    var v = Str(f, "vcodec");
                    var a = Str(f, "acodec");
                    var h = (int)Num(f, "height");
                    if (h <= 0 || v == "none") continue;
                    var hasAudio = a != "" && a != "none";
                    var total = FormatSize(f) + (hasAudio ? 0 : audioSize);
                    heights.TryGetValue(h, out var prev);
                    heights[h] = Math.Max(prev, total);
                }
            }
            foreach (var h in heights.Keys.OrderByDescending(x => x))
                info.Resolutions.Add(new ResOption { Height = h, Size = heights[h] });

            return (info, "");
        }
        catch
        {
            return (null, "Couldn't read the video info.");
        }
    }

    /// <summary>Direct URL of a small, widely playable stream used for the in-app preview.</summary>
    public static async Task<string> GetPreviewUrlAsync(string url, string cookies)
    {
        var exe = Tools.Find("yt-dlp");
        if (exe == null) return "";
        var args = new List<string>
        {
            "-g", "--no-playlist", "--no-warnings",
            "-f", "b[ext=mp4][height<=480]/b[height<=480]/w[ext=mp4]/w",
        };
        if (!string.IsNullOrEmpty(cookies)) { args.Add("--cookies-from-browser"); args.Add(cookies); }
        args.Add(url);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var r = await Proc.RunAsync(exe, args, cts.Token);
            if (r.Code != 0) return "";
            foreach (var line in r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (line.StartsWith("http")) return line;
        }
        catch { }
        return "";
    }

    public static List<string> BuildArgs(DownloadRequest r, string tmpDir, string ffDir)
    {
        var a = new List<string>
        {
            "--no-playlist", "--no-warnings", "--newline", "--encoding", "utf-8", "--windows-filenames",
            "--progress-template",
            "download:@@%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.format_id)s",
            "--print-to-file", "after_move:filepath", Path.Combine(tmpDir, "_path.txt"),
            "-o", Path.Combine(tmpDir, "%(title).120B.%(ext)s"),
        };
        if (!string.IsNullOrEmpty(ffDir)) { a.Add("--ffmpeg-location"); a.Add(ffDir); }
        if (!string.IsNullOrEmpty(r.Cookies)) { a.Add("--cookies-from-browser"); a.Add(r.Cookies); }

        if (r.Audio)
        {
            a.AddRange(new[] { "-f", "ba/b", "-x", "--audio-format", r.AudioFormat });
            a.Add("--audio-quality");
            a.Add(r.AudioFormat == "mp3" ? r.Bitrate + "K" : "0");
        }
        else
        {
            var h = r.Height;
            var sel = r.Mute
                ? "bv*[height<=" + h + "]/b[height<=" + h + "]"
                : "bv*[height<=" + h + "]+ba/b[height<=" + h + "]";
            a.AddRange(new[] { "-f", sel, "--merge-output-format", r.VideoFormat });
            a.Add("-S");
            a.Add(r.VideoFormat == "mp4" ? "res,vcodec:h264,acodec:m4a" : "res");
        }

        if (r.Trim)
        {
            var end = r.TrimEnd > 0 ? Fmt.Hms(r.TrimEnd) : "inf";
            a.Add("--download-sections");
            a.Add("*" + Fmt.Hms(r.TrimStart) + "-" + end);
            a.Add("--force-keyframes-at-cuts");
        }

        a.Add(r.Url);
        return a;
    }
}
