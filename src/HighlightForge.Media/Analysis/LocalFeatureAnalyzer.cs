using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Persistence;
using HighlightForge.Media.Runtime;

namespace HighlightForge.Media.Analysis;

public static partial class LocalFeatureAnalyzer
{
    private sealed record AudioLevel(TimeSpan Position, double RmsDb);
    private sealed record SceneSample(TimeSpan Position, double Score);

    public static async Task<LocalAnalysisResult> AnalyzeAsync(
        ProjectPaths paths,
        MediaSource source,
        AnalysisMode mode,
        IProgress<AnalysisProgress>? progress = null,
        TimeSpan? analysisStart = null,
        TimeSpan? analysisLimit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(source);
        if (!File.Exists(source.AbsolutePath)) throw new FileNotFoundException("The source recording is no longer available.", source.AbsolutePath);

        var jobId = Guid.NewGuid();
        var checkpoints = new AnalysisJobStore(paths);
        var features = new List<FeatureEvent>();
        await ReportAsync("starting", 0.02, "Preparing local media analysis.");

        var microphone = source.AudioTracks.FirstOrDefault(track => track.Role == AudioTrackRole.Microphone);
        var game = source.AudioTracks.FirstOrDefault(track => track.Role == AudioTrackRole.Game);
        var mixed = source.AudioTracks.FirstOrDefault(track => track.Role == AudioTrackRole.Mixed)
            ?? (source.AudioTracks.Count > 0 ? source.AudioTracks[0] : null);

        if (microphone is not null)
        {
            await ReportAsync("microphone", 0.12, "Measuring commentary activity and vocal excitement.");
            var levels = await ExtractAudioLevelsAsync(source.AbsolutePath, microphone.StreamIndex, mode, analysisStart, analysisLimit, cancellationToken);
            features.AddRange(CreateMicrophoneFeatures(levels.Select(level => (level.Position, level.RmsDb)).ToArray(), SampleDuration(mode)));
        }

        if (game is not null || microphone is null)
        {
            var track = game ?? mixed;
            if (track is not null)
            {
                await ReportAsync("game-audio", 0.43, "Finding unusually strong game-audio moments.");
                var levels = await ExtractAudioLevelsAsync(source.AbsolutePath, track.StreamIndex, mode, analysisStart, analysisLimit, cancellationToken);
                features.AddRange(CreateGameAudioFeatures(levels.Select(level => (level.Position, level.RmsDb)).ToArray(), SampleDuration(mode)));
            }
        }

        var hasUsableAudio = features.Any(feature => feature.Kind is FeatureKind.Speech or FeatureKind.VocalExcitement or FeatureKind.GameAudioPeak);
        var discreteIndexes = new[] { microphone?.StreamIndex, game?.StreamIndex };
        if (!hasUsableAudio && mixed is not null && !discreteIndexes.Contains(mixed.StreamIndex))
        {
            await ReportAsync("mixed-audio-fallback", 0.58, "Separate tracks appear silent; measuring the combined OBS track instead.");
            var levels = await ExtractAudioLevelsAsync(source.AbsolutePath, mixed.StreamIndex, mode, analysisStart, analysisLimit, cancellationToken);
            features.AddRange(CreateGameAudioFeatures(levels.Select(level => (level.Position, level.RmsDb)).ToArray(), SampleDuration(mode)));
        }

        await ReportAsync("visual", 0.70, "Sampling scene changes without uploading frames.");
        var scenes = await ExtractSceneSamplesAsync(source.AbsolutePath, mode, analysisStart, analysisLimit, cancellationToken);
        features.AddRange(scenes.Select(scene => new FeatureEvent(
            FeatureKind.SceneChange,
            scene.Position,
            scene.Position + TimeSpan.FromSeconds(1),
            Math.Clamp(scene.Score, 0.35, 1),
            $"scene change ({scene.Score:P0})")));

        await ReportAsync("ranking", 0.90, "Ranking explainable highlight candidates.");
        var start = analysisStart ?? TimeSpan.Zero;
        var sourceDuration = analysisLimit is { } limit && start + limit < source.Duration ? start + limit : source.Duration;
        var ranked = HighlightScorer.CreateCandidates(new AnalysisInput(sourceDuration, mode, features))
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();
        var analyzedDuration = analysisLimit is { } boundedDuration ? boundedDuration : source.Duration;
        var targetDuration = TimeSpan.FromMinutes(Math.Clamp(analyzedDuration.TotalMinutes * 0.08, 1, 12));
        var draft = HighlightScorer.BuildDraft(ranked, targetDuration);
        var result = new LocalAnalysisResult(jobId, source.Id, mode, features.OrderBy(feature => feature.Start).ToArray(), ranked, draft, DateTimeOffset.UtcNow);
        await new AnalysisResultStore(paths).SaveAsync(result, cancellationToken);
        await ReportAsync("complete", 1, $"Found {ranked.Length} candidates and selected {draft.Clips.Count} for the draft.");
        await HighlightForgeLog.InfoAsync($"Local analysis completed for '{source.AbsolutePath}' with {features.Count} features and {ranked.Length} candidates.", cancellationToken);
        return result;

        async Task ReportAsync(string stage, double value, string detail)
        {
            progress?.Report(new AnalysisProgress(stage, value, detail));
            await checkpoints.SaveAsync(new AnalysisJobCheckpoint(jobId, stage, value, DateTimeOffset.UtcNow, detail), cancellationToken);
        }
    }

