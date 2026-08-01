# HighlightForge

HighlightForge is a Windows-first, privacy-first desktop editor for turning local OBS recordings into reviewable gaming highlights. It never sends footage or transcripts to a cloud API.

## Current prototype

The desktop app can import OBS MKV/MP4/MOV recordings, preserve their source files, confirm separate OBS audio-track roles, open and save local `.gheproj` projects, preview recordings, and edit a non-destructive timeline. The local intelligence panel uses FFmpeg to measure commentary/game-audio dynamics and sparse scene changes, ranks explainable candidates, and builds an editable draft. Timeline edits support split, trim-to-playhead, reorder, ripple delete, undo/redo, and autosave.

Creator tabs install a pinned SHA-256-verified English Whisper model once, transcribe confirmed audio entirely on-device, persist editable caption text/timing, and export SRT/VTT sidecars. Silent gameplay gets local voice-over talking points; microphone takes are recorded only under the project `takes` directory. The audio panel measures confirmed source tracks without changing them, excludes the combined track when discrete microphone/game tracks are usable, and displays the conservative ducking and −14 LUFS / −1 dBTP mastering plan.

The analysis worker also exposes a local validation entry point:

```powershell
dotnet run --project src/HighlightForge.Worker -- --analyze-source <project-directory> <media-path> Balanced
dotnet run --project src/HighlightForge.Worker -- --transcribe-source <project-directory> <media-path> Fast
dotnet run --project src/HighlightForge.Worker -- --measure-source <project-directory> <media-path>
```

Current highlight detection is an offline heuristic prototype. YAMNet, Florence, Phi narrative guidance, production render integration, and the complete export/release workflow remain under active development.

## Prerequisites

- Windows 10 22H2 or Windows 11 x64
- .NET SDK 10
- FFmpeg/FFprobe on `PATH`, or set `HIGHLIGHTFORGE_FFPROBE_PATH` to `ffprobe.exe`

## Run

```powershell
dotnet restore
dotnet test
dotnet run --project src/HighlightForge.App
```

Source recordings are immutable: they are opened read-only and never overwritten, renamed, moved, deleted, or rendered in place. Generated proxies, extracted transcription audio, analysis data, and thumbnails belong in each project's disposable `cache` directory; voice-over recordings belong in its `takes` directory; final renders require a distinct user-selected output path.

## Diagnostics

Import attempts and unexpected application errors are written to `%LOCALAPPDATA%\HighlightForge\logs\highlightforge-YYYY-MM-DD.log`. Include this log when reporting a problem; source footage is never copied into it.
