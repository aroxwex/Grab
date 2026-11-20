# Grab

A portable, minimal video and audio downloader for Windows, built with C# and WPF.
Powered by **yt-dlp** and **ffmpeg**.

## What it does
- **One box for everything:** paste a link, type words to search YouTube (press Enter), or open / drop a video file. Grab figures out which one it is.
- **Watch before you save:** the video plays inside the app (up to 720p) so you can find the exact part you want.
- **Visual trimming:** drag the start and end handles on the timeline (or press `[` and `]` at the playhead), then save only that part.
- **Video:** every resolution the video offers with an estimated size, as MP4 / MKV / WebM.
- **Audio:** MP3 (128 / 192 / 320 kbps), M4A, Opus, WAV.
- **Remove sound:** save the video without audio.
- **Files from your computer:** trim, mute or convert them too. "Fast cut" cuts without re-encoding.
- Progress, speed, time left, cancel, "Show in folder".
- Settings: cookies (browser or cookies.txt, only needed when a site asks for a login) and a one-click yt-dlp update.
  If browser cookies can't be read (Chrome / Edge / Brave often can't), Grab retries without them.

Shortcuts: `Space` play/pause, `←` `→` seek 1 s (hold `Shift` for 5 s), `[` set start, `]` set end.

## Get it (portable)
1. On GitHub open **Actions > Build Grab > Run workflow**.
2. Download the **Grab-portable** artifact, extract it anywhere and run `Grab.exe`.

```
Grab/
├─ Grab.exe
├─ bin/     yt-dlp.exe, ffmpeg.exe, ffprobe.exe
└─ data/    settings (created on first launch)
```
No installer and no .NET install needed. Keep `data/` when updating.
If downloads stop working because a site changed: Settings > Update yt-dlp.

## Build locally
```
dotnet publish src/Grab/Grab.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```
Then put `yt-dlp.exe`, `ffmpeg.exe` and `ffprobe.exe` in `publish/bin/`.

## Player notes
The in-app player uses Windows' built-in media player. For formats it can't play (MKV, WebM, some codecs)
Grab automatically makes a small MP4 copy to play; the export always uses the original quality.

## Note
Only download content you have the right to download. Site terms of service and copyright rules are your responsibility.
