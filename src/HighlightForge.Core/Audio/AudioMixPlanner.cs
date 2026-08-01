using HighlightForge.Core.Domain;

namespace HighlightForge.Core.Audio;

public sealed record AudioMixSettings(
    double TargetIntegratedLufs = -14,
    double TruePeakDbtp = -1,
    double DuckingDb = -10,
    int DuckingAttackMs = 60,
    int DuckingReleaseMs = 450);

public sealed record AudioMixPlan(IReadOnlyList<AudioTrack> InputTracks, AudioMixSettings Settings, bool UsesDiscreteTracks, string Explanation);

public sealed record AudioLoudnessMeasurement(
    int StreamIndex,
    string DisplayName,
    double IntegratedLufs,
    double TruePeakDbtp,
    double LoudnessRangeLu,
    double ThresholdLufs,
    double TargetOffsetLu);

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

    public static string BuildMeasuredLoudnessFilter(AudioMixSettings settings, AudioLoudnessMeasurement measurement)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return $"loudnorm=I={settings.TargetIntegratedLufs.ToString(culture)}:LRA=11:TP={settings.TruePeakDbtp.ToString(culture)}" +
            $":measured_I={measurement.IntegratedLufs.ToString(culture)}:measured_LRA={measurement.LoudnessRangeLu.ToString(culture)}" +
            $":measured_TP={measurement.TruePeakDbtp.ToString(culture)}:measured_thresh={measurement.ThresholdLufs.ToString(culture)}" +
            $":offset={measurement.TargetOffsetLu.ToString(culture)}:linear=true:print_format=summary";
    }

    public static string BuildDiscreteDuckingFilter(AudioTrack microphone, AudioTrack game, AudioMixSettings settings)
    {
        if (microphone.Role != AudioTrackRole.Microphone) throw new ArgumentException("A microphone track is required.", nameof(microphone));
        if (game.Role != AudioTrackRole.Game) throw new ArgumentException("A game-audio track is required.", nameof(game));
        return $"[0:{microphone.StreamIndex}]highpass=f=80,afftdn=nf=-25[mic];" +
            $"[0:{game.StreamIndex}][mic]sidechaincompress=threshold=0.035:ratio=8:attack={settings.DuckingAttackMs}:release={settings.DuckingReleaseMs}[ducked];" +
            "[ducked][mic]amix=inputs=2:normalize=0[mix]";
    }
}
