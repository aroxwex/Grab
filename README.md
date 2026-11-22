<p align="center">
  <img src="docs/logo.png" width="120" alt="Grab logo">
</p>

<h1 align="center">Grab</h1>

<p align="center">
  Download, trim and convert video and audio. Watch first, pick the exact part, save it.<br>
  A portable Windows app. No installer.
</p>

<p align="center">
  <a href="https://github.com/aroxwex/Grab/releases"><img alt="Release" src="https://img.shields.io/github/v/release/aroxwex/Grab"></a>
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey">
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/aroxwex/Grab"></a>
</p>

<!-- Add a screenshot after your first run:  ![Grab](docs/screenshot.png) -->

## Features

- **One box for everything.** Paste a link, type words to search YouTube, or open / drop a file from your computer.
- **Watch before you save.** Grab loads a small copy into its own player, so you can find the exact moment you want.
- **Visual trimming.** Drag the start and end handles on the timeline, or press `[` and `]` at the playhead.
- **Several parts, one file.** Keep more than one range and Grab joins them in order (cut out the boring middle).
- **Volume control.** A slider and a number from 0% to 200%. 0% removes the sound.
- **Screenshots.** Save the current frame as a PNG.
- **Full screen player.**
- **Video:** every resolution the video offers, with an estimated size. MP4, MKV or WebM.
- **Audio:** MP3 (128 / 192 / 320 kbps), M4A, Opus or WAV.
- **Your own files too.** Trim, join, mute or convert videos that are already on your computer. "Fast cut" cuts without re-encoding.
- **Looks after your disk.** It checks there is room before it starts, keeps at most one temporary player file and cleans it up by itself.
- Progress, speed, time left, cancel, "Show in folder". Dark, minimal, one window.

## Download

1. Open the [Releases](https://github.com/aroxwex/Grab/releases) page and download **Grab-portable.zip**.
2. Extract it anywhere. You get one folder:

   ```
   Grab/
   ├─ Grab.exe
   ├─ bin/      yt-dlp.exe, ffmpeg.exe, ffprobe.exe
   └─ data/     your settings and the last log (created on first launch)
   ```
3. Run `Grab.exe`. There is nothing to install, and you don't need to install .NET.

To update, extract the new version over the old one and keep your `data` folder.

> Windows may say *"Windows protected your PC"* the first time, because the app isn't code-signed.
> Click **More info > Run anyway**. You can always build it yourself from this repository.

## How to use

1. **Load something.** Paste a link into the box (or press **Paste**), type a search and press **Enter**, or press **Open file**.
   You can also drop a link or a file anywhere on the window.
2. **Watch it.** Press play, drag the timeline, or press `Space`. Press `F` for full screen.
3. **Trim (optional).** Switch **Trim** on, drag the white handles around the part you want and press **Download**.
   To keep several parts: pick a range, press **+ Add part**, pick the next one and repeat. They are joined in the order you add them.
4. **Choose the output.** Video or Audio, the resolution and the format on the right.
5. **Volume (optional).** Move the slider or type a number. 0% = no sound, 200% = twice as loud.
6. Press **Download video** (or **Download audio** / **Export**) and then **Show in folder**.

### Keyboard shortcuts

| Key | Action |
| --- | --- |
| `Space` | Play / pause |
| `F` | Full screen (also double-click the video) |
| `S` | Save a screenshot of the current frame |
| `←` `→` | Seek 1 second (hold `Shift` for 5) |
| `[` `]` | Set the trim start / end at the playhead |
| `Esc` | Leave full screen / close settings |

## Settings

- **Player:** load the small player copy automatically (on by default). Turn it off to skip it and load it only when you want.
  You can also clear the temporary file by hand.
- **Cookies:** only needed for videos that require a login (age-restricted, members-only, or a "confirm you're not a bot" message).
  Leave it off otherwise. Firefox or a `cookies.txt` file works best, Chrome / Edge / Brave are often blocked.
  To use a `cookies.txt` file, sign in to the site in your browser, export your cookies to a `.txt` file with a "cookies.txt" browser extension
  and choose the file in Settings. Treat that file like a saved login and keep it private.
- **Download engine:** update yt-dlp with one click. Do this if downloads suddenly stop working.
- **Open last log:** shows what yt-dlp / ffmpeg printed during the last run. Please attach it when you report a problem.

## Troubleshooting

| Problem | What to try |
| --- | --- |
| Downloads stopped working | Settings > **Update yt-dlp**. Sites change often. |
| "Failed to decrypt with DPAPI" | Chrome / Edge / Brave cookies can't be read. Set cookies to **Off**, use **Firefox**, or choose a `cookies.txt` file. Grab also retries without cookies by itself. |
| The player doesn't load | Check your connection and press **Load player**. You can still trim by typing the start and end times. |
| Trimmed downloads take long | Grab asks the site for only the part you picked. Long, high-resolution videos can still take a while. The timer shows it is working. |
| "Not enough free space" | Free some room on the drive named in the message, or choose another save folder. |
| Windows SmartScreen warning | See the note under **Download**. |

## Build from source

Requires the .NET 8 SDK on Windows.

```
dotnet publish src/Grab/Grab.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/Grab
```

Then put `yt-dlp.exe`, `ffmpeg.exe` and `ffprobe.exe` into `dist/Grab/bin`.
For development you can also just run `dotnet run --project src/Grab` with those three files on your `PATH`.

The **Build Grab** workflow in `.github/workflows` does all of this on GitHub: run it manually, or push a tag like `v1.0.0` to publish a release.

```
src/Grab/
├─ Services/     yt-dlp and ffmpeg wrappers, downloads, exports, settings
├─ Controls/     the trim timeline
├─ Themes/       colors and styles
├─ Assets/       logo and icon
└─ MainWindow    the app window
```

## Credits

Grab is a friendly face for two great projects: [yt-dlp](https://github.com/yt-dlp/yt-dlp) and [FFmpeg](https://ffmpeg.org/).
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Disclaimer

Only download content you have the right to download. The terms of service of the sites you use and the copyright rules
that apply to you are your own responsibility. Grab is not affiliated with YouTube or any other site.

## License

[MIT](LICENSE) © 2026 aroxwex
