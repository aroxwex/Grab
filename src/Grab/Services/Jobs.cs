using System.Globalization;

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

    protected static double ParseNum(string s)
    {
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
    }
}

/// <summary>Downloads from a link with yt-dlp (trim / mute handled here too).</summary>
public class Downloader : JobBase
{
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

    async Task<JobResult> RunOnceAsync(DownloadRequest r)
    {
        var yt = Tools.Find("yt-dlp");
        var ff = Tools.Find("ffmpeg");
        if (yt == null) return new JobResult { Error = "yt-dlp not found. Put yt-dlp.exe in the bin folder." };

        var tmp = Path.Combine(AppPaths.TempDir, DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(r.OutDir);
            Directory.CreateDirectory(tmp);
            var args = YtDlp.BuildArgs(r, tmp, ff != null ? Path.GetDirectoryName(ff) : "");
            var streams = (!r.Audio && !r.Mute) ? 2 : 1;

            Current = Process.Start(Proc.Info(yt, args));
            var p = Current;
            var errTask = p.StandardError.ReadToEndAsync();
            Report(new ProgressInfo { Text = "Starting…" });

            var stage = 0;
            string lastFormat = null;
            string line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                line = line.Trim();
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
                        fraction = Math.Min(((Math.Min(stage, streams) - 1) + Math.Min(done / total, 1.0)) / streams, 0.99);

                    Report(new ProgressInfo
                    {
                        Fraction = fraction,
                        Text = fraction.HasValue ? "Downloading" : "Trimming…",
                        Speed = speed > 0 ? Fmt.Size(speed) + "/s" : "",
                        Eta = eta >= 0 ? Fmt.Time(eta) + " left" : "",
                    });
                }
                else if (line.StartsWith("[Merger]")) Report(new ProgressInfo { Text = "Merging…" });
                else if (line.StartsWith("[ExtractAudio]")) Report(new ProgressInfo { Text = "Converting audio…" });
                else if (line.StartsWith("[VideoRemuxer]") || line.StartsWith("[VideoConvertor]") || line.StartsWith("[Fixup"))
                    Report(new ProgressInfo { Text = "Processing…" });
            }
            await p.WaitForExitAsync();
            var err = await errTask;

            if (CancelRequested) return new JobResult { Cancelled = true };
            if (p.ExitCode != 0)
            {
                var lines = err.Split('\n').Where(l => l.Contains("ERROR")).ToList();
                return new JobResult
                {
                    Error = YtDlp.Friendly(lines.Count > 0 ? lines[lines.Count - 1] : err),
                    CookieProblem = YtDlp.IsCookieProblem(err),
                };
            }

            string src = null;
            var pathFile = Path.Combine(tmp, "_path.txt");
            if (File.Exists(pathFile))
            {
                var last = File.ReadAllLines(pathFile).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (last != null && File.Exists(last.Trim())) src = last.Trim();
            }
            if (src == null)
            {
                src = new DirectoryInfo(tmp).GetFiles()
                    .Where(x => x.Name != "_path.txt")
                    .OrderByDescending(x => x.Length)
                    .Select(x => x.FullName)
                    .FirstOrDefault();
            }
            if (src == null) return new JobResult { Error = "The downloaded file wasn't found." };

            var suffix = "";
            if (!r.Audio && r.Mute)
            {
                Report(new ProgressInfo { Text = "Removing audio…" });
                if (ff == null) return new JobResult { Error = "ffmpeg not found. Put ffmpeg.exe in the bin folder." };
                var muted = Path.Combine(tmp, "muted" + Path.GetExtension(src));
                var res = await Proc.RunAsync(ff, new[] { "-y", "-i", src, "-an", "-c:v", "copy", muted });
                if (res.Code != 0 || !File.Exists(muted)) return new JobResult { Error = "Couldn't remove the audio." };
                File.Delete(src);
                src = muted;
                suffix += " - muted";
            }
            if (r.Trim) suffix = " - trimmed" + suffix;

            // the name yt-dlp chose is lost after the mute step, so take it from the print file when possible
            var baseName = Path.GetFileNameWithoutExtension(src);
            if (File.Exists(pathFile))
            {
                var last = File.ReadAllLines(pathFile).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (last != null) baseName = Path.GetFileNameWithoutExtension(last.Trim());
            }
            var final = UniquePath(Path.Combine(r.OutDir, SafeName(baseName + suffix) + Path.GetExtension(src)));
            File.Move(src, final);
            return new JobResult { Ok = true, File = final };
        }
        catch (Exception ex)
        {
            return new JobResult { Error = YtDlp.Friendly(ex.Message) };
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}

/// <summary>Trims / converts a local file with ffmpeg.</summary>
public class LocalExporter : JobBase
{
    public static List<string> BuildArgs(LocalRequest r, string output)
    {
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
            switch (r.AudioFormat)
            {
                case "mp3": a.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", r.Bitrate + "k" }); break;
                case "m4a": a.AddRange(new[] { "-c:a", "aac", "-b:a", "256k" }); break;
                case "opus": a.AddRange(new[] { "-c:a", "libopus", "-b:a", "160k" }); break;
                default: a.AddRange(new[] { "-c:a", "pcm_s16le" }); break;
            }
        }
        else
        {
            if (r.Mute) a.Add("-an");
            if (r.FastCut)
            {
                a.AddRange(new[] { "-c", "copy" });
            }
            else if (r.VideoFormat == "webm")
            {
                a.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1" });
                if (!r.Mute) a.AddRange(new[] { "-c:a", "libopus", "-b:a", "160k" });
            }
            else
            {
                a.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p" });
                if (!r.Mute) a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
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
            var suffix = r.Trim ? " - trimmed" : " - converted";
            if (!r.Audio && r.Mute) suffix += " - muted";
            var stem = SafeName(Path.GetFileNameWithoutExtension(r.Input));
            var output = UniquePath(Path.Combine(r.OutDir, stem + suffix + "." + ext));

            var length = r.Trim ? ((r.TrimEnd > 0 ? r.TrimEnd : r.Duration) - r.TrimStart) : r.Duration;
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
                double? fraction = length > 0 ? Math.Min(us / 1_000_000.0 / length, 0.99) : (double?)null;
                Report(new ProgressInfo { Fraction = fraction, Text = r.FastCut ? "Cutting…" : "Exporting" });
            }
            await p.WaitForExitAsync();
            var err = await errTask;

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

    /// <summary>Makes a small, widely playable copy for the preview when Windows can't play the original.</summary>
    public static async Task<string> MakeProxyAsync(string input, CancellationToken ct)
    {
        var ff = Tools.Find("ffmpeg");
        if (ff == null) return "";
        try
        {
            Directory.CreateDirectory(AppPaths.TempDir);
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
