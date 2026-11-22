# Third-party software

Grab is released under the MIT License (see `LICENSE`). The portable release also ships these programs,
unmodified, in the `bin` folder. Grab starts them as separate processes; it does not link against them.

| Program | What it does in Grab | License |
| --- | --- | --- |
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) | Reads video information and downloads | The Unlicense |
| [FFmpeg](https://ffmpeg.org/) (`ffmpeg.exe`, `ffprobe.exe`) | Merging, trimming, converting, volume, screenshots | GPL v3 for the bundled build (see below) |
| [.NET](https://github.com/dotnet/runtime) runtime | Runs the app (self-contained build) | MIT |

## FFmpeg

The release workflow downloads the "win64-gpl" build from
[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds). FFmpeg's license depends on how a build was configured;
that build is GPL. The FFmpeg source code and build scripts are available from those two projects.
If you would rather bundle a different FFmpeg build, put its `ffmpeg.exe` and `ffprobe.exe` in `bin` and check that
build's license.

Always check the upstream projects for their current license terms.
