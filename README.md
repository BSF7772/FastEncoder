# FastEncoder

Native Windows 11 app (C# · WinUI 3 · Mica) that shrinks videos with ffmpeg while keeping the quality.

## Run
Double-click `FastEncoder.bat`. The first run builds the app (about 30 s); after that it starts instantly.
Use `FastEncoder.bat --rebuild` after editing the source. You can also drop video files onto the .bat or the .exe.

Requires the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`) and ffmpeg (`winget install Gyan.FFmpeg`).

## Features
- Queue: drag and drop files or folders, per-file trim, thumbnails, live progress/ETA, taskbar progress
- Preview: hardware playback, filmstrip timeline with draggable trim handles, loop inside the trim.
  Files Windows can't decode get an automatic low-res preview copy.
- Storage presets: Balanced / Archive / Pristine (SVT-AV1 10-bit + Opus), GPU Turbo (NVENC/QSV/AMF),
  Compact 720p, Universal (H.264), Fit to size (exact MB, re-encodes if it overshoots), Trim only (lossless)
- Size estimate: encodes three 4-second samples and extrapolates. "View sample" plays the compressed clip.
- Edits: speed 0.25–4×, volume 0–300 %, remove audio
- Audio sync (per video): slider ±1.5 s with live preview (the sound plays shifted while you drag), ‹ › 10 ms nudges,
  **Detect** (matches sounds with flashes/impacts; re-checks itself with deliberately shifted audio and only applies
  confident answers), Detect for every video, copy one offset to the whole queue. Works in Trim only too (lossless).
- Fix timing drift (on by default, in the Detect menu): constant frame rate output + audio placed by its timestamps.
  Game captures (Steam, ShadowPlay, OBS) stamp frames unevenly — up to 0.4 s off a steady 60 fps in long clips —
  which editors and some players show as desync.
- Results that end up bigger than the original are discarded (optional)

Keys: `Space` play/pause · `I` / `O` set start/end · `R` reset trim · `← →` ±5 s · `, .` frame step · `Ctrl+O` add

## Layout
- `src/Core/` has no UI code: ffmpeg probing (`Ff.cs`), encoder catalog + GPU detection (`Encoders.cs`), A/V offset detection (`AudioSync.cs`),
  presets (`Presets.cs`), command building/encode/estimate (`Pipeline.cs`), settings, taskbar
- `src/MainWindow.xaml(.cs)`: UI · `src/JobItem.cs`: queue item model
- Settings: `%LOCALAPPDATA%\FastEncoder\settings.json` · temp files: `%TEMP%\FastEncoder` (cleared on exit)

## Self-test
`FastEncoder.exe --selftest report.txt` runs every preset plus the CPU two-pass/scale paths on a generated clip
and writes PASS/FAIL lines to the report. Exit code 0 means everything passed. It also checks audio sync: a generated
clip with sound 300 ms late must be detected and come out in sync, and a jittery capture must come out at an even rate.

`FastEncoder.exe --syncdetect video.mp4 report.txt` writes the measured offset of one file (for diagnosing desync).

## License
[MIT](LICENSE). FastEncoder runs ffmpeg as a separate program and doesn't bundle it, so ffmpeg's licence applies to your ffmpeg
build, not to this code.
