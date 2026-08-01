using System.Diagnostics;
using System.Globalization;
using System.Text;
using HighlightForge.Core.Audio;
using HighlightForge.Core.Captions;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Persistence;
using HighlightForge.Core.Timeline;
using HighlightForge.Core.Voiceover;
using HighlightForge.Media.Audio;
using HighlightForge.Media.Runtime;

namespace HighlightForge.Media.Render;

public enum VerticalComposition
{
    FullFrameBlurred,
    FocusedCrop
}

public sealed record ProjectRenderOptions(
    RenderKind Kind,
    bool BurnInCaptions = true,
    bool WriteSrt = true,
    bool WriteVtt = true,
    VerticalComposition VerticalComposition = VerticalComposition.FullFrameBlurred,
    double FocusX = 0.5,
    double FocusConfidence = 0,
    bool PreferNvidia = true);

public sealed record ProjectRenderRequest(
    ProjectPaths Paths,
    ProjectDocument Project,
    CreatorWorkflowState CreatorState,
    string OutputPath,
    ProjectRenderOptions Options,
    AudioMixSettings? AudioSettings = null,
    IReadOnlyDictionary<Guid, CreatorWorkflowState>? CreatorStates = null);

public sealed record ProjectRenderProgress(double Fraction, string Stage, string Detail);
public sealed record ProjectRenderResult(
    string OutputPath,
    string? SrtPath,
    string? VttPath,
    string VideoEncoder,
    TimeSpan Duration,
    AudioLoudnessMeasurement OutputLoudness,
    OutputValidationReport Validation);

public static class ProjectRenderPlan
{
    public static IReadOnlyList<string> BuildIntermediateArguments(
        ProjectRenderRequest request,
        string intermediatePath,
        string? assPath,
        string videoEncoder)
    {
        Validate(request);
        var clips = request.Project.Timeline.OrderBy(clip => clip.TimelineIn).ToArray();
        var arguments = new List<string> { "-hide_banner", "-y" };
        foreach (var clip in clips)
        {
            var source = request.Project.Sources.Single(item => item.Id == clip.SourceId);
            arguments.AddRange([
                "-ss", Seconds(clip.SourceIn),
                "-t", Seconds(clip.SourceOut - clip.SourceIn),
                "-i", source.AbsolutePath
            ]);
        }

        var selectedTakes = SelectedTakes(request).ToArray();
        foreach (var take in selectedTakes) arguments.AddRange(["-i", take.AbsolutePath]);

        var filters = new List<string>();
        for (var index = 0; index < clips.Length; index++)
        {
            var clip = clips[index];
            var source = request.Project.Sources.Single(item => item.Id == clip.SourceId);
            filters.Add(BuildClipVideoFilter(source, clip, index));
            filters.Add(BuildClipAudioFilter(source, clip, index, AudioSettingsForSource(request, source.Id)));
        }
        var concatInputs = string.Concat(Enumerable.Range(0, clips.Length).Select(index => $"[v{index}][a{index}]"));
        filters.Add($"{concatInputs}concat=n={clips.Length}:v=1:a=1[video][program]");

        var totalDuration = TimelineDuration(clips);
        if (selectedTakes.Length == 0)
        {
            filters.Add("[program]anull[premaster]");
        }
        else
        {
            for (var index = 0; index < selectedTakes.Length; index++)
            {
                var inputIndex = clips.Length + index;
                var delay = Math.Max(0, (long)selectedTakes[index].Start.TotalMilliseconds);
                filters.Add($"[{inputIndex}:a:0]adelay={delay}:all=1,apad,atrim=duration={Seconds(totalDuration)}[vo{index}]");
            }
            var voiceInputs = string.Concat(Enumerable.Range(0, selectedTakes.Length).Select(index => $"[vo{index}]"));
            filters.Add($"[program]{voiceInputs}amix=inputs={selectedTakes.Length + 1}:duration=first:normalize=0[premaster]");
        }
        filters.Add("[premaster]acompressor=threshold=0.016:ratio=3:attack=20:release=250,alimiter=limit=0.891:level=false[audioout]");

        filters.Add(BuildVideoComposition(request.Options, assPath));
        arguments.AddRange([
            "-filter_complex", string.Join(';', filters),
            "-map", "[videoout]",
            "-map", "[audioout]",
            "-c:v", videoEncoder
        ]);
        arguments.AddRange(VideoEncoderArguments(videoEncoder, request.Options.Kind));
        arguments.AddRange(["-pix_fmt", "yuv420p", "-c:a", "pcm_s24le", "-progress", "pipe:1", "-nostats", intermediatePath]);
        return arguments;
    }

