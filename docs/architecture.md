# Architecture

OurCut has three layers. Only the App knows about Avalonia; Core has no dependencies at all, so the same
editing operations can be driven by the UI today and by an MCP server (Claude) later.

```
OurCut.App    Avalonia views and view models ──┐
                                                ├──> OurCut.Core   project model, commands, undo/redo, .ourcut.json
OurCut.Media  libmpv, ffprobe/ffmpeg, previews ─┘
```

## Core

- **Model** (`OurCut.Core.Model`): immutable records. A `Project` has one `SourceMedia` (path, duration,
  frame rate, audio tracks) and an ordered list of `Clip`s. List order is output order. Times are
  seconds on the source timeline. Excluded clips (`IsIncluded = false`) stay in the project but are not exported.
- **Commands** (`OurCut.Core.Editing`): every change is an `IEditCommand` that turns one `Project` into the
  next, or throws `EditException` with a readable reason. Commands never clamp or guess; callers do that.
- **Session**: `EditorSession` holds the current project and a linear `History`. `Execute` applies a command
  and records it with its origin (`User` or `Assistant`). Edits sharing a merge key (one drag) become one
  undo step. `Changed` fires after every edit, undo, redo and load.
- **Files**: `ProjectFile` reads and writes `.ourcut.json` (see below).

The session is not thread-safe. A future MCP server must marshal its calls to the UI thread.

### Commands (future MCP tools)

| Command | `Name` | What it does |
| --- | --- | --- |
| `AddClipCommand` | `add_segment` | Adds a clip for a source range, at the end or at a position |
| `RemoveClipCommand` | `remove_segment` | Removes a clip from the project |
| `SetClipRangeCommand` | `trim_segment` | Sets a clip's in- and out-point |
| `SplitClipCommand` | `split_segment` | Splits a clip in two at a source time |
| `SetClipIncludedCommand` | `set_included` | Excludes a clip from the export or keeps it again |
| `MoveClipCommand` | `move_segment` | Moves a clip to another output position |
| `RenameClipCommand` | `set_label` | Renames a clip |
| `BatchCommand` | any | Several commands as one undo step |
| `RevertEditCommand` | `revert_action` | Reverts one earlier edit and keeps the edits made after it |

`EditorSession` wraps these with UI-friendly helpers (`Trim` clamps and snaps to keyframes, `KeepRange`
inserts by source position, `Split` returns the new clip).

`Revert(entry)` is the Undo on a single card in the Claude panel. Unlike `Undo`, which steps back through the
history, it applies a `RevertEditCommand`: the clips that edit added, removed, changed or reordered go back to how
they were before it, and every other clip stays as it is now. The revert is an ordinary edit, so Ctrl+Z undoes it
and it can be reverted in turn (`RevertOf`, `IsReverted`). If a later edit changed one of the same clips there is no
single right answer, so the command refuses with an `EditException` and the UI shows the reason.

## Media

`OurCut.Media` runs ffprobe and ffmpeg as separate processes, always off the UI thread. Paths go through
`ProcessStartInfo.ArgumentList` (or FFMpegCore's quoting), never through a shell.

- **Probing** (`MediaProbe`): `ffprobe -show_format -show_streams` as JSON → `MediaInfo`: container family,
  the first real video stream (cover art is skipped), audio streams with their titles (MP4 keeps track names in
  the handler name), subtitles, rotation, B-frames. `ToSourceMedia()` gives Core's description.
- **Keyframes** (`KeyframeScanner`): packet flags from ffprobe, no decoding. Times are relative to the file's
  start time, like everything else in OurCut.
- **Previews**: `WaveformExtractor` decodes every audio stream in one pass to 8 kHz mono and keeps one peak per
  10 ms (`WaveformData`, drawn on a dB scale). `ThumbnailExtractor` decodes only keyframes
  (`-skip_frame nokey`) to raw BGRA, at most about 300 per file. Both stream their results as they arrive.
- **Cache** (`MediaCache`): keyframes, waveform and a JPEG thumbnail atlas per file in
  `%LOCALAPPDATA%\OurCut\cache`, keyed by path, size and modification time.
- **Export**: `ExportPlanner` turns the project and `ExportSettings` into an `ExportPlan` (every step, output
  and temporary file decided up front, so it can be tested and shown); `FfmpegCommands` builds each step's
  ffmpeg command with FFMpegCore; `ExportRunner` runs the steps with progress and cancellation.

In the App, `FfmpegMediaOpener` probes a file and creates a `MediaPreview`, which runs the three analyses in
parallel (or reads them from the cache) and raises `Changed` as results arrive; the timeline redraws, and the
keyframes are handed to the editing session for snapping.

### Lossless cuts

With `-ss` before `-i` and `-c copy`, ffmpeg starts every stream at a keyframe. OurCut makes that explicit:
`CutPlanner` moves each clip's in-point back to the keyframe at or before it (nothing is lost; a short lead-in
is added) and computes the `-ss` value that makes ffmpeg land exactly on that keyframe. MP4/MOV seek by
presentation time, so a value just after the keyframe works. Matroska and most other demuxers seek
3/23 s earlier when the video has B-frames; the planner adds that back. Transport streams have no index, so
their cut points are marked approximate.

