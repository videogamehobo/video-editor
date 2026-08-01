# HighlightForge

HighlightForge is a Windows-first, privacy-first desktop editor for turning local OBS recordings into reviewable gaming highlights. It never sends footage or transcripts to a cloud API.

## Current foundation

The initial milestone establishes the .NET/Avalonia desktop shell, a versioned non-destructive project format backed by SQLite, an FFprobe media-probing boundary, and a separate analysis worker process.

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