    public static IReadOnlyList<(TimeSpan Position, double RmsDb)> ParseAudioLevels(string output)
    {
        var result = new List<(TimeSpan Position, double RmsDb)>();
        TimeSpan? position = null;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var frame = FrameTimeRegex().Match(line);
            if (frame.Success && double.TryParse(frame.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                position = TimeSpan.FromSeconds(seconds);
                continue;
            }
            var level = RmsLevelRegex().Match(line);
            if (position is { } timestamp && level.Success && double.TryParse(level.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rms))
            {
                result.Add((timestamp, rms));
                position = null;
            }
        }
        return result;
    }

    public static IReadOnlyList<(TimeSpan Position, double Score)> ParseSceneSamples(string output)
    {
        var result = new List<(TimeSpan Position, double Score)>();
        TimeSpan? position = null;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var frame = FrameTimeRegex().Match(line);
            if (frame.Success && double.TryParse(frame.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                position = TimeSpan.FromSeconds(seconds);
                continue;
            }
            var score = SceneScoreRegex().Match(line);
            if (position is { } timestamp && score.Success && double.TryParse(score.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                result.Add((timestamp, value));
                position = null;
            }
        }
        return result;
    }

    public static IReadOnlyList<FeatureEvent> CreateMicrophoneFeatures(IReadOnlyList<(TimeSpan Position, double RmsDb)> levels, TimeSpan sampleDuration)
    {
        var usable = levels.Where(level => double.IsFinite(level.RmsDb) && level.RmsDb > -80).ToArray();
        if (usable.Length == 0) return [];
        var speechThreshold = Math.Max(-60, Percentile(usable.Select(level => level.RmsDb), 0.35));
        var excitementThreshold = Math.Max(-50, Percentile(usable.Select(level => level.RmsDb), 0.90));
        var speech = BuildRuns(usable.Where(level => level.RmsDb >= speechThreshold), sampleDuration, FeatureKind.Speech, speechThreshold, "detected commentary");
        var excitement = BuildRuns(usable.Where(level => level.RmsDb >= excitementThreshold), sampleDuration, FeatureKind.VocalExcitement, excitementThreshold, "strong vocal reaction");
        return speech.Concat(excitement).ToArray();
    }

    public static IReadOnlyList<FeatureEvent> CreateGameAudioFeatures(IReadOnlyList<(TimeSpan Position, double RmsDb)> levels, TimeSpan sampleDuration)
    {
        var usable = levels.Where(level => double.IsFinite(level.RmsDb) && level.RmsDb > -80).ToArray();
        if (usable.Length == 0) return [];
        var threshold = Math.Max(-60, Percentile(usable.Select(level => level.RmsDb), 0.90));
        return BuildRuns(usable.Where(level => level.RmsDb >= threshold), sampleDuration, FeatureKind.GameAudioPeak, threshold, "unusually strong game audio");
    }

    private static async Task<IReadOnlyList<AudioLevel>> ExtractAudioLevelsAsync(string sourcePath, int streamIndex, AnalysisMode mode, TimeSpan? start, TimeSpan? limit, CancellationToken cancellationToken)
    {
        var seconds = SampleDuration(mode).TotalSeconds;
        var samples = (int)Math.Round(8000 * seconds);
        var arguments = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error" };
        AddInputWindow(arguments, start, limit);
        arguments.AddRange(["-i", sourcePath, "-map", $"0:{streamIndex}", "-af", $"aresample=8000,asetnsamples=n={samples}:p=1,astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.RMS_level:file=-", "-f", "null", "NUL"]);
        var output = await RunFfmpegAsync(arguments, cancellationToken);
        var offset = start ?? TimeSpan.Zero;
        return ParseAudioLevels(output).Select(level => new AudioLevel(level.Position + offset, level.RmsDb)).ToArray();
    }

    private static async Task<IReadOnlyList<SceneSample>> ExtractSceneSamplesAsync(string sourcePath, AnalysisMode mode, TimeSpan? start, TimeSpan? limit, CancellationToken cancellationToken)
    {
        var interval = mode switch { AnalysisMode.Fast => 5, AnalysisMode.Deep => 1, _ => 2 };
        var threshold = mode switch { AnalysisMode.Fast => 0.30, AnalysisMode.Deep => 0.18, _ => 0.22 };
        var arguments = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error" };
        AddInputWindow(arguments, start, limit);
        arguments.AddRange(["-i", sourcePath, "-map", "0:v:0", "-vf", $"fps=1/{interval},scale=320:-2,select='gt(scene,{threshold.ToString(CultureInfo.InvariantCulture)})',metadata=print:file=-", "-an", "-f", "null", "NUL"]);
        var output = await RunFfmpegAsync(arguments, cancellationToken);
        var offset = start ?? TimeSpan.Zero;
        return ParseSceneSamples(output).Select(scene => new SceneSample(scene.Position + offset, scene.Score)).ToArray();
    }

    private static async Task<string> RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegRuntime.ResolveFfmpegPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start FFmpeg analysis.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"Local media analysis failed: {error.Trim()}");
        return output;
    }

    private static List<FeatureEvent> BuildRuns(IEnumerable<(TimeSpan Position, double RmsDb)> samples, TimeSpan sampleDuration, FeatureKind kind, double threshold, string detail)
    {
        var ordered = samples.OrderBy(sample => sample.Position).ToArray();
        if (ordered.Length == 0) return [];
        var result = new List<FeatureEvent>();
        var start = ordered[0].Position;
        var end = start + sampleDuration;
        var maximum = ordered[0].RmsDb;
        foreach (var sample in ordered.Skip(1))
        {
            if (sample.Position <= end + sampleDuration)
            {
                end = sample.Position + sampleDuration;
                maximum = Math.Max(maximum, sample.RmsDb);
                continue;
            }
            AddRun(start, end, maximum);
            start = sample.Position;
            end = sample.Position + sampleDuration;
            maximum = sample.RmsDb;
        }
        AddRun(start, end, maximum);
        return result;

        void AddRun(TimeSpan runStart, TimeSpan runEnd, double peak)
        {
            var confidence = Math.Clamp(0.45 + ((peak - threshold) / 20), 0.45, 0.98);
            result.Add(new FeatureEvent(kind, runStart, runEnd, confidence, detail));
        }
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0) return double.NegativeInfinity;
        var index = (int)Math.Round((ordered.Length - 1) * percentile, MidpointRounding.AwayFromZero);
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static TimeSpan SampleDuration(AnalysisMode mode) => mode switch
    {
        AnalysisMode.Fast => TimeSpan.FromSeconds(1),
        AnalysisMode.Deep => TimeSpan.FromSeconds(0.25),
        _ => TimeSpan.FromSeconds(0.5)
    };

    private static void AddInputWindow(List<string> arguments, TimeSpan? start, TimeSpan? limit)
    {
        if (start is { } offset && offset > TimeSpan.Zero)
        {
            arguments.Add("-ss");
            arguments.Add(offset.TotalSeconds.ToString(CultureInfo.InvariantCulture));
        }
        if (limit is { } duration)
        {
            arguments.Add("-t");
            arguments.Add(duration.TotalSeconds.ToString(CultureInfo.InvariantCulture));
        }
    }

    [GeneratedRegex(@"pts_time:([0-9]+(?:\.[0-9]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex FrameTimeRegex();

    [GeneratedRegex(@"lavfi\.astats\.Overall\.RMS_level=(-?[0-9]+(?:\.[0-9]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex RmsLevelRegex();

    [GeneratedRegex(@"lavfi\.scene_score=([0-9]+(?:\.[0-9]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex SceneScoreRegex();
}
