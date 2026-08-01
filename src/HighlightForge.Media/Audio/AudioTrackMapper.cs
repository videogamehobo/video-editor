using HighlightForge.Core.Domain;

namespace HighlightForge.Media.Audio;

public sealed record TrackRoleSuggestion(int StreamIndex, AudioTrackRole Role, double Confidence, string Reason);

public sealed record AudioTrackMapping(IReadOnlyList<AudioTrack> Tracks, IReadOnlyList<TrackRoleSuggestion> Suggestions)
{
    public bool UsesDiscreteTracks =>
        Tracks.Any(track => track.Role == AudioTrackRole.Microphone) && Tracks.Any(track => track.Role == AudioTrackRole.Game);

    public IReadOnlyList<AudioTrack> TracksForMix()
    {
        if (UsesDiscreteTracks) return Tracks.Where(track => track.Role is AudioTrackRole.Microphone or AudioTrackRole.Game).ToArray();
        var mixed = Tracks.FirstOrDefault(track => track.Role == AudioTrackRole.Mixed);
        return mixed is not null ? [mixed] : Tracks.Count == 0 ? [] : [Tracks[0]];
    }
}

public static class AudioTrackMapper
{
    public static AudioTrackMapping Suggest(IReadOnlyList<AudioTrack> tracks)
    {
        var suggestions = tracks.Select(SuggestRole).ToArray();
        var applied = tracks.Select(track => track with
        {
            Role = suggestions.Single(suggestion => suggestion.StreamIndex == track.StreamIndex).Role
        }).ToArray();
        return new AudioTrackMapping(applied, suggestions);
    }

    private static TrackRoleSuggestion SuggestRole(AudioTrack track)
    {
        var name = track.DisplayName.ToLowerInvariant();
        if (ContainsAny(name, "microphone", "mic", "voice", "commentary"))
        {
            return new(track.StreamIndex, AudioTrackRole.Microphone, 0.95, "Track title identifies commentary or a microphone.");
        }
        if (ContainsAny(name, "game", "desktop", "system", "application"))
        {
            return new(track.StreamIndex, AudioTrackRole.Game, 0.92, "Track title identifies game or desktop audio.");
        }
        if (ContainsAny(name, "mixed", "mix", "combined", "master", "all audio"))
        {
            return new(track.StreamIndex, AudioTrackRole.Mixed, 0.90, "Track title indicates a combined mix.");
        }
        if (track.Channels == 1)
        {
            return new(track.StreamIndex, AudioTrackRole.Microphone, 0.55, "Mono audio is often a microphone track; confirm before editing.");
        }
        if (track.Channels >= 2)
        {
            return new(track.StreamIndex, AudioTrackRole.Unassigned, 0.25, "Stereo audio needs confirmation because it could be game or a mixed track.");
        }
        return new(track.StreamIndex, AudioTrackRole.Unassigned, 0, "No reliable metadata is available.");
    }

    private static bool ContainsAny(string value, params string[] needles) => needles.Any(value.Contains);
}
