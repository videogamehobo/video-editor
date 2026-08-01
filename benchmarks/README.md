# Creator benchmark format

Release evaluation consumes a local JSON array with at least ten creator-annotated sessions. Keep footage outside the repository; the benchmark contains timestamps and analysis results only.

```json
[
  {
    "id": "session-01",
    "mustKeepMoments": [{ "start": "00:01:10", "end": "00:01:18" }],
    "creatorAcceptedMoments": [{ "start": "00:01:08", "end": "00:01:20" }],
    "reviewQueue": [
      { "sourceIn": "00:01:05", "sourceOut": "00:01:22", "score": 1.25, "reasons": [] }
    ],
    "draftClips": [
      { "sourceIn": "00:01:05", "sourceOut": "00:01:22", "score": 1.25, "reasons": [] }
    ]
  }
]
```

Run the gate locally:

```powershell
dotnet run --project src\HighlightForge.Worker -- --benchmark C:\path\to\creator-benchmark.json
```

The process exits successfully only when the dataset has ten unique sessions, must-keep recall is at least 80%, and creator acceptance of the top-ranked draft is at least 60%. Empty annotations or drafts fail the gate.