ffmpeg ends a stream copy by decode time, so with B-frames each clip comes out a few frames longer than planned.
A merged lossless export cuts every clip to a temporary file and joins them with the concat demuxer; chapters are
written afterwards from the real length of each cut, so they start exactly where their clip does.

A re-encoded merge is one ffmpeg pass: each clip is its own frame-accurately seeked input, joined with the concat
filter. Re-encoding one clip per file copies audio when asked, dropping packets before the in-point
(`-copypriorss 0`).

Output names: `{project}-cut.{ext}` when merged, `{project}-{n}-{label}.{ext}` otherwise; existing files are never
overwritten (" (2)" is added) and an export never writes over its source. Temporary files
(`.ourcut-tmp-*`) and any half-written output are removed on failure or cancel.

## Playback

`OurCut.Media.Playback` talks to libmpv directly (`LibraryImport`, client API 2.x; `libmpv-2.dll` from
`fetch-deps.ps1`, `libmpv.so.2` on Linux).

- **`MpvPlayer`** owns one mpv core. Everything that controls playback (load, play, pause, seek, frame step,
  volume, speed, audio tracks) goes through `mpv_command_async`, so the calling thread never waits for the core.
  That matters because the UI thread also renders video, and mpv's render API forbids waiting for the core on a
  render thread. State comes back as observed properties (`time-pos`, `pause`, `duration`, `eof-reached`, video
  size) on a background event thread, which raises `StateChanged`.
- **Seeking** is exact (`hr-seek`). While a seek is in flight, `Position` reports the target and `IsSeeking` is
  true; only the playback restart after the newest seek settles the position, so a dragged playhead never jumps
  back to stale positions.
- **Audio tracks**: the timeline's lanes map to mpv's `aid1…N`. One unmuted lane plays directly (`aid`),
  several are mixed with `lavfi-complex` (`amix`), none sets `aid=no`. Muting affects the preview only.
- **Video** goes through the render API into OurCut's own view: `MpvOpenGlRenderer` draws into Avalonia's OpenGL
  framebuffer; `MpvSoftwareRenderer` renders BGRX frames into memory on a background thread. `vo=libmpv` without
  a render context fails the whole file, so the player uses `vo=null` until a renderer is attached and reopens
  the file where it was when one attaches or detaches.

In the App, `IPlayer` is what `EditorViewModel` uses (`MpvPlaybackEngine` in the app, a fake in tests). The view
model keeps the playhead: user moves become seeks, the player's positions come back as `Time` without seeking
again. `VideoView` shows the video (OpenGL first, software if OpenGL is not there within two seconds or fails);
until its first frame the thumbnail preview underneath shows through. Without libmpv, or for the design's
sample, playback is simulated over the thumbnails.

## Extension points

- **MCP server**: call `EditorSession.Execute` with `EditOrigin.Assistant`. The Claude panel logs each edit as a
  card with its own Undo (`EditorSession.Revert`), the clips of the latest assistant edit get a pulsing blue ring on
  the timeline, and the title bar badge shows the connection. Until the server exists the badge reads
  "MCP · not running" and the panel lists the project's edit history.
- **Smart cut**: `CutMode.SmartCut` exists in the export settings; the planner rejects it for now. It becomes
  a third kind of plan (re-encode the GOP around each cut, copy the rest, concat). The dialog lists it as not
  yet available.
- **Transcription**: a future source of labels and ranges for `AddClipCommand` / `RenameClipCommand`.
  Settings → Transcription (engine, model, device, language, models folder) is saved to
  `%LOCALAPPDATA%\OurCut\settings.json`; outside demo mode the model table only reports which models are in the
  models folder, and downloads come with transcription.
- **Silence and scene detection**: `IMediaPreview.Silences` and `SceneChanges` feed the timeline's marker layers.
  They are empty for real files for now (only the demo sample has them), so those toolbar chips are disabled.

## Project file (`.ourcut.json`)

```json
{
  "format": "ourcut-project",
  "version": 1,
  "name": "launch-keynote",
  "source": {
    "path": "media/keynote_final_4k.mp4",
    "duration": 872.48,
    "frameRate": 29.97,
    "audioStreams": [ { "index": 1, "label": "Mic" } ]
  },
  "clips": [
    { "id": 1, "label": "Intro", "start": 12.04, "end": 45.32, "included": true }
  ]
}
```

- `path` is relative to the project file when the video is on the same drive, otherwise absolute.
- Times are seconds with an invariant decimal point.
- Readers ignore unknown fields. Files with a higher `version` than the app supports are rejected.
- Files are written atomically (temporary file, then replace). A saved project is autosaved after edits.
