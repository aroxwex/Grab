using System.Collections.Generic;

namespace Grab;

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
    public bool Mute;
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
    public bool Mute;
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
}
