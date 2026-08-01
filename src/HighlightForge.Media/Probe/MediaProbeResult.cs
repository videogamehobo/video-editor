using HighlightForge.Core.Domain;

namespace HighlightForge.Media.Probe;

public sealed record MediaProbeResult(
    string AbsolutePath,
    TimeSpan Duration,
    int Width,
    int Height,
    double FramesPerSecond,
    IReadOnlyList<AudioTrack> AudioTracks)
{
    public MediaSource ToSource() => new(
        Guid.NewGuid(),
        AbsolutePath,
        Duration,
        Width,
        Height,
        FramesPerSecond,
        AudioTracks);
}