    public static IReadOnlyList<string> BuildFinalArguments(
        string intermediatePath,
        string outputPath,
        AudioMixSettings settings,
        AudioLoudnessMeasurement measurement)
    {
        MediaPathSafety.RequireSeparateOutput(intermediatePath, outputPath, "Final export");
        var audioFilter = measurement.IntegratedLufs <= -70 || measurement.TruePeakDbtp <= -70
            ? "anull"
            : AudioMixPlanner.BuildMeasuredLoudnessFilter(settings, measurement);
        return [
            "-hide_banner", "-y", "-i", intermediatePath,
            "-map", "0:v:0", "-map", "0:a:0",
            "-c:v", "copy",
            "-af", audioFilter,
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000",
            "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", outputPath
        ];
    }

    public static void Validate(ProjectRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Project.Timeline.Count == 0) throw new InvalidOperationException("The timeline has no clips to export.");
        var timelineValidation = TimelineEditor.Validate(request.Project.Timeline, request.Project.Sources);
        if (!timelineValidation.IsValid) throw new InvalidOperationException($"The timeline is invalid: {string.Join(" ", timelineValidation.Problems)}");
        if (!string.Equals(Path.GetExtension(request.OutputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("HighlightForge exports H.264/AAC video to an .mp4 file.");
        }
        foreach (var source in request.Project.Sources)
        {
            MediaPathSafety.RequireSeparateOutput(source.AbsolutePath, request.OutputPath, "Export");
            if (!source.AudioRolesConfirmed) throw new InvalidOperationException($"Confirm audio roles for '{Path.GetFileName(source.AbsolutePath)}' before export.");
        }
        if (request.Options.VerticalComposition == VerticalComposition.FocusedCrop &&
            request.Options.FocusConfidence < 0.8 && request.Options.FocusX == 0.5)
        {
            throw new InvalidOperationException("Automatic focused crop requires at least 80% confidence. Use the safe blurred layout or set the crop position manually.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Options.FocusX, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Options.FocusX, 1);
        EffectiveAudioSettings(request).Validated();
        (request.CreatorState.CaptionStyle ?? new CaptionStyleSettings()).Validated();
        foreach (var pair in AllCreatorStates(request))
        {
            if (pair.Key != pair.Value.SourceId) throw new InvalidOperationException("Creator workflow state is assigned to the wrong source.");
            if (request.Project.Sources.All(source => source.Id != pair.Key)) throw new InvalidOperationException("Creator workflow state refers to media outside this project.");
            (pair.Value.AudioSettings ?? new AudioMixSettings()).Validated();
        }
    }

    public static TimeSpan TimelineDuration(IReadOnlyList<TimelineClip> clips) =>
        clips.Aggregate(TimeSpan.Zero, (duration, clip) => duration + clip.SourceOut - clip.SourceIn);

    private static string BuildClipAudioFilter(MediaSource source, TimelineClip clip, int inputIndex, AudioMixSettings settings)
    {
        var duration = clip.SourceOut - clip.SourceIn;
        var microphone = source.AudioTracks.SingleOrDefault(track => track.Role == AudioTrackRole.Microphone);
        var game = source.AudioTracks.SingleOrDefault(track => track.Role == AudioTrackRole.Game);
        string sourceFilter;
        if (microphone is not null && game is not null)
        {
            sourceFilter = $"[{inputIndex}:{microphone.StreamIndex}]aresample=48000,highpass=f=80,afftdn=nf=-25,volume={settings.MicrophoneGainDb.ToString(CultureInfo.InvariantCulture)}dB[mic{inputIndex}];" +
                $"[{inputIndex}:{game.StreamIndex}]aresample=48000,volume={settings.GameGainDb.ToString(CultureInfo.InvariantCulture)}dB[game{inputIndex}];" +
                $"[game{inputIndex}][mic{inputIndex}]sidechaincompress=threshold=0.035:ratio={settings.DuckingRatio.ToString(CultureInfo.InvariantCulture)}:attack={settings.DuckingAttackMs}:release={settings.DuckingReleaseMs}[ducked{inputIndex}];" +
                $"[ducked{inputIndex}][mic{inputIndex}]amix=inputs=2:duration=longest:normalize=0,aresample=async=1:first_pts=0,asetpts=PTS-STARTPTS[clipaudio{inputIndex}]";
        }
        else
        {
            var mixed = source.AudioTracks.SingleOrDefault(track => track.Role == AudioTrackRole.Mixed);
            sourceFilter = mixed is not null
                ? $"[{inputIndex}:{mixed.StreamIndex}]aresample=async=1:first_pts=0,asetpts=PTS-STARTPTS[clipaudio{inputIndex}]"
                : $"anullsrc=r=48000:cl=stereo,atrim=duration={Seconds(duration)},asetpts=PTS-STARTPTS[clipaudio{inputIndex}]";
        }
        var envelope = $"[clipaudio{inputIndex}]volume={clip.GainDb.ToString("0.###", CultureInfo.InvariantCulture)}dB";
        if (clip.FadeIn > TimeSpan.Zero) envelope += $",afade=t=in:st=0:d={Seconds(clip.FadeIn)}";
        if (clip.FadeOut > TimeSpan.Zero) envelope += $",afade=t=out:st={Seconds(duration - clip.FadeOut)}:d={Seconds(clip.FadeOut)}";
        return $"{sourceFilter};{envelope}[a{inputIndex}]";
    }

    private static string BuildClipVideoFilter(MediaSource source, TimelineClip clip, int inputIndex)
    {
        var duration = clip.SourceOut - clip.SourceIn;
        var filter = $"[{inputIndex}:v:0]setpts=PTS-STARTPTS";
        if (clip.PunchZoom)
        {
            var x = clip.ReframeX.ToString("0.###", CultureInfo.InvariantCulture);
            var y = clip.ReframeY.ToString("0.###", CultureInfo.InvariantCulture);
            filter += $",zoompan=z='min(max(zoom,1)+0.002,1.08)':x='(iw-iw/zoom)*{x}':y='(ih-ih/zoom)*{y}':d=1:s={source.Width}x{source.Height}:fps={source.FramesPerSecond.ToString("0.###", CultureInfo.InvariantCulture)}";
        }
        else if (clip.CropScale > 1)
        {
            var scale = clip.CropScale.ToString("0.###", CultureInfo.InvariantCulture);
            var x = clip.ReframeX.ToString("0.###", CultureInfo.InvariantCulture);
            var y = clip.ReframeY.ToString("0.###", CultureInfo.InvariantCulture);
            filter += $",scale=w='ceil(iw*{scale}/2)*2':h='ceil(ih*{scale}/2)*2',crop={source.Width}:{source.Height}:x='(iw-ow)*{x}':y='(ih-oh)*{y}'";
        }
        if (clip.FadeIn > TimeSpan.Zero) filter += $",fade=t=in:st=0:d={Seconds(clip.FadeIn)}";
        if (clip.FadeOut > TimeSpan.Zero) filter += $",fade=t=out:st={Seconds(duration - clip.FadeOut)}:d={Seconds(clip.FadeOut)}";
        return $"{filter},format=yuv420p[v{inputIndex}]";
    }

    private static string BuildVideoComposition(ProjectRenderOptions options, string? assPath)
    {
        var composition = options.Kind switch
        {
            RenderKind.LongForm => "[video]null[composed]",
            _ when options.VerticalComposition == VerticalComposition.FocusedCrop =>
                $"[video]crop=w='min(iw,ih*9/16)':h=ih:x='(iw-ow)*{options.FocusX.ToString(CultureInfo.InvariantCulture)}':y=0,scale=1080:1920[composed]",
            _ => "[video]split=2[background][foreground];[background]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,gblur=sigma=20:steps=2[blurred];[foreground]scale=1080:1920:force_original_aspect_ratio=decrease[gameplay];[blurred][gameplay]overlay=(W-w)/2:(H-h)/2[composed]"
        };
        return assPath is null
            ? $"{composition};[composed]format=yuv420p[videoout]"
            : $"{composition};[composed]ass=filename='{EscapeFilterPath(assPath)}',format=yuv420p[videoout]";
    }

    private static IEnumerable<string> VideoEncoderArguments(string videoEncoder, RenderKind kind) => videoEncoder switch
    {
        "h264_nvenc" => ["-preset", "p5", "-cq", "19", "-b:v", "0"],
        "libopenh264" => ["-b:v", kind == RenderKind.Vertical ? "12M" : "20M"],
        _ => ["-b:v", kind == RenderKind.Vertical ? "12M" : "20M"]
    };

    private static IEnumerable<VoiceoverTake> SelectedTakes(ProjectRenderRequest request)
    {
        foreach (var take in AllCreatorStates(request).Values
            .SelectMany(state => state.VoiceoverTakes)
            .Where(take => take.IsSelected && File.Exists(take.AbsolutePath)))
        {
            MediaPathSafety.RequireOutputWithinDirectory(request.Paths.TakesDirectory, take.AbsolutePath, "Voice-over take");
            yield return take;
        }
    }

    private static string EscapeFilterPath(string path) => Path.GetFullPath(path)
        .Replace("\\", "/", StringComparison.Ordinal)
        .Replace(":", "\\:", StringComparison.Ordinal)
        .Replace("'", "\\'", StringComparison.Ordinal);

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static AudioMixSettings EffectiveAudioSettings(ProjectRenderRequest request) =>
        request.AudioSettings ?? request.CreatorState.AudioSettings ?? new AudioMixSettings();

    private static AudioMixSettings AudioSettingsForSource(ProjectRenderRequest request, Guid sourceId)
    {
        if (request.AudioSettings is not null) return request.AudioSettings.Validated();
        return AllCreatorStates(request).TryGetValue(sourceId, out var state)
            ? (state.AudioSettings ?? new AudioMixSettings()).Validated()
            : EffectiveAudioSettings(request).Validated();
    }

    public static IReadOnlyDictionary<Guid, CreatorWorkflowState> AllCreatorStates(ProjectRenderRequest request)
    {
        var states = request.CreatorStates is null
            ? new Dictionary<Guid, CreatorWorkflowState>()
            : new Dictionary<Guid, CreatorWorkflowState>(request.CreatorStates);
        states[request.CreatorState.SourceId] = request.CreatorState;
        return states;
    }

    public static IReadOnlyList<CaptionCue> MapCaptions(ProjectRenderRequest request) => AllCreatorStates(request)
        .Values
        .SelectMany(state => CaptionTimelineMapper.MapToTimeline(state.Captions, request.Project.Timeline, state.SourceId))
        .OrderBy(cue => cue.Start)
        .ToArray();
}

public static class AssCaptionDocument
{
    public static string Create(IReadOnlyList<CaptionCue> cues, bool vertical, CaptionStyleSettings? settings = null)
    {
        var style = (settings ?? new CaptionStyleSettings()).Validated();
        var fontSize = vertical ? Math.Max(style.FontSize, 52) : style.FontSize;
        var marginVertical = vertical ? 240 : 64;
        var alignment = style.Placement switch { CaptionPlacement.Top => 8, CaptionPlacement.Middle => 5, _ => 2 };
        var primary = ToAssColor(style.PrimaryColor);
        var outline = ToAssColor(style.OutlineColor);
        var builder = new StringBuilder($$"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: {{(vertical ? 1080 : 1920)}}
            PlayResY: {{(vertical ? 1920 : 1080)}}
            WrapStyle: 2

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,{{style.FontName}},{{fontSize}},{{primary}},&H000000FF,{{outline}},&H80000000,-1,0,0,0,100,100,0,0,1,{{style.OutlineSize.ToString(CultureInfo.InvariantCulture)}},1,{{alignment}},80,80,{{marginVertical}},1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            """);
        builder.AppendLine();
        foreach (var cue in cues)
        {
            builder.Append("Dialogue: 0,")
                .Append(FormatTime(cue.Start)).Append(',')
                .Append(FormatTime(cue.End))
                .Append(",Default,,0,0,0,,")
                .AppendLine(EscapeText(cue.Text));
        }
        return builder.ToString();
    }

    private static string EscapeText(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("{", "\\{", StringComparison.Ordinal)
        .Replace("}", "\\}", StringComparison.Ordinal)
        .Replace(Environment.NewLine, "\\N", StringComparison.Ordinal)
        .Replace("\n", "\\N", StringComparison.Ordinal);

    private static string FormatTime(TimeSpan value) =>
        $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds / 10:00}";

    private static string ToAssColor(string color)
    {
        var value = color.AsSpan(1);
        return $"&H00{value[4..6]}{value[2..4]}{value[0..2]}";
    }
}

public static class FfmpegEncoderCapabilities
{
    private static readonly string[] CandidateEncoders = ["h264_nvenc", "h264_mf", "libopenh264"];

    public static async Task<IReadOnlyList<string>> AvailableH264EncodersAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegRuntime.ResolveFfmpegPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-encoders");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start FFmpeg capability discovery.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask + await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException("FFmpeg encoder discovery failed.");
        return CandidateEncoders.Where(encoder => output.Contains(encoder, StringComparison.Ordinal)).ToArray();
    }

    public static string Choose(IReadOnlyList<string> encoders, bool preferNvidia)
    {
        var candidates = Ranked(encoders, preferNvidia);
        if (candidates.Count > 0) return candidates[0];
        throw new InvalidOperationException("This FFmpeg build has no LGPL-compatible H.264 encoder (h264_mf, h264_nvenc, or libopenh264). Install the bundled HighlightForge FFmpeg build.");
    }

    public static IReadOnlyList<string> Ranked(IReadOnlyList<string> encoders, bool preferNvidia)
    {
        var preference = preferNvidia
            ? CandidateEncoders
            : ["h264_mf", "libopenh264"];
        return preference.Where(encoder => encoders.Contains(encoder, StringComparer.Ordinal)).ToArray();
    }
}

public static class ProjectRenderService
{
    public static async Task<ProjectRenderResult> RenderAsync(
        ProjectRenderRequest request,
        IProgress<ProjectRenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ProjectRenderPlan.Validate(request);
        request.Paths.EnsureDirectories();
        var jobDirectory = Path.Combine(request.Paths.RenderCacheDirectory, Guid.NewGuid().ToString("N"));
        MediaPathSafety.RequireOutputWithinDirectory(request.Paths.RenderCacheDirectory, jobDirectory, "Render cache");
        Directory.CreateDirectory(jobDirectory);
        var intermediatePath = Path.Combine(jobDirectory, "program.mkv");
        var assPath = request.Options.BurnInCaptions ? Path.Combine(jobDirectory, "captions.ass") : null;
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!;
        Directory.CreateDirectory(outputDirectory);
        var partialPath = Path.Combine(outputDirectory, $".{Path.GetFileNameWithoutExtension(request.OutputPath)}-{Guid.NewGuid():N}.partial.mp4");
        foreach (var source in request.Project.Sources) MediaPathSafety.RequireSeparateOutput(source.AbsolutePath, partialPath, "Export");

        string? srtPath = null;
        string? vttPath = null;
        var mappedCaptions = ProjectRenderPlan.MapCaptions(request);
        var duration = ProjectRenderPlan.TimelineDuration(request.Project.Timeline);
        try
        {
            if (assPath is not null)
            {
                await File.WriteAllTextAsync(
                    assPath,
                    AssCaptionDocument.Create(mappedCaptions, request.Options.Kind == RenderKind.Vertical, request.CreatorState.CaptionStyle),
                    cancellationToken);
            }
            var encoders = await FfmpegEncoderCapabilities.AvailableH264EncodersAsync(cancellationToken);
            var encoderCandidates = FfmpegEncoderCapabilities.Ranked(encoders, request.Options.PreferNvidia);
            if (encoderCandidates.Count == 0) FfmpegEncoderCapabilities.Choose(encoders, request.Options.PreferNvidia);
            string? encoder = null;
            InvalidOperationException? lastEncoderFailure = null;
            foreach (var candidate in encoderCandidates)
            {
                progress?.Report(new ProjectRenderProgress(0, "Video", $"Rendering edited timeline with {candidate}"));
                try
                {
                    await RunFfmpegAsync(ProjectRenderPlan.BuildIntermediateArguments(request, intermediatePath, assPath, candidate), duration, 0, 0.82, progress, cancellationToken);
                    encoder = candidate;
                    break;
                }
                catch (InvalidOperationException exception)
                {
                    lastEncoderFailure = exception;
                    if (File.Exists(intermediatePath)) File.Delete(intermediatePath);
                    await HighlightForgeLog.InfoAsync($"The {candidate} encoder was unavailable at runtime; trying the next local encoder.", cancellationToken);
                }
            }
            if (encoder is null) throw new InvalidOperationException("Every available local H.264 encoder failed.", lastEncoderFailure);

            progress?.Report(new ProjectRenderProgress(0.83, "Audio", "Measuring the edited program for two-pass loudness normalization"));
            var measurement = await AudioLoudnessAnalyzer.MeasureAsync(
                intermediatePath,
                new AudioTrack(1, "Edited program", 2, 48000, AudioTrackRole.Mixed),
                cancellationToken: cancellationToken);
            progress?.Report(new ProjectRenderProgress(0.86, "Mastering", "Normalizing to −14 LUFS and at most −1 dBTP"));
            await RunFfmpegAsync(
                ProjectRenderPlan.BuildFinalArguments(intermediatePath, partialPath, request.AudioSettings ?? request.CreatorState.AudioSettings ?? new AudioMixSettings(), measurement),
                duration, 0.86, 0.14, progress, cancellationToken);
            progress?.Report(new ProjectRenderProgress(0.99, "Verification", "Checking final LUFS and true peak"));
            var outputMeasurement = await AudioLoudnessAnalyzer.MeasureAsync(
                partialPath,
                new AudioTrack(1, "Final export", 2, 48000, AudioTrackRole.Mixed),
                cancellationToken: cancellationToken);
            var settings = request.AudioSettings ?? request.CreatorState.AudioSettings ?? new AudioMixSettings();
            var firstSource = request.Project.Sources.Single(source => source.Id == request.Project.Timeline[0].SourceId);
            var validation = await MediaOutputValidator.ValidateAsync(
                partialPath,
                request.Options.Kind,
                duration,
                outputMeasurement,
                settings,
                request.Options.Kind == RenderKind.LongForm ? firstSource.Width : null,
                request.Options.Kind == RenderKind.LongForm ? firstSource.Height : null,
                cancellationToken);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException($"Final output verification failed: {string.Join(" ", validation.Problems)}");
            }
            File.Move(partialPath, request.OutputPath, overwrite: true);

            if (request.Options.WriteSrt)
            {
                srtPath = Path.ChangeExtension(request.OutputPath, ".srt");
                await File.WriteAllTextAsync(srtPath, CaptionDocument.ToSrt(mappedCaptions), cancellationToken);
            }
            if (request.Options.WriteVtt)
            {
                vttPath = Path.ChangeExtension(request.OutputPath, ".vtt");
                await File.WriteAllTextAsync(vttPath, CaptionDocument.ToWebVtt(mappedCaptions), cancellationToken);
            }
            await HighlightForgeLog.InfoAsync($"Completed {request.Options.Kind} export to '{request.OutputPath}' with {encoder}.", cancellationToken);
            progress?.Report(new ProjectRenderProgress(1, "Complete", "Export complete"));
            return new ProjectRenderResult(request.OutputPath, srtPath, vttPath, encoder, duration, outputMeasurement, validation);
        }
        finally
        {
            if (File.Exists(partialPath)) File.Delete(partialPath);
            if (Directory.Exists(jobDirectory)) Directory.Delete(jobDirectory, recursive: true);
        }
    }

    private static async Task RunFfmpegAsync(
        IReadOnlyList<string> arguments,
        TimeSpan duration,
        double progressOffset,
        double progressScale,
        IProgress<ProjectRenderProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegRuntime.ResolveFfmpegPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(FfmpegRuntime.MissingRuntimeMessage, exception);
        }
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("out_time_ms=", StringComparison.Ordinal) || duration <= TimeSpan.Zero) continue;
            if (!long.TryParse(line.AsSpan("out_time_ms=".Length), CultureInfo.InvariantCulture, out var microseconds)) continue;
            var fraction = Math.Clamp(microseconds / 1_000_000d / duration.TotalSeconds, 0, 1);
            progress?.Report(new ProjectRenderProgress(progressOffset + (fraction * progressScale), "Rendering", $"{fraction:P0}"));
        }
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg render failed: {error.Trim()}");
    }
}
