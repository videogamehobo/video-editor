using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Captions;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Models;
using HighlightForge.Core.Persistence;
using HighlightForge.Media.Runtime;

namespace HighlightForge.Media.Analysis;

public static partial class LocalFeatureAnalyzer
{
    private sealed record AudioLevel(TimeSpan Position, double RmsDb);
    private sealed record SceneSample(TimeSpan Position, double Score);
    private sealed record VisualValue(TimeSpan Position, double Value);

    public static async Task<LocalAnalysisResult> AnalyzeAsync(
        ProjectPaths paths,
        MediaSource source,
        AnalysisMode mode,
        IProgress<AnalysisProgress>? progress = null,
        TimeSpan? analysisStart = null,
        TimeSpan? analysisLimit = null,
        Guid? jobId = null,
        AnalysisJobCheckpoint? resumeFrom = null,
        IReadOnlyList<CaptionCue>? transcript = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(source);
        if (!File.Exists(source.AbsolutePath)) throw new FileNotFoundException("The source recording is no longer available.", source.AbsolutePath);

        var checkpoints = new AnalysisJobStore(paths);
        var checkpoint = resumeFrom;
        if (checkpoint is null && jobId is { } requestedJobId) checkpoint = await checkpoints.LoadAsync(requestedJobId, cancellationToken);
        if (checkpoint is not null && (checkpoint.SourceId != source.Id || checkpoint.Mode != mode))
        {
            throw new InvalidOperationException("The saved analysis checkpoint belongs to a different source or analysis mode.");
        }
        var activeJobId = checkpoint?.JobId ?? jobId ?? Guid.NewGuid();
        if (checkpoint?.Status == AnalysisJobStatus.Completed)
        {
            var completed = await new AnalysisResultStore(paths).LoadAsync(source.Id, cancellationToken);
            if (completed?.JobId == activeJobId) return completed;
        }
        var features = checkpoint?.Features?.ToList() ?? [];
        var completedStage = checkpoint?.Stage ?? string.Empty;
        var lastCheckpoint = checkpoint ?? new AnalysisJobCheckpoint(
            activeJobId,
            "starting",
            0,
            DateTimeOffset.UtcNow,
            "Preparing local media analysis.",
            source.Id,
            mode,
            AnalysisJobStatus.Pending,
            []);
        await ReportAsync("starting", Math.Max(0.02, checkpoint?.Progress ?? 0.02), checkpoint is null
            ? "Preparing local media analysis."
            : $"Resuming local analysis after {checkpoint.Stage}.");

        var microphone = source.AudioTracks.FirstOrDefault(track => track.Role == AudioTrackRole.Microphone);
        var game = source.AudioTracks.FirstOrDefault(track => track.Role == AudioTrackRole.Game);
        var mixed = source.AudioTracks.FirstOrDefault(track => track.Role == AudioTrackRole.Mixed)
            ?? (source.AudioTracks.Count > 0 ? source.AudioTracks[0] : null);

        try
        {
            if (!StageCompleted(completedStage, "transcript"))
            {
                await ReportAsync("transcript", 0.05, "Adding local transcript and speech-context signals.");
                features.AddRange(CreateTranscriptFeatures(transcript ?? []));
                await CompleteStageAsync("transcript", 0.10, "Transcript context checkpoint saved.");
            }

            if (!StageCompleted(completedStage, "microphone"))
            {
                await ReportAsync("microphone", 0.12, "Measuring commentary activity and vocal excitement.");
                if (microphone is not null)
                {
                    var levels = await ExtractAudioLevelsAsync(source.AbsolutePath, microphone.StreamIndex, mode, analysisStart, analysisLimit, cancellationToken);
                    features.AddRange(CreateMicrophoneFeatures(levels.Select(level => (level.Position, level.RmsDb)).ToArray(), SampleDuration(mode)));
                }
                await CompleteStageAsync("microphone", 0.32, "Commentary analysis checkpoint saved.");
            }

            if (!StageCompleted(completedStage, "game-audio"))
            {
                await ReportAsync("game-audio", 0.38, "Finding unusually strong game-audio moments.");
                if (game is not null || microphone is null)
                {
                    var track = game ?? mixed;
                    if (track is not null)
                    {
                        var levels = await ExtractAudioLevelsAsync(source.AbsolutePath, track.StreamIndex, mode, analysisStart, analysisLimit, cancellationToken);
                        features.AddRange(CreateGameAudioFeatures(levels.Select(level => (level.Position, level.RmsDb)).ToArray(), SampleDuration(mode)));
                    }
                }
                await CompleteStageAsync("game-audio", 0.55, "Game-audio analysis checkpoint saved.");
            }

            if (!StageCompleted(completedStage, "mixed-audio-fallback"))
            {
                var hasUsableAudio = features.Any(feature => feature.Kind is FeatureKind.Speech or FeatureKind.VocalExcitement or FeatureKind.GameAudioPeak);
                var discreteIndexes = new[] { microphone?.StreamIndex, game?.StreamIndex };
                if (!hasUsableAudio && mixed is not null && !discreteIndexes.Contains(mixed.StreamIndex))
                {
                    await ReportAsync("mixed-audio-fallback", 0.58, "Separate tracks appear silent; measuring the combined OBS track instead.");
                    var levels = await ExtractAudioLevelsAsync(source.AbsolutePath, mixed.StreamIndex, mode, analysisStart, analysisLimit, cancellationToken);
                    features.AddRange(CreateGameAudioFeatures(levels.Select(level => (level.Position, level.RmsDb)).ToArray(), SampleDuration(mode)));
                }
                await CompleteStageAsync("mixed-audio-fallback", 0.64, "Audio fallback checkpoint saved.");
            }

            if (!StageCompleted(completedStage, "sound-events"))
            {
                var soundTrack = game ?? mixed ?? microphone;
                var activeYamnet = await new ModelPackManager(WhisperModelCatalog.DefaultRootDirectory)
                    .GetActiveVersionAsync(YamnetModelCatalog.Pack.Manifest.Id, cancellationToken);
                if (soundTrack is not null && activeYamnet is not null)
                {
                    var modelDirectory = YamnetModelCatalog.InstalledDirectory(WhisperModelCatalog.DefaultRootDirectory);
                    features.AddRange(await YamnetSoundEventAnalyzer.AnalyzeAsync(
                        paths,
                        source,
                        soundTrack,
                        features,
                        modelDirectory,
                        mode,
                        progress,
                        cancellationToken));
                }
                await CompleteStageAsync(
                    "sound-events",
                    0.69,
                    activeYamnet is null ? "YAMNet is not installed; sound-event stage skipped." : "YAMNet sound-event checkpoint saved.");
            }

            if (!StageCompleted(completedStage, "visual"))
            {
                await ReportAsync("visual", 0.70, "Sampling scene changes, motion, and visual novelty without uploading frames.");
                var scenes = await ExtractSceneSamplesAsync(source.AbsolutePath, mode, analysisStart, analysisLimit, cancellationToken);
                features.AddRange(scenes.Select(scene => new FeatureEvent(
                    FeatureKind.SceneChange,
                    scene.Position,
                    scene.Position + TimeSpan.FromSeconds(1),
                    Math.Clamp(scene.Score, 0.35, 1),
                    $"scene change ({scene.Score:P0})")));
                var motion = await ExtractVisualValuesAsync(source.AbsolutePath, mode, "motion", analysisStart, analysisLimit, cancellationToken);
                features.AddRange(CreateRelativeVisualFeatures(motion.Select(sample => (sample.Position, sample.Value)).ToArray(), FeatureKind.Motion, "high on-screen motion"));
                var entropy = await ExtractVisualValuesAsync(source.AbsolutePath, mode, "entropy", analysisStart, analysisLimit, cancellationToken);
                features.AddRange(CreateNoveltyFeatures(entropy.Select(sample => (sample.Position, sample.Value)).ToArray()));
                var activeFlorence = mode == AnalysisMode.Fast
                    ? null
                    : await new ModelPackManager(WhisperModelCatalog.DefaultRootDirectory)
                        .GetActiveVersionAsync(FlorenceModelCatalog.BaseFtVisualEncoder.Manifest.Id, cancellationToken);
                if (activeFlorence is not null)
                {
                    features.AddRange(await FlorenceVisualContextAnalyzer.AnalyzeAsync(
                        source,
                        mode,
                        FlorenceModelCatalog.InstalledDirectory(WhisperModelCatalog.DefaultRootDirectory),
                        analysisStart,
                        analysisLimit,
                        progress,
                        cancellationToken));
                }
                var activeOcr = await new ModelPackManager(WhisperModelCatalog.DefaultRootDirectory)
                    .GetActiveVersionAsync(OcrModelCatalog.English.Manifest.Id, cancellationToken);
                if (activeOcr is not null)
                {
                    features.AddRange(await OcrFeatureAnalyzer.AnalyzeAsync(
                        paths,
                        source,
                        mode,
                        OcrModelCatalog.InstalledDirectory(WhisperModelCatalog.DefaultRootDirectory),
                        analysisStart,
                        analysisLimit,
                        progress,
                        cancellationToken));
                }
                await CompleteStageAsync("visual", 0.86, "Visual analysis checkpoint saved.");
            }

            await ReportAsync("ranking", 0.90, "Ranking explainable highlight candidates.");
            var start = analysisStart ?? TimeSpan.Zero;
            var sourceDuration = analysisLimit is { } limit && start + limit < source.Duration ? start + limit : source.Duration;
            var ranked = HighlightScorer.CreateCandidates(new AnalysisInput(sourceDuration, mode, features))
                .OrderByDescending(candidate => candidate.Score)
                .ToArray();
            var analyzedDuration = analysisLimit is { } boundedDuration ? boundedDuration : source.Duration;
            var targetDuration = TimeSpan.FromMinutes(Math.Clamp(analyzedDuration.TotalMinutes * 0.08, 1, 12));
            var draft = HighlightScorer.BuildDraft(ranked, targetDuration);
            IReadOnlyList<HighlightForge.Core.Voiceover.NarrativeSuggestion>? narrativeSuggestions = null;
            if (mode != AnalysisMode.Fast)
            {
                var activePhi = await new ModelPackManager(WhisperModelCatalog.DefaultRootDirectory)
                    .GetActiveVersionAsync(PhiModelCatalog.MiniInstructCpuInt4.Manifest.Id, cancellationToken);
                if (activePhi is not null)
                {
                    await ReportAsync("narrative", 0.95, "Creating local Phi-4 voice-over talking points.");
                    try
                    {
                        narrativeSuggestions = await PhiNarrativeService.GenerateAsync(
                            ranked,
                            PhiModelCatalog.InstalledDirectory(WhisperModelCatalog.DefaultRootDirectory),
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        await HighlightForgeLog.ErrorAsync("Local Phi-4 narrative generation failed; deterministic talking points remain available.", exception, cancellationToken);
                    }
                }
            }
            var result = new LocalAnalysisResult(
                activeJobId,
                source.Id,
                mode,
                features.OrderBy(feature => feature.Start).ToArray(),
                ranked,
                draft,
                DateTimeOffset.UtcNow,
                narrativeSuggestions);
            await new AnalysisResultStore(paths).SaveAsync(result, cancellationToken);
            await CompleteStageAsync("complete", 1, $"Found {ranked.Length} candidates and selected {draft.Clips.Count} for the draft.", AnalysisJobStatus.Completed);
            await ReportAsync("complete", 1, lastCheckpoint.Detail!);
            await HighlightForgeLog.InfoAsync($"Local analysis completed for '{source.AbsolutePath}' with {features.Count} features and {ranked.Length} candidates.", cancellationToken);
            return result;
        }
        catch (OperationCanceledException)
        {
            lastCheckpoint = lastCheckpoint with
            {
                Status = AnalysisJobStatus.Paused,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Detail = $"Paused after {lastCheckpoint.Stage}; resume will continue from this checkpoint.",
                Features = lastCheckpoint.Features
            };
            await checkpoints.SaveAsync(lastCheckpoint, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            lastCheckpoint = lastCheckpoint with
            {
                Status = AnalysisJobStatus.Failed,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Detail = exception.Message,
                Features = lastCheckpoint.Features
            };
            await checkpoints.SaveAsync(lastCheckpoint, CancellationToken.None);
            throw;
        }

        Task ReportAsync(string stage, double value, string detail)
        {
            progress?.Report(new AnalysisProgress(stage, value, detail));
            return Task.CompletedTask;
        }

        async Task CompleteStageAsync(string stage, double value, string detail, AnalysisJobStatus status = AnalysisJobStatus.Running)
        {
            completedStage = stage;
            lastCheckpoint = new AnalysisJobCheckpoint(
                activeJobId,
                stage,
                value,
                DateTimeOffset.UtcNow,
                detail,
                source.Id,
                mode,
                status,
                features.ToArray());
            await checkpoints.SaveAsync(lastCheckpoint, cancellationToken);
        }
    }

    public static bool StageCompleted(string completedStage, string requestedStage)
    {
        var order = new[] { "starting", "transcript", "microphone", "game-audio", "mixed-audio-fallback", "sound-events", "visual", "ranking", "complete" };
        var completedIndex = Array.IndexOf(order, completedStage);
        var requestedIndex = Array.IndexOf(order, requestedStage);
        return completedIndex >= 0 && requestedIndex >= 0 && completedIndex >= requestedIndex;
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

    public static IReadOnlyList<FeatureEvent> CreateTranscriptFeatures(IReadOnlyList<CaptionCue> cues)
    {
        var result = new List<FeatureEvent>();
        foreach (var cue in cues.Where(cue => cue.End > cue.Start && !string.IsNullOrWhiteSpace(cue.Text)))
        {
            var text = string.Join(' ', cue.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var detail = text.Length <= 120 ? text : $"{text[..117]}...";
            result.Add(new FeatureEvent(FeatureKind.Speech, cue.Start, cue.End, 0.72, $"commentary: {detail}"));
            if (LaughterTextRegex().IsMatch(text))
            {
                result.Add(new FeatureEvent(FeatureKind.Laughter, cue.Start, cue.End, 0.82, $"laughter in commentary: {detail}"));
            }
            if (text.Contains('!') || ExcitementTextRegex().IsMatch(text))
            {
                result.Add(new FeatureEvent(FeatureKind.VocalExcitement, cue.Start, cue.End, 0.78, $"excited commentary: {detail}"));
            }
        }
        return result;
    }

    public static IReadOnlyList<FeatureEvent> CreateRelativeVisualFeatures(
        IReadOnlyList<(TimeSpan Position, double Value)> samples,
        FeatureKind kind,
        string detail)
    {
        var usable = samples.Where(sample => double.IsFinite(sample.Value)).ToArray();
        if (usable.Length < 3) return [];
        var threshold = Percentile(usable.Select(sample => sample.Value), 0.90);
        return usable
            .Where(sample => sample.Value >= threshold && sample.Value > 0)
            .Select(sample => new FeatureEvent(
                kind,
                sample.Position,
                sample.Position + TimeSpan.FromSeconds(1),
                Math.Clamp(0.45 + ((sample.Value - threshold) / Math.Max(1, Math.Abs(threshold))), 0.45, 0.95),
                $"{detail} ({sample.Value:0.00})"))
            .ToArray();
    }

    public static IReadOnlyList<FeatureEvent> CreateNoveltyFeatures(IReadOnlyList<(TimeSpan Position, double Value)> samples)
    {
        var changes = samples
            .OrderBy(sample => sample.Position)
            .Zip(samples.OrderBy(sample => sample.Position).Skip(1), (left, right) =>
                (right.Position, Value: Math.Abs(right.Value - left.Value)))
            .ToArray();
        return CreateRelativeVisualFeatures(changes, FeatureKind.VisualNovelty, "unusual visual change");
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

    private static async Task<IReadOnlyList<VisualValue>> ExtractVisualValuesAsync(
        string sourcePath,
        AnalysisMode mode,
        string kind,
        TimeSpan? start,
        TimeSpan? limit,
        CancellationToken cancellationToken)
    {
        var framesPerSecond = mode switch { AnalysisMode.Fast => "1/3", AnalysisMode.Deep => "2", _ => "1" };
        var filter = kind == "motion"
            ? $"fps={framesPerSecond},scale=320:-2,tblend=all_mode=difference,signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-"
            : $"fps={framesPerSecond},scale=320:-2,entropy,metadata=print:key=lavfi.entropy.normalized_entropy.normal.Y:file=-";
        var arguments = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error" };
        AddInputWindow(arguments, start, limit);
        arguments.AddRange(["-i", sourcePath, "-map", "0:v:0", "-vf", filter, "-an", "-f", "null", "NUL"]);
        var output = await RunFfmpegAsync(arguments, cancellationToken);
        var key = kind == "motion" ? "lavfi.signalstats.YAVG=" : "lavfi.entropy.normalized_entropy.normal.Y=";
        var offset = start ?? TimeSpan.Zero;
        return ParseMetadataValues(output, key).Select(sample => new VisualValue(sample.Position + offset, sample.Value)).ToArray();
    }

    public static IReadOnlyList<(TimeSpan Position, double Value)> ParseMetadataValues(string output, string key)
    {
        var result = new List<(TimeSpan Position, double Value)>();
        TimeSpan? position = null;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var frame = FrameTimeRegex().Match(line);
            if (frame.Success && double.TryParse(frame.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                position = TimeSpan.FromSeconds(seconds);
                continue;
            }
            if (position is null || !line.StartsWith(key, StringComparison.Ordinal)) continue;
            if (double.TryParse(line.AsSpan(key.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                result.Add((position.Value, value));
                position = null;
            }
        }
        return result;
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
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException($"Local media analysis failed: {error.Trim()}");
            return output;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
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

    [GeneratedRegex(@"(?i)(?:\b(?:lol|lmao|rofl|laugh(?:ing|ed)?|giggl(?:e|ing))\b|(?:ha){2,}|(?:he){2,})", RegexOptions.CultureInvariant)]
    private static partial Regex LaughterTextRegex();

    [GeneratedRegex(@"\b(?:NO WAY|LET'S GO|LETS GO|WHAT|YES|WOW|OH MY GOD|OMG)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ExcitementTextRegex();
}
