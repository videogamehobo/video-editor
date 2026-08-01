using HighlightForge.Core.Persistence;
using HighlightForge.Core.Voiceover;
using HighlightForge.Media.Runtime;
using NAudio.Wave;

namespace HighlightForge.Media.Voiceover;

public sealed class VoiceoverRecorder : IDisposable
{
    private WaveInEvent? _capture;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<StoppedEventArgs>? _stopped;
    private string? _outputPath;
    private TimeSpan _timelineStart;
    private DateTimeOffset _startedUtc;

    public bool IsRecording => _capture is not null;

    public string Start(ProjectPaths projectPaths, TimeSpan timelineStart, int deviceNumber = 0)
    {
        if (IsRecording) throw new InvalidOperationException("A voice-over take is already being recorded.");
        projectPaths.EnsureDirectories();
        var outputPath = Path.Combine(projectPaths.TakesDirectory, $"voiceover-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.wav");
        outputPath = MediaPathSafety.RequireOutputWithinDirectory(projectPaths.TakesDirectory, outputPath, "Voice-over recording");

        var capture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(48_000, 16, 1),
            BufferMilliseconds = 100
        };
        var writer = new WaveFileWriter(outputPath, capture.WaveFormat);
        var stopped = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.DataAvailable += (_, eventArgs) => writer.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
        capture.RecordingStopped += (_, eventArgs) => stopped.TrySetResult(eventArgs);

        _capture = capture;
        _writer = writer;
        _stopped = stopped;
        _outputPath = outputPath;
        _timelineStart = timelineStart;
        _startedUtc = DateTimeOffset.UtcNow;
        try
        {
            capture.StartRecording();
        }
        catch
        {
            _capture = null;
            _writer = null;
            _stopped = null;
            _outputPath = null;
            writer.Dispose();
            capture.Dispose();
            if (File.Exists(outputPath)) File.Delete(outputPath);
            throw;
        }
        return outputPath;
    }

    public async Task<VoiceoverTake> StopAsync(CancellationToken cancellationToken = default)
    {
        if (_capture is null || _writer is null || _stopped is null || _outputPath is null)
        {
            throw new InvalidOperationException("No voice-over take is being recorded.");
        }

        _capture.StopRecording();
        var stopped = await _stopped.Task.WaitAsync(cancellationToken);
        var capture = _capture;
        var writer = _writer;
        var outputPath = _outputPath;
        var timelineStart = _timelineStart;
        var wallClockDuration = DateTimeOffset.UtcNow - _startedUtc;
        _capture = null;
        _writer = null;
        _stopped = null;
        _outputPath = null;
        var duration = writer.TotalTime > TimeSpan.Zero ? writer.TotalTime : wallClockDuration;
        writer.Dispose();
        capture.Dispose();
        if (stopped.Exception is not null) throw new InvalidOperationException("The microphone stopped with an error.", stopped.Exception);
        return new VoiceoverTake(Guid.NewGuid(), outputPath, timelineStart, duration, IsSelected: false);
    }

    public void Dispose()
    {
        if (_capture is not null)
        {
            _capture.StopRecording();
            _capture.Dispose();
            _capture = null;
        }
        _writer?.Dispose();
        _writer = null;
    }
}
