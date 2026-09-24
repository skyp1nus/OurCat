# OurCut

OurCut is an open-source desktop video editor for cutting long recordings down to the parts worth keeping.
You mark segments on a timeline of the whole source file, arrange them, and export them as one merged file
or as separate files. Cuts are lossless by default (stream copy, no re-encoding); re-encoding is optional.

It is inspired by [LosslessCut](https://github.com/mifi/lossless-cut). Every edit is a command in a UI-independent
core, so an AI assistant (Claude via MCP) can later edit the same timeline through the same operations.

> **Status:** early development, Phase 1. Windows only for now; the code is kept cross-platform so macOS and
> Linux can follow. The editor UI is in place with sample data; opening real files, playback (libmpv) and
> export (FFmpeg) are being connected milestone by milestone.

## Phase 1 scope

- Open a video (file dialog or drag and drop) and probe it with ffprobe: duration, frame rate, codecs, keyframes.
- Player: play/pause, frame step, precise seek, `HH:MM:SS.mmm` timecode, volume, speed.
- Timeline: thumbnails, audio waveform per audio track, playhead, zoom, keyframe markers,
  clips with draggable in/out handles. Parts that are not kept stay visible as "Excluded".
- Clips panel: reorder output by drag, include/exclude, total output duration.
- Export: lossless copy (cut points on keyframes) or re-encode; merge into one file or separate files;
  progress and cancel.
- Projects saved as `.ourcut.json`. Undo/redo for every edit.

Not in Phase 1: the MCP server, transcription and smart cut. The UI already has places for them.

## Keyboard shortcuts

| Key | Action |
| --- | --- |
| Space | Play / pause |
| I / O | Set in-point / out-point |
| ← / → | Previous / next frame (Shift: 1 second) |
| S | Split clip at playhead |
| E or Del | Exclude / keep the selected clip |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Ctrl+O | Open file |
| Ctrl+E | Export |

## Building

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download) and PowerShell (Windows PowerShell 5.1
that ships with Windows, or PowerShell 7).

```powershell
git clone https://github.com/skyp1nus/OurCat.git
cd OurCat
powershell -ExecutionPolicy Bypass -File scripts\fetch-deps.ps1   # or: pwsh scripts/fetch-deps.ps1
dotnet run --project src/OurCut.App
```

`fetch-deps.ps1` downloads ffmpeg, ffprobe and libmpv into `deps/win-x64/`. They are not stored in the
repository. The build copies them next to `OurCut.exe`. Versions, URLs and SHA-256 hashes are pinned in
[`scripts/deps.json`](scripts/deps.json), and the script refuses anything that does not match.
Useful options: `-Check` (verify only, no downloads), `-Force` (reinstall), `-Component ffmpeg`,
`-Proxy http://proxy:8080`. Downloads are cached in `deps/.cache`.

To see the UI with the sample project from the design, start it in demo mode:
`dotnet run --project src/OurCut.App -- --demo editing` (other screens: `empty`, `ai`, `export`, `exporting`).

Run the tests with `dotnet test OurCut.slnx`. The UI tests render the app headlessly and write screenshots to
`artifacts/screenshots/`.

Avalonia's build tooling sends anonymous build telemetry. Set `AVALONIA_TELEMETRY_OPTOUT=1` to turn it off
(CI does this).

## Project layout

```
src/OurCut.App      Avalonia UI: views and view models (CommunityToolkit.Mvvm)
src/OurCut.Core     Project model, timeline and edit commands with undo/redo. No UI references.
src/OurCut.Media    libmpv playback, ffprobe/ffmpeg (FFMpegCore), thumbnails and waveforms (SkiaSharp)
tests/              xUnit tests for Core, Media and headless UI tests for App
scripts/            fetch-deps.ps1 and the pinned dependency manifest
design/             The Claude Design export the UI is built from
```

## License

OurCut is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any
later version. See [LICENSE](LICENSE).

OurCut uses FFmpeg and mpv, which are downloaded separately and are also GPL-licensed. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for all third-party components and their licenses.
