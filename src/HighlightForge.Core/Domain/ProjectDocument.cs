namespace HighlightForge.Core.Domain;

public static class ProjectSchema
{
    public const int CurrentVersion = 1;
}

public sealed record ProjectDocument(
    Guid Id,
    int SchemaVersion,
    string Name,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    IReadOnlyList<MediaSource> Sources,
    IReadOnlyList<TimelineClip> Timeline)
{
    public static ProjectDocument Create(string name, DateTimeOffset now) =>
        new(Guid.NewGuid(), ProjectSchema.CurrentVersion, name, now, now, [], []);
}

public sealed record MediaSource(
    Guid Id,
    string AbsolutePath,
    TimeSpan Duration,
    int Width,
    int Height,
    double FramesPerSecond,
    IReadOnlyList<AudioTrack> AudioTracks,
    bool AudioRolesConfirmed = false);

public sealed record AudioTrack(
    int StreamIndex,
    string DisplayName,
    int Channels,
    int SampleRate,
    AudioTrackRole Role = AudioTrackRole.Unassigned);

public enum AudioTrackRole
{
    Unassigned,
    Microphone,
    Game,
    Mixed
}

public sealed record TimelineClip(
    Guid Id,
    Guid SourceId,
    TimeSpan SourceIn,
    TimeSpan SourceOut,
    TimeSpan TimelineIn,
    bool IsLocked = false);
