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

    /// <summary>spec is "file:path", "browser:name" or "".</summary>
    public static void AddCookies(List<string> args, string spec)
    {
        if (string.IsNullOrEmpty(spec)) return;
        if (spec.StartsWith("file:")) { args.Add("--cookies"); args.Add(spec.Substring(5)); }
        else if (spec.StartsWith("browser:")) { args.Add("--cookies-from-browser"); args.Add(spec.Substring(8)); }
    }

    /// <summary>YouTube client to use instead of the default one, set when the default got a bot check.</summary>
    public static string ClientOverride = "";

    static readonly string[] ClientFallbacks =
    {
        "youtube:player_client=tv",
        "youtube:player_client=web_safari",
        "youtube:player_client=mweb",
    };

    /// <summary>False when the installed yt-dlp is too old to know --js-runtimes (before 2025.11.12).</summary>
    public static bool JsRuntimeFlagSupported = true;

    /// <summary>Call with the output of "yt-dlp --version" to learn whether --js-runtimes can be used.</summary>
    public static void NoteVersion(string version)
    {
        var parts = (version ?? "").Trim().Split('.');
        if (parts.Length < 3) return;
        if (!int.TryParse(parts[0], out var y) || !int.TryParse(parts[1], out var m) || !int.TryParse(parts[2], out var d)) return;
        JsRuntimeFlagSupported = y * 10000 + m * 100 + d >= 20251112;
    }

    /// <summary>Path of the small JavaScript engine YouTube downloads need (QuickJS, or Deno if you put it there).</summary>
    public static string JsEngine()
    {
        return Tools.Find("qjs") ?? Tools.Find("deno");
    }

    /// <summary>Cookies, the JavaScript engine and the YouTube client choice.</summary>
    public static void AddCommon(List<string> args, string cookieSpec)
    {
        AddCookies(args, cookieSpec);
        var qjs = Tools.Find("qjs");
        if (qjs != null && JsRuntimeFlagSupported) { args.Add("--js-runtimes"); args.Add("quickjs:" + qjs); }
        if (!string.IsNullOrEmpty(ClientOverride)) { args.Add("--extractor-args"); args.Add(ClientOverride); }
    }

    public static bool IsBotCheck(string err)
    {
        var low = (err ?? "").ToLowerInvariant();
        return low.Contains("not a bot") || low.Contains("sign in to confirm");
    }

    static string BotCheckMessage()
    {
        var msg = "YouTube is asking Grab to prove it isn't a bot.";
        if (JsEngine() == null) msg += " qjs.exe is missing from the bin folder, and YouTube needs it.";
        msg += " Try Settings > Update yt-dlp. If it keeps happening, use Firefox cookies or a cookies.txt file from a browser where you're signed in to YouTube.";
        return msg;
    }

    public static bool IsCookieProblem(string err)
    {
        var low = (err ?? "").ToLowerInvariant();
        return low.Contains("dpapi") || low.Contains("could not copy") || low.Contains("cookie database")
            || low.Contains("failed to decrypt") || low.Contains("cookies-from-browser");
    }

    public static string Friendly(string err)
    {
        var e = (err ?? "").Trim();
        var low = e.ToLowerInvariant();
        var table = new (string Key, string Msg)[]
        {
            ("no space left", "Your disk is full. Free up some space and try again."),
            ("not enough space", "Your disk is full. Free up some space and try again."),
            ("errno 28", "Your disk is full. Free up some space and try again."),
            ("dpapi", "Couldn't read cookies from that browser. Chrome, Edge and Brave often block this. Use Firefox or a cookies.txt file in Settings, or turn cookies off."),
            ("failed to decrypt", "Couldn't read cookies from that browser. Use Firefox or a cookies.txt file in Settings, or turn cookies off."),
            ("could not copy", "Couldn't read browser cookies. Close the browser and try again, or turn cookies off in Settings."),
            ("unsupported url", "This link isn't supported."),
            ("video unavailable", "This video is unavailable (it may be deleted or blocked in your region)."),
            ("private video", "This video is private."),
            ("sign in to confirm", BotCheckMessage()),
            ("not a bot", BotCheckMessage()),
            ("sign in", "This video needs a login. Choose a browser or a cookies file in Settings."),
            ("confirm your age", "Age verification is required. Choose a browser or a cookies file in Settings."),
            ("ffmpeg", "ffmpeg not found. Put ffmpeg.exe and ffprobe.exe in the bin folder."),
            ("unable to download webpage", "Couldn't reach the site. Check your internet connection."),
            ("getaddrinfo", "You seem to be offline."),
            ("http error 429", "The site is temporarily rate-limiting you. Wait a bit and try again."),
        };
        foreach (var (key, msg) in table)
            if (low.Contains(key)) return msg;

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

    /// <summary>Direct URL of the video stream (no sound needed), used to grab a single frame.</summary>
    public static async Task<string> GetVideoUrlAsync(string url, int height, string cookieSpec)
    {
        var exe = Tools.Find("yt-dlp");
        if (exe == null) return "";
        foreach (var spec in string.IsNullOrEmpty(cookieSpec) ? new[] { "" } : new[] { cookieSpec, "" })
        {
            var args = new List<string>
            {
                "-g", "--no-playlist", "--no-warnings",
                "-f", "bv*[height<=" + height + "]/b[height<=" + height + "]/b",
            };
            AddCommon(args, spec);
            args.Add(url);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var r = await Proc.RunAsync(exe, args, cts.Token);
                if (r.Code != 0) continue;
                foreach (var line in r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (line.StartsWith("http")) return line;
            }
            catch { }
        }
        return "";
    }

    // ---------------------------------------------------------------- search

    /// <summary>Searches YouTube (top 10) without downloading anything.</summary>
    public static async Task<(List<SearchResult> Items, string Error)> SearchAsync(string query)
    {
        var exe = Tools.Find("yt-dlp");
        if (exe == null) return (null, "yt-dlp not found. Put yt-dlp.exe in the bin folder.");

        var args = new List<string> { "--flat-playlist", "-J", "--no-warnings", "--encoding", "utf-8", "ytsearch10:" + query };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (int Code, string Out, string Err) r;
        try { r = await Proc.RunAsync(exe, args, cts.Token); }
        catch (Exception ex) { return (null, ex.Message); }
        if (cts.IsCancellationRequested) return (null, "The search timed out. Try again.");
        if (r.Code != 0)
        {
            var errLines = r.Err.Split('\n').Where(l => l.Contains("ERROR")).ToList();
            return (null, Friendly(errLines.Count > 0 ? errLines[errLines.Count - 1] : r.Err));
        }

        try
        {
            using var doc = JsonDocument.Parse(r.Out);
            var items = new List<SearchResult>();
            if (doc.RootElement.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var en in entries.EnumerateArray())
                {
                    var id = Str(en, "id");
                    if (id == "") continue;
                    var channel = Str(en, "channel");
                    if (channel == "") channel = Str(en, "uploader");
                    items.Add(new SearchResult
                    {
                        Id = id,
                        Title = Str(en, "title"),
                        Channel = channel,
                        Duration = Num(en, "duration"),
                        Url = "https://www.youtube.com/watch?v=" + id,
                        Thumb = "https://i.ytimg.com/vi/" + id + "/mqdefault.jpg",
                    });
                }
            }
            return (items, "");
        }
        catch
        {
            return (null, "Couldn't read the search results.");
        }
    }

    // ---------------------------------------------------------------- info

    /// <summary>
    /// Fetches video info. If the cookies can't be read it retries without them, and if YouTube asks for a
    /// bot check it tries other YouTube clients before giving up.
    /// </summary>
    public static async Task<(VideoInfo Info, string Error)> GetInfoAsync(string url, string cookieSpec)
    {
        ClientOverride = "";
        var spec = cookieSpec;
        var cookiesSkipped = false;

        var r = await GetInfoCoreAsync(url, spec);
        if (r.Info == null && !string.IsNullOrEmpty(spec) && IsCookieProblem(r.Raw))
        {
            spec = "";
            cookiesSkipped = true;
            r = await GetInfoCoreAsync(url, spec);
        }

        if (r.Info == null && IsBotCheck(r.Raw))
        {
            foreach (var client in ClientFallbacks)
            {
                ClientOverride = client;
                var t = await GetInfoCoreAsync(url, spec);
                if (t.Info != null)
                {
                    t.Info.CompatMode = true;
                    r = t;
                    break;
                }
                if (!IsBotCheck(t.Raw)) r = t;      // a different problem: report that one
            }
            if (r.Info == null) ClientOverride = "";
        }

        if (r.Info != null) r.Info.CookiesSkipped = cookiesSkipped;
        return (r.Info, r.Error);
    }

    static async Task<(VideoInfo Info, string Error, string Raw)> GetInfoCoreAsync(string url, string cookieSpec)
    {
        var exe = Tools.Find("yt-dlp");
        if (exe == null) return (null, "yt-dlp not found. Put yt-dlp.exe in the bin folder.", "");

        var args = new List<string> { "-J", "--no-playlist", "--no-warnings", "--encoding", "utf-8" };
        AddCommon(args, cookieSpec);
        args.Add(url);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        (int Code, string Out, string Err) r;
        try { r = await Proc.RunAsync(exe, args, cts.Token); }
        catch (Exception ex) { return (null, ex.Message, ex.Message); }
        if (cts.IsCancellationRequested) return (null, "Timed out. Try again.", "");

        if (r.Code != 0)
        {
            var errLines = r.Err.Split('\n').Where(l => l.Contains("ERROR")).ToList();
            return (null, Friendly(errLines.Count > 0 ? errLines[errLines.Count - 1] : r.Err), r.Err);
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

            return (info, "", "");
        }
        catch
        {
            return (null, "Couldn't read the video info.", "");
        }
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
        AddCommon(a, r.Cookies);

        if (r.Audio)
        {
            a.AddRange(new[] { "-f", "ba/b", "-x", "--audio-format", r.AudioFormat });
            a.Add("--audio-quality");
            a.Add(r.AudioFormat == "mp3" ? r.Bitrate + "K" : "0");
        }
        else
        {
            var h = r.Height;
            var sel = r.Silent
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
