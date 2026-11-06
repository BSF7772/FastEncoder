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
- Storage presets: Balanced / Archive / Pristine (SVT-AV1 10-bit + Opus), GPU Turbo / GPU Balanced / GPU Fast (NVENC/QSV/AMF),
  Compact 720p, Universal (H.264), Fit to size (exact MB, re-encodes if it overshoots), Trim only (lossless)
  (the preset selected on first start is **Balanced**, the CPU SVT-AV1 one)
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

## GPU presets
All three use the best GPU encoder that passed its test encode (AV1 before HEVC before H.264; NVENC, then QuickSync, then AMF)
with the same quality target; they differ in how much work the encoder spends per frame. With no GPU encoder they fall back to
SVT-AV1 presets 10 / 8 / 12.

| Preset | NVENC settings | QuickSync / AMF | Size | VMAF | PSNR (Y) |
|---|---|---|---|---|---|
| GPU Turbo | p7, two-pass full-res, 32-frame lookahead, AQ | veryslow / quality | 48.7 MB | 94.82 | 41.27 dB |
| GPU Balanced | p4, single pass, 8-frame lookahead, AQ | medium / balanced | 50.1 MB | 94.73 | 41.23 dB |
| GPU Fast | p2, single pass, no lookahead, no AQ | veryfast / speed | 45.3 MB | 94.37 | 40.47 dB |

Measured on a desktop NVIDIA GPU (AV1 NVENC, CQ 36, 10-bit, Opus audio) with a 30 s 2560×1440 60 fps HEVC game capture
(92.6 MB, constant frame rate fix on); output sizes were identical in every run.
Quality is ffmpeg `libvmaf` (default model, every second frame) and PSNR against a lossless constant-frame-rate copy of the
source. The QuickSync and AMF settings follow the same pattern but were not measured (no such hardware on the test machine).
Encode times are not listed yet: they have to be measured on an idle machine, and the preset settings above were
chosen from an earlier sweep whose timings were taken under GPU load. In that sweep
p1 scored lower than p2 (VMAF 93.97) and p3 was within 0.1 VMAF of p2, so nothing below p2 is offered.

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
