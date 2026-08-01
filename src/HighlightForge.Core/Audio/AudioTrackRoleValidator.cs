using HighlightForge.Core.Domain;

namespace HighlightForge.Core.Audio;

public sealed record AudioTrackRoleValidation(bool IsValid, bool UsesDiscreteTracks, string Message);

public static class AudioTrackRoleValidator
{
    public static AudioTrackRoleValidation Validate(IReadOnlyList<AudioTrack> tracks)
    {
        if (tracks.Count(track => track.Role == AudioTrackRole.Microphone) > 1 ||
            tracks.Count(track => track.Role == AudioTrackRole.Game) > 1 ||
            tracks.Count(track => track.Role == AudioTrackRole.Mixed) > 1)
        {
            return new(false, false, "Assign no more than one Microphone, Game, and Mixed track.");
        }
        var hasDiscrete = tracks.Any(track => track.Role == AudioTrackRole.Microphone) &&
            tracks.Any(track => track.Role == AudioTrackRole.Game);
        var hasMixed = tracks.Any(track => track.Role == AudioTrackRole.Mixed);
        if (!hasDiscrete && !hasMixed)
        {
            return new(false, false, "Assign either a Mixed track or both Microphone and Game roles before confirming.");
        }
        return new(true, hasDiscrete, hasDiscrete
            ? "Microphone and Game will be used; Mixed is excluded from the edit mix."
            : "The Mixed track will be used.");
    }
}
