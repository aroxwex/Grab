# Grab

A portable, minimal video and audio downloader for Windows, built with C# and WPF.
Powered by **yt-dlp** and **ffmpeg**.

## What it does
- **Link mode:** paste a link, preview the video inside the app, pick a resolution, format and save.
- **File mode:** open or drop a video from your computer to trim, mute or convert it.
- **Visual trimming:** watch the video, drag the start and end handles on the timeline (or press `[` and `]` at the playhead), then save only that part.
- **Video:** every resolution the video offers with an estimated size, as MP4 / MKV / WebM.
- **Audio:** MP3 (128 / 192 / 320 kbps), M4A, Opus, WAV.
- **Mute:** save the video without sound.
- **Fast cut (File mode):** cut without re-encoding, instant but may start a moment early.
- Progress, speed, time left, cancel, "Show in folder".
- Settings: browser cookies (for bot / age checks) and a one-click yt-dlp update.

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

## Preview notes
The in-app preview uses Windows' built-in media player. For formats it can't play (MKV, WebM, some codecs)
Grab automatically makes a small MP4 copy for the preview; the export always uses the original.

## Note
Only download content you have the right to download. Site terms of service and copyright rules are your responsibility.
