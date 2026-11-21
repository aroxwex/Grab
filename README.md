# Grab

A portable, minimal video and audio downloader for Windows, built with C# and WPF.
Powered by **yt-dlp** and **ffmpeg**.

## What it does
- **One box for everything:** paste a link, type words to search YouTube (press Enter), or open / drop a video file. Grab figures out which one it is.
- **Watch before you save:** a small 360p copy is loaded in the app's own player so you can find the exact part you want. You can turn this off in Settings and load it only when you need it.
- **Visual trimming:** drag the start and end handles on the timeline (or press `[` and `]` at the playhead), then save only that part.
- **Volume:** a slider and a number box from 0% to 200%. 0% removes the sound, above 100% boosts it (with a gentle limiter so it doesn't distort).
- **Video:** every resolution the video offers with an estimated size, as MP4 / MKV / WebM.
- **Audio:** MP3 (128 / 192 / 320 kbps), M4A, Opus, WAV.
- **Full screen:** press `F` or double-click the video. `Esc` leaves it.
- **Files from your computer:** trim or convert them too. "Fast cut" cuts without re-encoding.
- Progress, speed, time left, cancel, "Show in folder".
- Settings: player on/off, temporary file cleanup, cookies (browser or cookies.txt, only for videos that need a login),
  one-click yt-dlp update and the last log. If browser cookies can't be read, Grab retries without them.

Shortcuts: `Space` play/pause, `F` full screen, `←` `→` seek 1 s (hold `Shift` for 5 s), `[` set start, `]` set end.

## Get it (portable)
1. On GitHub open **Actions > Build Grab > Run workflow**, or push a tag like `v1.0.0` to get a release.
2. Download **Grab-portable** (artifact or release zip) and extract it. Inside is one folder:

```
Grab/
├─ Grab.exe
├─ bin/     yt-dlp.exe, ffmpeg.exe, ffprobe.exe
└─ data/    settings and the last log (created on first launch)
```
No installer and no .NET install needed. Keep `data/` when updating.
If downloads stop working because a site changed: Settings > Update yt-dlp.

## Temporary files
The player copy lives in `data/tmp` and is deleted when you load another video, when you close Grab and on the next start.
There is only ever one at a time. You can also clear it by hand in Settings.

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
