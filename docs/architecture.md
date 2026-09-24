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

`EditorSession` wraps these with UI-friendly helpers (`Trim` clamps and snaps to keyframes, `KeepRange`
inserts by source position, `Split` returns the new clip).

## Extension points

- **MCP server**: call `EditorSession.Execute` with `EditOrigin.Assistant`. The UI already highlights assistant
  edits in violet and shows them in the Claude panel with undo.
- **Smart cut**: the export pipeline (OurCut.Media) will choose a cut strategy per export mode; smart cut
  becomes one more strategy. The dialog lists it as not yet available.
- **Transcription**: a future source of labels and ranges for `AddClipCommand` / `RenameClipCommand`.

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
