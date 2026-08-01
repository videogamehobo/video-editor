# HighlightForge

HighlightForge is a Windows-first, privacy-first desktop editor for turning local OBS recordings into reviewable gaming highlights. It never sends footage or transcripts to a cloud API.

## Current prototype

The desktop app can import OBS MKV/MP4/MOV recordings, preserve their source files and audio-track roles, open and save local `.gheproj` projects, preview recordings, and edit a non-destructive timeline. The local intelligence panel uses FFmpeg to measure commentary/game-audio dynamics and sparse scene changes, ranks explainable candidates, and builds an editable draft. Timeline edits support split, trim-to-playhead, reorder, ripple delete, undo/redo, and autosave.

The analysis worker also exposes a local validation entry point:

```powershell
dotnet run --project src/HighlightForge.Worker -- --analyze-source <project-directory> <media-path> Balanced
```

Current intelligence is an offline heuristic prototype. The planned Whisper, YAMNet, Florence, and Phi model packs, caption/voice-over UI, production audio mastering, and complete export workflow remain under active development.

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

Source recordings are referenced in the project and never copied, moved, or altered. Generated proxies, analysis data, and thumbnails belong in each project's disposable `cache` directory.

## Diagnostics

Import attempts and unexpected application errors are written to `%LOCALAPPDATA%\HighlightForge\logs\highlightforge-YYYY-MM-DD.log`. Include this log when reporting a problem; source footage is never copied into it.
