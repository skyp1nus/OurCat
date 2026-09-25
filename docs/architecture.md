# Architecture

OurCut has four parts. Only the App knows about Avalonia; Core has no dependencies at all, so the same
editing operations are driven by the UI and by Claude through the MCP server.

```
OurCut.App    Avalonia views and view models ──┐
OurCut.Media  libmpv, ffprobe/ffmpeg, previews ─┼──> OurCut.Core   project model, commands, undo/redo, .ourcut.json
OurCut.Mcp    MCP tools, pipe server, bridge ───┘
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

The session is not thread-safe. The MCP server runs every tool call on the UI thread (see below).

### Commands (and their MCP tools)

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
| `CutRangesCommand` | `cut_silences` | Cuts source ranges (pauses) out of the clips they touch, splitting them |

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

## Silence and scene detection

Both live in `OurCut.Media.Analysis` and keep their raw measurements, so a different sensitivity is instant.

- **Silences** (`SilenceDetector`) come from the waveform the timeline already has: 10 ms peak buckets per audio
  track, like ffmpeg's `silencedetect` but without decoding the audio again. A stretch counts as silent when every
  chosen track stays under the level for at least the minimum length (1 s on the timeline). The default level is
  12 dB over the noise floor (the level of the quietest 5 % of the audio), kept between −55 and −35 dBFS, so a
  noisy microphone still has pauses and quiet music is not taken for one.
- **Scene changes** (`SceneDetector`) need the whole video decoded, so they run after the rest of the analysis
  (status bar: "detecting scenes 34%"), with half the CPU cores. ffmpeg shrinks every frame (at the video's own
  rate, up to 60 fps, timed from the file start like keyframes) to 64×36 grey and pipes it out; each frame is
  scored against the one before as ffmpeg's `scdet` does: the mean difference, but no more than its jump from the
  previous frame's, so steady motion (scrolling, panning) scores low and a cut scores high. The per-frame scores
  are cached (`scenes.bin`); changes are the frames at or over the threshold (10 by default, `scdet`'s), with
  changes less than 0.5 s apart counted once.
- `CutRangesCommand` (Core) cuts source ranges out of clips: it trims or splits every clip a range touches and
  drops leftovers shorter than the minimum clip length. `cut_silences` uses it (after keeping the whole video if
  there are no clips yet), so removing every pause is one undo step.

## MCP server

Claude edits the open project through MCP tools (`OurCut.Mcp.EditorTools`). There are two processes:

```
Claude ──stdio──> OurCut.exe mcp (McpBridge) ──named pipe──> OurCut editor (McpPipeServer → EditorMcpHost → UI thread)
```

- **The bridge** is what Claude starts (`OurCut.exe mcp`; `Program.Main` never starts Avalonia in this mode). It
  lists the tools itself, from the same `EditorTools` definitions, so Claude Desktop can start its MCP servers at
  launch without OurCut popping up. The first tool call connects to the editor's pipe, starting the editor
  (`EditorLauncher`) if it is not running, and every call is forwarded as is. If the editor was closed since,
  the next call starts it again.
- **The pipe server** runs in the editor (not in `--demo` runs). The pipe is `ourcut-mcp-<user>`, created with
  `PipeOptions.CurrentUserOnly`, so only the same user account can connect. One editor serves it: the one that
  holds `<temp>/ourcut-mcp-<user>.lock` (released by the system when that editor exits, even if it crashes). A
  second window shows "MCP · in another window" and takes over when the first one closes. On Windows the first pipe
  instance is also created with `FirstPipeInstance`.
- **The host** (`EditorMcpHost` in the App) runs each tool call on the UI thread, where the session and the view
  models live, so a tool call never lands in the middle of a user edit. Edits go through `EditorSession.Execute`
  with `EditOrigin.Assistant`: the Claude panel shows each one as a highlighted card with its own Undo
  (`EditorSession.Revert`), the clips of the latest one get a pulsing blue ring on the timeline, and the title bar
  badge shows "MCP · Claude editing" for a few seconds after each call.

| Tool | Does |
| --- | --- |
| `get_project` | Source, playhead, selection and clips in output order |
| `get_history` | Recent edits (user's and Claude's) with ids for `revert_action` |
| `find_keyframes` | Keyframe times in a range (lossless cuts start on them) |
| `find_silences` | Pauses at a minimum length and level (automatic by default), on all or some audio tracks |
| `find_scene_changes` | Scene changes at a sensitivity; what is found so far while detection runs |
| `cut_silences` | Cuts the pauses out of the included (or given) clips as one undo step, keeping some padding |
| `list_videos` | Video files in a folder, newest first |
| `add_segment`, `remove_segment`, `trim_segment`, `split_segment`, `set_included`, `move_segment`, `set_label` | One edit each (the commands above) |
| `edit_timeline` | Several edits as one undo step, all or nothing |
| `revert_action`, `undo`, `redo` | Take edits back |
| `seek`, `set_playing` | Show a frame or play |
| `open_file`, `save_project` | Open a video or project; save as `.ourcut.json` (full paths only) |
| `export`, `get_export_status`, `cancel_export` | Export like the Export button (the dialog shows the progress); choices left out keep the dialog's; waits up to 20 s, then Claude polls |

A refused edit (`EditException`, e.g. "Clip 7 does not exist") goes back to Claude as a tool error it can act on.
Results are JSON; times are seconds, rounded to milliseconds, with `MM:SS.mmm` ranges for talking to the user.

## Extension points
- **Smart cut**: `CutMode.SmartCut` exists in the export settings; the planner rejects it for now. It becomes
  a third kind of plan (re-encode the GOP around each cut, copy the rest, concat). The dialog lists it as not
  yet available.
- **GPU transcription**: sherpa-onnx runs on the CPU; the Device setting has no effect yet.

## Transcription

`OurCut.Transcription` (no UI references) turns speech into words with times, locally, with sherpa-onnx (ONNX
Runtime, CPU). Settings → Transcription lists the models (`ModelCatalog`), all int8 builds from the sherpa-onnx
GitHub releases (.tar.bz2): Parakeet TDT 0.6B v3 (25 European languages including Ukrainian and English; the default)
and Whisper large-v3-turbo, small and base.en (99 languages; base.en English only).

- **Audio**: ffmpeg mixes every audio track to 16 kHz mono float (`SpeechAudio`), timed from the file start like
  keyframes, and pipes it out.
- **Pieces** (`TranscriptionPipeline`): the stream is cut into pieces of at most 28 s, each ending at the quietest
  100 ms after 18 s, so words are not cut in half and memory stays small. Each piece is recognized in turn.
- **Words** (`WordBuilder`): the models give subword tokens (a leading space starts a word) with start times;
  punctuation joins the word before it; a word followed by a pause ends after about as long as it takes to say.
  The published Whisper models give no times, so their words get estimated ones: the speech in the piece (stretches
  louder than the background) is shared out in proportion to word length, and the transcript says its times are
  approximate.
- **In the editor** (`MediaPreview`): transcription starts when a file is opened and a model is installed, after
  keyframes, waveform and thumbnails (it may overlap scene detection), on half the cores. The status bar shows
  "transcribing 34%", the transcript fills in piece by piece and is cached per model and language
  (`transcript-<model>-<language>.json`). Installing a model or changing the model or language starts it.

- `ModelInstaller` downloads into `<models folder>/<id>.partial`, continuing an interrupted download with an HTTP
  range request, unpacks archives there (SharpZipLib's bzip2 + `System.Formats.Tar`, without the archive's top
  folder, refusing entries that point outside it) and renames the folder to `<id>` only when every file the model
  needs is there. It checks free space first and explains failures (HTTP status, lost connection, full disk).
- `ModelStore` answers whether a model is installed (all its files present), its size, and deletes it.
- The settings (engine, model, device, language, models folder) are saved to `%LOCALAPPDATA%\OurCut\settings.json`;
  models go to `%LOCALAPPDATA%\OurCut\models` unless another folder is chosen. Cancelling a download removes it;
  a failed one is kept and continues on the next try.

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
