using HighlightForge.Core.Domain;

namespace HighlightForge.Core.Audio;

public sealed record AudioMixSettings(
    double TargetIntegratedLufs = -14,
    double TruePeakDbtp = -1,
    double DuckingDb = -10,
    int DuckingAttackMs = 60,
    int DuckingReleaseMs = 450,
    double MicrophoneGainDb = 0,
    double GameGainDb = 0)
{
    public AudioMixSettings Validated()
    {
        RequireRange(TargetIntegratedLufs, -24, -9, nameof(TargetIntegratedLufs));
        RequireRange(TruePeakDbtp, -6, -1, nameof(TruePeakDbtp));
        RequireRange(DuckingDb, -24, 0, nameof(DuckingDb));
        ArgumentOutOfRangeException.ThrowIfLessThan(DuckingAttackMs, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DuckingAttackMs, 2000);
        ArgumentOutOfRangeException.ThrowIfLessThan(DuckingReleaseMs, 20);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DuckingReleaseMs, 5000);
        RequireRange(MicrophoneGainDb, -24, 12, nameof(MicrophoneGainDb));
        RequireRange(GameGainDb, -24, 12, nameof(GameGainDb));
        return this;
    }

    public double DuckingRatio => Math.Clamp(1 + (-DuckingDb / 2), 1, 20);

    private static void RequireRange(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"{name} must be between {minimum} and {maximum}.");
        }
    }
}

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
            (settings ?? new AudioMixSettings()).Validated(),
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
            $":offset={measurement.TargetOffsetLu.ToString(culture)}:linear=false:print_format=summary" +
            ",volume=0.9dB,alimiter=limit=0.85:level=false";
    }

    public static string BuildDiscreteDuckingFilter(AudioTrack microphone, AudioTrack game, AudioMixSettings settings)
    {
        if (microphone.Role != AudioTrackRole.Microphone) throw new ArgumentException("A microphone track is required.", nameof(microphone));
        if (game.Role != AudioTrackRole.Game) throw new ArgumentException("A game-audio track is required.", nameof(game));
        settings.Validated();
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return $"[0:{microphone.StreamIndex}]highpass=f=80,afftdn=nf=-25,volume={settings.MicrophoneGainDb.ToString(culture)}dB[mic];" +
            $"[0:{game.StreamIndex}]volume={settings.GameGainDb.ToString(culture)}dB[game];" +
            $"[game][mic]sidechaincompress=threshold=0.035:ratio={settings.DuckingRatio.ToString(culture)}:attack={settings.DuckingAttackMs}:release={settings.DuckingReleaseMs}[ducked];" +
            "[ducked][mic]amix=inputs=2:normalize=0[mix]";
    }
}
