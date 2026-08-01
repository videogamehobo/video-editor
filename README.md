# HighlightForge

HighlightForge is a Windows-first, privacy-first desktop editor for turning local OBS recordings into reviewable gaming highlights. It never sends footage or transcripts to a cloud API.

## Current prototype

The desktop app can import OBS MKV/MP4/MOV recordings, preserve their source files, confirm separate OBS audio-track roles, open and save local `.gheproj` projects, preview recordings, and edit a non-destructive timeline. The local intelligence panel uses FFmpeg to measure commentary/game-audio dynamics and sparse scene changes, ranks explainable candidates, and builds an editable draft. Timeline edits support split, trim-to-playhead, reorder, ripple delete, undo/redo, and autosave.

Creator tabs install a pinned SHA-256-verified English Whisper model once, transcribe confirmed audio entirely on-device, persist editable caption text/timing, and export SRT/VTT sidecars. Silent gameplay gets local voice-over talking points; microphone takes are recorded only under the project `takes` directory. The audio panel measures confirmed source tracks without changing them, excludes the combined track when discrete microphone/game tracks are usable, and applies conservative ducking plus two-pass mastering to −14 LUFS integrated and no higher than −1 dBTP.

The Export tab renders the edited timeline—not the original full recording—to H.264/AAC MP4. It supports long-form output, safe 1080×1920 Shorts with the full game frame over a blurred background, confidence-gated/manual focus crops, burned styled captions, SRT/VTT sidecars, local metadata suggestions, progress, cancellation, NVIDIA acceleration when usable, and a Windows-compatible CPU fallback. Every completed render is checked with FFprobe for codecs, dimensions, duration, and A/V sync, then checked for final loudness and true peak before it replaces the selected destination.

The analysis worker also exposes a local validation entry point:

```powershell
dotnet run --project src/HighlightForge.Worker -- --analyze-source <project-directory> <media-path> Balanced
dotnet run --project src/HighlightForge.Worker -- --multimodal-source <project-directory> <media-path> <yamnet-directory> <ocr-directory> 60 <florence-directory>
dotnet run --project src/HighlightForge.Worker -- --transcribe-source <project-directory> <media-path> Fast
dotnet run --project src/HighlightForge.Worker -- --measure-source <project-directory> <media-path>
dotnet run --project src/HighlightForge.Worker -- --render-source <project-directory> <media-path> <output.mp4> LongForm 30 0
dotnet run --project src/HighlightForge.Worker -- --benchmark <creator-benchmark.json>
```

The benchmark command enforces the release thresholds from the product plan: at least ten creator-annotated sessions, at least 80% recall of must-keep moments in the review queue, and at least 60% creator acceptance of top-draft clips. It exits nonzero when a gate fails.

The intelligence pipeline is fully local: FFmpeg-derived audio/scene/motion signals, Whisper transcription, YAMNet sound events, sparse OCR, Florence-2 visual context, and optional Phi-4 Mini narrative suggestions. Fast mode favors speed; Balanced (the default) adds all local model types; Deep samples more densely. Model-dependent stages run after their pinned, SHA-256-verified packs are installed and fall back to explainable deterministic analysis if an optional model cannot run.

Two evidence-based release gates intentionally remain external to automated tests: ten sessions of creator annotations for recall/acceptance calibration, and the four-hour performance run on the specified RTX 3060-class target machine. The benchmark command and release verification scripts fail clearly when their required evidence is not supplied; they do not manufacture passing data.

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

## Package and verify

Install an LGPL FFmpeg build and Inno Setup 6, then run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package.ps1
powershell -ExecutionPolicy Bypass -File scripts\verify-package.ps1
```

The package is self-contained for Windows x64 and includes replaceable FFmpeg/FFprobe executables with their license. Verification starts the packaged application, exercises a silent install and uninstall in a temporary directory under `artifacts`, and checks the bundled media tools. Model files are downloaded separately on demand, verified against pinned manifests, retained by version, and available offline after installation with rollback to a still-verified prior version.

Source recordings are immutable: they are opened read-only and never overwritten, renamed, moved, deleted, or rendered in place. Generated proxies, extracted transcription audio, analysis data, and thumbnails belong in each project's disposable `cache` directory; voice-over recordings belong in its `takes` directory; final renders require a distinct user-selected output path.

HighlightForge has no cloud-storage, platform-upload, footage-upload, or cloud-inference integration. Network access is used only when the creator explicitly installs a pinned model pack; footage and transcripts remain on the device.

## Diagnostics

Import attempts and unexpected application errors are written to `%LOCALAPPDATA%\HighlightForge\logs\highlightforge-YYYY-MM-DD.log`. Include this log when reporting a problem; source footage is never copied into it.
