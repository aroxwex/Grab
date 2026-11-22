using System.Collections.Generic;

namespace Grab;

public static class AppInfo
{
    public const string Name = "Grab";
    public const string RepoUrl = "https://github.com/aroxwex/Grab";
    public const string IssuesUrl = "https://github.com/aroxwex/Grab/issues";
}

/// <summary>One kept part of a video (seconds).</summary>
public class Segment
{
    public double Start;
    public double End;
    public double Speed = 1.0;                 // 0.25 - 4, 1 = normal
    public double Length => End - Start;
    public double OutputLength => Length / (Speed > 0 ? Speed : 1.0);
}

public class ResOption
{
    public int Height;
    public double Size;

    public string Label
    {
        get
        {
            if (Height >= 4320) return "8K";
            if (Height >= 2160) return "4K";
            if (Height >= 1440) return "2K";
            return Height + "p";
        }
    }
}

public class VideoInfo
{
    public string Title = "";
    public string Channel = "";
    public double Duration;
    public string Thumb = "";
    public List<ResOption> Resolutions = new List<ResOption>();
    public bool CookiesSkipped;
    public bool CompatMode;       // fetched with a different YouTube client because the normal one was blocked   // cookies couldn't be read, info was fetched without them
}

public class SearchResult
{
    public string Id = "";
    public string Title = "";
    public string Channel = "";
    public double Duration;
    public string Url = "";
    public string Thumb = "";
}

public class DownloadRequest
{
    public string Url = "";
    public bool Audio;
    public int Height = 99999;
    public string VideoFormat = "mp4";
    public string AudioFormat = "mp3";
    public int Bitrate = 320;
    public bool Trim;
    public double TrimStart;
    public double TrimEnd;       // 0 = until the end
    public List<Segment> Segments = new List<Segment>();   // 2 or more parts that get joined (overrides Trim)
    public double Gain = 1.0;                // 1.0 = original volume, 0 = silent, 2.0 = double
    public bool Silent => !Audio && Gain <= 0.001;
    public bool NeedsGain => !Silent && Math.Abs(Gain - 1.0) > 0.01;
    public string OutDir = "";
    public string Cookies = "";
    public double Duration;
}

public class LocalRequest
{
    public string Input = "";
    public bool Audio;
    public string VideoFormat = "mp4";
    public string AudioFormat = "mp3";
    public int Bitrate = 320;
    public bool Trim;
    public double TrimStart;
    public double TrimEnd;       // 0 = until the end
    public List<Segment> Segments = new List<Segment>();   // 2 or more parts that get joined (overrides Trim)
    public double Gain = 1.0;                // 1.0 = original volume, 0 = silent, 2.0 = double
    public bool Silent => !Audio && Gain <= 0.001;
    public bool NeedsGain => !Silent && Math.Abs(Gain - 1.0) > 0.01;
    public bool FastCut;
    public string OutDir = "";
    public double Duration;
}

public class ProgressInfo
{
    public double? Fraction;     // null = unknown
    public string Text = "";
    public string Speed = "";
    public string Eta = "";
}

public class JobResult
{
    public bool Ok;
    public bool Cancelled;
    public string File = "";
    public string Error = "";
    public string Note = "";
    public bool CookieProblem;
}
