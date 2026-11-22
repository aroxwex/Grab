using System.Globalization;
using System.Text.RegularExpressions;

namespace Grab.Services;

public abstract class JobBase
{
    protected Process Current;
    protected volatile bool CancelRequested;

    public event Action<ProgressInfo> Progress;

    protected void Report(ProgressInfo p)
    {
        Progress?.Invoke(p);
    }

    public void Cancel()
    {
        CancelRequested = true;
        Proc.Kill(Current);
    }

    protected static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, name + " (" + i + ")" + ext);
            if (!File.Exists(candidate)) return candidate;
        }
    }

    protected static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    public static double ParseNum(string s)
    {
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
    }

    protected static string Quote(string a)
    {
        return a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a;
    }
}

/// <summary>Volume helpers shared by downloads and local exports.</summary>
public static class AudioTools
{
    public static string G(double v)
    {
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public static string VolumeFilterText(double gain)
    {
        return gain > 1.0 ? "volume=" + G(gain) + ",alimiter=limit=0.95" : "volume=" + G(gain);
    }

    /// <summary>ffmpeg volume filter. Boosting adds a gentle limiter so it doesn't clip harshly.</summary>
    public static string[] VolumeFilter(double gain)
    {
        return new[] { "-af", VolumeFilterText(gain) };
    }

    public static string[] CodecArgs(string ext, int bitrate)
    {
        switch ((ext ?? "").TrimStart('.').ToLowerInvariant())
        {
            case "mp3": return new[] { "-c:a", "libmp3lame", "-b:a", bitrate + "k" };
            case "m4a": return new[] { "-c:a", "aac", "-b:a", "256k" };
            case "webm":
            case "opus": return new[] { "-c:a", "libopus", "-b:a", "160k" };
            case "wav": return new[] { "-c:a", "pcm_s16le" };
            default: return new[] { "-c:a", "aac", "-b:a", "192k" };
        }
    }

    /// <summary>Re-writes a finished file with a different volume (video stream is copied untouched).</summary>
    public static List<string> GainArgs(string input, string output, double gain, bool audioOnly, string ext, int bitrate)
    {
        var a = new List<string> { "-y", "-hide_banner", "-i", input };
        if (audioOnly)
        {
            a.Add("-vn");
        }
        else if (gain <= 0.001)
        {
            a.AddRange(new[] { "-an", "-c:v", "copy" });
            a.Add(output);
            return a;
        }
        else
        {
            a.AddRange(new[] { "-c:v", "copy" });
        }
        a.AddRange(VolumeFilter(gain));
        a.AddRange(CodecArgs(ext, bitrate));
        a.Add(output);
        return a;
    }

    public static string Suffix(bool silent, double gain)
    {
        return silent ? " - muted" : " - vol " + (int)Math.Round(gain * 100) + "%";
    }
}

/// <summary>Downloads from a link with yt-dlp (trim, several parts and volume handled here too).</summary>
public class Downloader : JobBase
{
    static readonly Regex TimeRe = new Regex(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    public async Task<JobResult> RunAsync(DownloadRequest r)
    {
        var res = await RunOnceAsync(r);
        if (!res.Ok && !res.Cancelled && !string.IsNullOrEmpty(r.Cookies) && res.CookieProblem)
        {
            // browser cookies can't always be read (Chrome / Edge / Brave): try again without them
            r.Cookies = "";
            Report(new ProgressInfo { Text = "Retrying without cookies…" });
            res = await RunOnceAsync(r);
            if (res.Ok) res.Note = "Your browser cookies couldn't be read, so Grab saved it without them.";
        }
        return res;
    }

    static DownloadRequest PartOf(DownloadRequest r, Segment seg)
    {
        return new DownloadRequest
        {
            Url = r.Url,
            Audio = r.Audio,
            Height = r.Height,
            VideoFormat = r.VideoFormat,
            AudioFormat = r.AudioFormat,
            Bitrate = r.Bitrate,
            Trim = true,
            TrimStart = seg.Start,
            TrimEnd = seg.End,
            Gain = r.Gain,            // only decides audio-less formats here, volume is applied once at the end
            OutDir = r.OutDir,
            Cookies = r.Cookies,
            Duration = r.Duration,
        };
    }

    async Task<JobResult> RunOnceAsync(DownloadRequest r)
    {
        var yt = Tools.Find("yt-dlp");
        var ff = Tools.Find("ffmpeg");
        if (yt == null) return new JobResult { Error = "yt-dlp not found. Put yt-dlp.exe in the bin folder." };

        var log = new StringBuilder();
        var tmp = Path.Combine(AppPaths.TempDir, DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(r.OutDir);
            Directory.CreateDirectory(tmp);

            var multi = r.Segments.Count >= 2;
            string src;
            string printedName;

            if (!multi)
            {
                var one = await FetchOneAsync(r, tmp, 0, 1, "", log, yt, ff);
                if (one.Failure != null) return one.Failure;
                src = one.File;
                printedName = one.Name;
            }
            else
            {
                var files = new List<string>();
                printedName = null;
                for (var i = 0; i < r.Segments.Count; i++)
                {
                    if (CancelRequested) return new JobResult { Cancelled = true };
                    var partDir = Path.Combine(tmp, "part" + i);
                    Directory.CreateDirectory(partDir);
                    var one = await FetchOneAsync(PartOf(r, r.Segments[i]), partDir, i, r.Segments.Count,
                        "Part " + (i + 1) + " of " + r.Segments.Count + ": ", log, yt, ff);
                    if (one.Failure != null) return one.Failure;
                    files.Add(one.File);
                    if (printedName == null) printedName = one.Name;
                }

                Report(new ProgressInfo { Text = "Joining the parts…" });
                if (ff == null) return new JobResult { Error = "ffmpeg not found. Put ffmpeg.exe in the bin folder." };
                var listPath = Path.Combine(tmp, "parts.txt");
                File.WriteAllLines(listPath, files.Select(f => "file '" + f.Replace('\\', '/').Replace("'", "'\\''") + "'"));
                var joined = Path.Combine(tmp, "joined" + Path.GetExtension(files[0]));
                var res = await Proc.RunAsync(ff, new[] { "-y", "-hide_banner", "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", joined });
                log.AppendLine(res.Err);
                if (res.Code != 0 || !File.Exists(joined)) return new JobResult { Error = "Couldn't join the parts." };
                src = joined;
            }

            var suffix = "";
            if (r.Silent || r.NeedsGain)
            {
                Report(new ProgressInfo { Text = r.Silent ? "Removing sound…" : "Adjusting volume…" });
                if (ff == null) return new JobResult { Error = "ffmpeg not found. Put ffmpeg.exe in the bin folder." };
                var ext = Path.GetExtension(src);
                var adjusted = Path.Combine(tmp, "adjusted" + ext);
                var res = await Proc.RunAsync(ff, AudioTools.GainArgs(src, adjusted, r.Silent ? 0 : r.Gain, r.Audio, ext, r.Bitrate));
                if (res.Code != 0 || !File.Exists(adjusted))
                {
                    log.AppendLine(res.Err);
                    return new JobResult { Error = "Couldn't change the volume." };
                }
                File.Delete(src);
                src = adjusted;
                suffix += AudioTools.Suffix(r.Silent, r.Gain);
            }
            if (multi) suffix = " - " + r.Segments.Count + " parts" + suffix;
            else if (r.Trim) suffix = " - trimmed" + suffix;

            var baseName = printedName ?? Path.GetFileNameWithoutExtension(src);
            var final = UniquePath(Path.Combine(r.OutDir, SafeName(baseName + suffix) + Path.GetExtension(src)));
            try
            {
                File.Move(src, final);
            }
            catch (IOException ex)
            {
                log.AppendLine(ex.ToString());
                return new JobResult { Error = "Couldn't save the file to " + r.OutDir + ". " + YtDlp.Friendly(ex.Message) };
            }
            return new JobResult { Ok = true, File = final };
        }
        catch (Exception ex)
        {
            log.AppendLine(ex.ToString());
            return new JobResult { Error = YtDlp.Friendly(ex.Message) };
        }
        finally
        {
            AppLog.Write("last-download.log", log.ToString());
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>Downloads one file (the whole video or one section). Failure is null when it worked.</summary>
    async Task<(JobResult Failure, string File, string Name)> FetchOneAsync(
        DownloadRequest r, string dir, int index, int count, string label, StringBuilder log, string yt, string ff)
    {
        var args = YtDlp.BuildArgs(r, dir, ff != null ? Path.GetDirectoryName(ff) : "");
        var streams = (!r.Audio && !r.Silent) ? 2 : 1;
        log.AppendLine(Quote(yt) + " " + string.Join(" ", args.Select(Quote)));
        log.AppendLine();

        double? Scale(double? f)
        {
            return f.HasValue ? Math.Min((index + f.Value) / count, 0.99) : (double?)null;
        }

        Current = Process.Start(Proc.Info(yt, args));
        var p = Current;
        var errTask = p.StandardError.ReadToEndAsync();

        var sectionLength = r.Trim ? ((r.TrimEnd > 0 ? r.TrimEnd : r.Duration) - r.TrimStart) : 0;
        Report(new ProgressInfo { Text = label + (r.Trim ? "Downloading the selected part…" : "Starting…") });

        var stage = 0;
        var destinations = 0;
        string lastFormat = null;
        string line;
        while ((line = await p.StandardOutput.ReadLineAsync()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;
            if (log.Length < 200000) log.AppendLine(line);

            if (line.StartsWith("@@"))
            {
                var f = line.Substring(2).Split('|');
                if (f.Length < 6) continue;
                if (f[5] != lastFormat) { stage++; lastFormat = f[5]; }
                var done = ParseNum(f[0]);
                var total = ParseNum(f[1]);
                if (total <= 0) total = ParseNum(f[2]);
                var speed = ParseNum(f[3]);
                var eta = ParseNum(f[4]);

                double? fraction = null;
                if (total > 0 && done >= 0)
                    fraction = ((Math.Min(stage, streams) - 1) + Math.Min(done / total, 1.0)) / streams;

                Report(new ProgressInfo
                {
                    Fraction = Scale(fraction),
                    Text = label + (fraction.HasValue ? "Downloading" : (r.Trim ? "Downloading the selected part…" : "Downloading…")),
                    Speed = speed > 0 ? Fmt.Size(speed) + "/s" : "",
                    Eta = eta >= 0 ? Fmt.Time(eta) + " left" : "",
                });
            }
            else if (line.StartsWith("[download] Destination:"))
            {
                destinations++;
            }
            else if (r.Trim && sectionLength > 0 && TimeRe.IsMatch(line))
            {
                // ffmpeg prints its position while it cuts the section
                var m = TimeRe.Match(line);
                var t = ParseNum(m.Groups[1].Value) * 3600 + ParseNum(m.Groups[2].Value) * 60 + ParseNum(m.Groups[3].Value);
                var part = Math.Min(Math.Max(destinations, 1), streams);
                var frac = ((part - 1) + Math.Min(t / sectionLength, 1.0)) / streams;
                Report(new ProgressInfo { Fraction = Scale(frac), Text = label + "Downloading the selected part" });
            }
            else if (line.StartsWith("[Merger]")) Report(new ProgressInfo { Text = label + "Merging…" });
            else if (line.StartsWith("[ExtractAudio]")) Report(new ProgressInfo { Text = label + "Converting audio…" });
            else if (line.StartsWith("[VideoRemuxer]") || line.StartsWith("[VideoConvertor]") || line.StartsWith("[Fixup"))
                Report(new ProgressInfo { Text = label + "Processing…" });
        }
        await p.WaitForExitAsync();
        var err = await errTask;
        log.AppendLine("--- stderr ---");
        log.AppendLine(err);

        if (CancelRequested) return (new JobResult { Cancelled = true }, null, null);
        if (p.ExitCode != 0)
        {
            var lines = err.Split('\n').Where(l => l.Contains("ERROR")).ToList();
            return (new JobResult
            {
                Error = YtDlp.Friendly(lines.Count > 0 ? lines[lines.Count - 1] : err),
                CookieProblem = YtDlp.IsCookieProblem(err),
            }, null, null);
        }

        string src = null;
        string printedName = null;
        var pathFile = Path.Combine(dir, "_path.txt");
        if (File.Exists(pathFile))
        {
            var last = File.ReadAllLines(pathFile).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (last != null)
            {
                printedName = Path.GetFileNameWithoutExtension(last.Trim());
                if (File.Exists(last.Trim())) src = last.Trim();
            }
        }
        if (src == null)
        {
            src = new DirectoryInfo(dir).GetFiles()
                .Where(x => x.Name != "_path.txt")
                .OrderByDescending(x => x.Length)
                .Select(x => x.FullName)
                .FirstOrDefault();
        }
        if (src == null) return (new JobResult { Error = "The downloaded file wasn't found." }, null, null);
        return (null, src, printedName);
    }
}

/// <summary>Trims / converts a local file with ffmpeg.</summary>
public class LocalExporter : JobBase
{
    /// <summary>Keeps several parts of one file and joins them (always re-encodes).</summary>
    static List<string> BuildMultiArgs(LocalRequest r, string output)
    {
        var a = new List<string> { "-y", "-hide_banner", "-nostats", "-progress", "pipe:1", "-i", r.Input };
        var video = !r.Audio;
        var audio = r.Audio || !r.Silent;
        var n = r.Segments.Count;

        var f = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            var s = AudioTools.G(r.Segments[i].Start);
            var e = AudioTools.G(r.Segments[i].End);
            if (video) f.Append("[0:v:0]trim=start=" + s + ":end=" + e + ",setpts=PTS-STARTPTS[v" + i + "];");
            if (audio) f.Append("[0:a:0]atrim=start=" + s + ":end=" + e + ",asetpts=PTS-STARTPTS[a" + i + "];");
        }
        for (var i = 0; i < n; i++)
        {
            if (video) f.Append("[v" + i + "]");
            if (audio) f.Append("[a" + i + "]");
        }
        f.Append("concat=n=" + n + ":v=" + (video ? 1 : 0) + ":a=" + (audio ? 1 : 0));
        if (video) f.Append("[v]");
        if (audio) f.Append(r.NeedsGain ? "[ac]" : "[a]");
        if (audio && r.NeedsGain) f.Append(";[ac]" + AudioTools.VolumeFilterText(r.Gain) + "[a]");

        a.AddRange(new[] { "-filter_complex", f.ToString() });
        if (video) a.AddRange(new[] { "-map", "[v]" });
        if (audio) a.AddRange(new[] { "-map", "[a]" });

        if (video)
        {
            if (r.VideoFormat == "webm") a.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1" });
            else a.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p" });
        }
        if (audio) a.AddRange(AudioTools.CodecArgs(r.Audio ? r.AudioFormat : r.VideoFormat, r.Bitrate));
        if (video && r.VideoFormat == "mp4") a.AddRange(new[] { "-movflags", "+faststart" });
        a.Add(output);
        return a;
    }

    public static List<string> BuildArgs(LocalRequest r, string output)
    {
        if (r.Segments.Count >= 2) return BuildMultiArgs(r, output);

        var a = new List<string> { "-y", "-hide_banner", "-nostats", "-progress", "pipe:1" };
        if (r.Trim)
        {
            a.Add("-ss"); a.Add(Fmt.Hms(r.TrimStart));
            var end = r.TrimEnd > 0 ? r.TrimEnd : r.Duration;
            if (end > r.TrimStart) { a.Add("-t"); a.Add(Fmt.Hms(end - r.TrimStart)); }
        }
        a.Add("-i"); a.Add(r.Input);

        if (r.Audio)
        {
            a.Add("-vn");
            if (r.NeedsGain) a.AddRange(AudioTools.VolumeFilter(r.Gain));
            a.AddRange(AudioTools.CodecArgs(r.AudioFormat, r.Bitrate));
        }
        else
        {
            a.AddRange(new[] { "-map", "0:v:0?" });
            if (!r.Silent) a.AddRange(new[] { "-map", "0:a:0?" });

            if (r.FastCut)
            {
                a.AddRange(new[] { "-c:v", "copy" });
                if (!r.Silent)
                {
                    if (r.NeedsGain)
                    {
                        a.AddRange(AudioTools.VolumeFilter(r.Gain));
                        a.AddRange(AudioTools.CodecArgs(r.VideoFormat, r.Bitrate));
                    }
                    else
                    {
                        a.AddRange(new[] { "-c:a", "copy" });
                    }
                }
            }
            else if (r.VideoFormat == "webm")
            {
                a.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1" });
                if (!r.Silent)
                {
                    if (r.NeedsGain) a.AddRange(AudioTools.VolumeFilter(r.Gain));
                    a.AddRange(new[] { "-c:a", "libopus", "-b:a", "160k" });
                }
            }
            else
            {
                a.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p" });
                if (!r.Silent)
                {
                    if (r.NeedsGain) a.AddRange(AudioTools.VolumeFilter(r.Gain));
                    a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
                }
            }
            if (r.VideoFormat == "mp4") a.AddRange(new[] { "-movflags", "+faststart" });
        }
        a.Add(output);
        return a;
    }

    public async Task<JobResult> RunAsync(LocalRequest r)
    {
        var ff = Tools.Find("ffmpeg");
        if (ff == null) return new JobResult { Error = "ffmpeg not found. Put ffmpeg.exe and ffprobe.exe in the bin folder." };

        try
        {
            Directory.CreateDirectory(r.OutDir);
            var ext = r.Audio ? r.AudioFormat : r.VideoFormat;
            var multi = r.Segments.Count >= 2;
            var suffix = multi ? " - " + r.Segments.Count + " parts"
                : r.Trim ? " - trimmed" : (r.Silent || r.NeedsGain ? "" : " - converted");
            if (r.Silent || r.NeedsGain) suffix += AudioTools.Suffix(r.Silent, r.Gain);
            var stem = SafeName(Path.GetFileNameWithoutExtension(r.Input));
            var output = UniquePath(Path.Combine(r.OutDir, stem + suffix + "." + ext));

            var length = multi ? r.Segments.Sum(x => x.Length)
                : r.Trim ? ((r.TrimEnd > 0 ? r.TrimEnd : r.Duration) - r.TrimStart) : r.Duration;
            Current = Process.Start(Proc.Info(ff, BuildArgs(r, output)));
            var p = Current;
            var errTask = p.StandardError.ReadToEndAsync();
            Report(new ProgressInfo { Text = "Starting…" });

            string line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                if (!line.StartsWith("out_time_us=")) continue;
                var us = ParseNum(line.Substring("out_time_us=".Length));
                if (us < 0) continue;
                double? fraction = length > 0 ? Math.Min(us / 1000000.0 / length, 0.99) : (double?)null;
                Report(new ProgressInfo { Fraction = fraction, Text = r.FastCut && !multi ? "Cutting…" : "Exporting" });
            }
            await p.WaitForExitAsync();
            var err = await errTask;
            AppLog.Write("last-export.log", string.Join(" ", BuildArgs(r, output).Select(Quote)) + "\n\n" + err);

            if (CancelRequested)
            {
                try { if (File.Exists(output)) File.Delete(output); } catch { }
                return new JobResult { Cancelled = true };
            }
            if (p.ExitCode != 0 || !File.Exists(output))
            {
                var last = err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                return new JobResult { Error = string.IsNullOrEmpty(last) ? "ffmpeg failed." : last };
            }
            return new JobResult { Ok = true, File = output };
        }
        catch (Exception ex)
        {
            return new JobResult { Error = ex.Message };
        }
    }

    /// <summary>Makes a small, widely playable copy for the player when Windows can't play the original.</summary>
    public static async Task<string> MakeProxyAsync(string input, CancellationToken ct)
    {
        var ff = Tools.Find("ffmpeg");
        if (ff == null) return "";
        try
        {
            Directory.CreateDirectory(AppPaths.TempDir);
            var free = DiskSpace.FreeBytes(AppPaths.TempDir);
            if (free >= 0 && free < 1000000000) return "";          // not enough room for a player copy
            var output = Path.Combine(AppPaths.TempDir, "preview-" + DateTime.Now.Ticks + ".mp4");
            var args = new List<string>
            {
                "-y", "-hide_banner", "-i", input,
                "-vf", "scale=-2:360", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart", output,
            };
            var r = await Proc.RunAsync(ff, args, ct);
            return r.Code == 0 && File.Exists(output) ? output : "";
        }
        catch { return ""; }
    }
}

/// <summary>Downloads a small (about 360p) copy of a video so the in-app player can play and scrub it reliably.</summary>
public static class PreviewMaker
{
    public static async Task<string> MakeAsync(string url, string cookieSpec, Action<double?> report, CancellationToken ct)
    {
        var yt = Tools.Find("yt-dlp");
        if (yt == null) return "";
        var ff = Tools.Find("ffmpeg");

        foreach (var spec in string.IsNullOrEmpty(cookieSpec) ? new[] { "" } : new[] { cookieSpec, "" })
        {
            var dir = Path.Combine(AppPaths.TempDir, "preview-" + DateTime.Now.Ticks);
            try
            {
                Directory.CreateDirectory(dir);
                var args = new List<string>
                {
                    "--no-playlist", "--no-warnings", "--newline", "--encoding", "utf-8",
                    "--progress-template",
                    "download:@@%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(info.format_id)s",
                    "--print-to-file", "after_move:filepath", Path.Combine(dir, "_path.txt"),
                    "-o", Path.Combine(dir, "preview.%(ext)s"),
                    "-f", "bv*[height<=360][vcodec^=avc1]+ba[ext=m4a]/bv*[height<=480][vcodec^=avc1]+ba[ext=m4a]/b[height<=480]/bv*[height<=480]+ba/w",
                    "--merge-output-format", "mp4",
                };
                if (ff != null) { args.Add("--ffmpeg-location"); args.Add(Path.GetDirectoryName(ff)); }
                YtDlp.AddCommon(args, spec);
                args.Add(url);

                using var p = Process.Start(Proc.Info(yt, args));
                using var reg = ct.Register(() => Proc.Kill(p));
                var errTask = p.StandardError.ReadToEndAsync();

                var stage = 0;
                string lastFormat = null;
                string line;
                while ((line = await p.StandardOutput.ReadLineAsync()) != null)
                {
                    if (!line.StartsWith("@@")) continue;
                    var f = line.Substring(2).Trim().Split('|');
                    if (f.Length < 4) continue;
                    if (f[3] != lastFormat) { stage++; lastFormat = f[3]; }
                    var done = JobBase.ParseNum(f[0]);
                    var total = JobBase.ParseNum(f[1]);
                    if (total <= 0) total = JobBase.ParseNum(f[2]);
                    if (total > 0 && done >= 0)
                        report?.Invoke(Math.Min(((Math.Min(stage, 2) - 1) + Math.Min(done / total, 1.0)) / 2.0, 0.99));
                }
                await p.WaitForExitAsync();
                await errTask;

                if (ct.IsCancellationRequested) return "";
                if (p.ExitCode == 0)
                {
                    var pathFile = Path.Combine(dir, "_path.txt");
                    if (File.Exists(pathFile))
                    {
                        var last = File.ReadAllLines(pathFile).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
                        if (last != null && File.Exists(last.Trim())) return last.Trim();
                    }
                    var any = new DirectoryInfo(dir).GetFiles("preview.*").OrderByDescending(x => x.Length).FirstOrDefault();
                    if (any != null) return any.FullName;
                }
            }
            catch
            {
                // try the next option
            }
            try { Directory.Delete(dir, true); } catch { }
        }
        return "";
    }
}

/// <summary>Saves one frame of a video as a PNG.</summary>
public static class Screenshots
{
    public static async Task<(string File, string Error)> CaptureAsync(string input, double seconds, string outDir, string title)
    {
        var ff = Tools.Find("ffmpeg");
        if (ff == null) return ("", "ffmpeg not found. Put ffmpeg.exe in the bin folder.");
        try
        {
            Directory.CreateDirectory(outDir);
            var stem = (title ?? "").Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
            if (stem.Length == 0) stem = "Screenshot";
            if (stem.Length > 100) stem = stem.Substring(0, 100);
            var stamp = Fmt.Time(seconds).Replace(':', '.');
            var path = Path.Combine(outDir, stem + " - " + stamp + ".png");
            for (var i = 1; File.Exists(path); i++) path = Path.Combine(outDir, stem + " - " + stamp + " (" + i + ").png");

            var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-ss", Fmt.Hms(seconds), "-i", input, "-frames:v", "1", path };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var r = await Proc.RunAsync(ff, args, cts.Token);
            if (r.Code != 0 || !File.Exists(path))
            {
                var last = r.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                return ("", string.IsNullOrEmpty(last) ? "Couldn't save the screenshot." : YtDlp.Friendly(last));
            }
            return (path, "");
        }
        catch (Exception ex)
        {
            return ("", YtDlp.Friendly(ex.Message));
        }
    }
}
