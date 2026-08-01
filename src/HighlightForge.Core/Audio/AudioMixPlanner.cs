using HighlightForge.Core.Domain;

namespace HighlightForge.Core.Audio;

public sealed record AudioMixSettings(
    double TargetIntegratedLufs = -14,
    double TruePeakDbtp = -1,
    double DuckingDb = -10,
    int DuckingAttackMs = 60,
    int DuckingReleaseMs = 450);

public sealed record AudioMixPlan(IReadOnlyList<AudioTrack> InputTracks, AudioMixSettings Settings, bool UsesDiscreteTracks, string Explanation);

public static class AudioMixPlanner
{
    public static AudioMixPlan Create(IReadOnlyList<AudioTrack> tracks, bool usesDiscreteTracks, AudioMixSettings? settings = null)
    {
        var selected = usesDiscreteTracks
            ? tracks.Where(track => track.Role is AudioTrackRole.Microphone or AudioTrackRole.Game).ToArray()
            : tracks.Where(track => track.Role == AudioTrackRole.Mixed).ToArray();
        return new AudioMixPlan(
            selected,
            settings ?? new AudioMixSettings(),
            usesDiscreteTracks,
            usesDiscreteTracks
                ? "Microphone and game audio will be mixed separately; the combined OBS track is excluded to prevent duplication."
                : "Only one usable mix is available; loudness normalization will preserve it as a single source.");
    }

    public static string BuildFinalLoudnessFilter(AudioMixSettings settings) =>
        $"loudnorm=I={settings.TargetIntegratedLufs.ToString(System.Globalization.CultureInfo.InvariantCulture)}:LRA=11:TP={settings.TruePeakDbtp.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
