using System.Security.Cryptography;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Models;
using HighlightForge.Core.Audio;
using HighlightForge.Core.Captions;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Export;
using HighlightForge.Core.Persistence;
using HighlightForge.Core.Release;
using HighlightForge.Media.Render;

namespace HighlightForge.Core.Tests;

public sealed class ReleaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HighlightForgeReleaseTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ModelPackVerificationRejectsChangedFiles()
    {
        var staged = Path.Combine(_directory, "staged");
        Directory.CreateDirectory(staged);
        var modelPath = Path.Combine(staged, "model.bin");
        await File.WriteAllTextAsync(modelPath, "local model");
        string hash;
        await using (var stream = File.OpenRead(modelPath))
        {
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        }
        var manifest = new ModelPackManifest("balanced", "1.0.0", "Local model pack", [new ModelFile("model.bin", hash, "MIT")]);
        var manager = new ModelPackManager(Path.Combine(_directory, "packs"));

        await manager.InstallFromDirectoryAsync(manifest, staged);
        var installed = await manager.ValidateAsync(manifest);
        await File.WriteAllTextAsync(Path.Combine(_directory, "packs", "balanced", "1.0.0", "model.bin"), "modified");
        var changed = await manager.ValidateAsync(manifest);

        Assert.True(installed.IsInstalled);
        Assert.False(changed.IsInstalled);
    }

    [Fact]
    public async Task ModelPackCannotEscapeItsVersionDirectory()
    {
        var staged = Path.Combine(_directory, "staged-escape");
        Directory.CreateDirectory(staged);
        var manifest = new ModelPackManifest("balanced", "1.0.0", "Local model pack", [new ModelFile("..\\outside.bin", new string('0', 64), "MIT")]);
        var manager = new ModelPackManager(Path.Combine(_directory, "packs"));

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallFromDirectoryAsync(manifest, staged));
    }

    [Fact]
    public async Task ModelPackCanRollbackToAStillVerifiedInstalledVersion()
    {
        var manager = new ModelPackManager(Path.Combine(_directory, "rollback-packs"));
        var version1 = await StageModelAsync("version-1", "first model");
        var version2 = await StageModelAsync("version-2", "second model");

        await manager.InstallFromDirectoryAsync(version1.Manifest, version1.Directory);
        await manager.ActivateAsync(version1.Manifest);
        await manager.InstallFromDirectoryAsync(version2.Manifest, version2.Directory);
        await manager.ActivateAsync(version2.Manifest);
        var before = await manager.ListInstalledVersionsAsync("balanced");
        var restored = await manager.RollbackAsync("balanced");
        var after = await manager.ListInstalledVersionsAsync("balanced");

        Assert.Equal("version-2", Assert.Single(before, version => version.IsActive).Manifest.Version);
        Assert.Equal("version-1", restored.Version);
        Assert.Equal("version-1", Assert.Single(after, version => version.IsActive).Manifest.Version);
        Assert.All(after, version => Assert.True(version.Status.IsInstalled));
    }

    [Fact]
    public void ShortPlanPreservesGameFrameOverBlurredVerticalBackground()
    {
        var arguments = RenderPlan.BuildArguments(new RenderRequest(RenderKind.Vertical, "source.mkv", "short.mp4", new AudioMixSettings()));

        Assert.Contains(arguments, argument => argument.Contains("gblur=sigma=20", StringComparison.Ordinal));
        Assert.Contains("h264_mf", arguments);
        Assert.Contains(arguments, argument => argument.StartsWith("loudnorm=I=-14", StringComparison.Ordinal));
    }

    [Fact]
    public void ExportRejectsWritingOverTheOriginalRecording()
    {
        var source = Path.Combine(_directory, "original.mkv");
        var request = new RenderRequest(RenderKind.LongForm, source, source, new AudioMixSettings());

        var exception = Assert.Throws<InvalidOperationException>(() => RenderPlan.BuildArguments(request));

        Assert.Contains("cannot overwrite the original recording", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectExportRendersOnlyEditedClipsWithConfirmedDiscreteAudio()
    {
        var request = CreateProjectRenderRequest(RenderKind.LongForm);
        var intermediate = Path.Combine(_directory, "cache", "program.mkv");

        var arguments = ProjectRenderPlan.BuildIntermediateArguments(request, intermediate, assPath: null, "h264_mf");
        var filter = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Equal(2, arguments.Count(argument => argument == "-ss"));
        Assert.Contains("concat=n=2:v=1:a=1", filter, StringComparison.Ordinal);
        Assert.Contains("sidechaincompress", filter, StringComparison.Ordinal);
        Assert.Contains("acompressor", filter, StringComparison.Ordinal);
        Assert.Contains("alimiter=limit=0.891:level=false", filter, StringComparison.Ordinal);
        Assert.Contains("[0:2]", filter, StringComparison.Ordinal);
        Assert.Contains("[0:3]", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("[0:1]", filter, StringComparison.Ordinal);
        Assert.Equal(intermediate, arguments[^1]);
    }

    [Fact]
    public void ProjectExportAppliesClipGainFadesAndRestrainedPunchZoom()
    {
        var request = CreateProjectRenderRequest(RenderKind.LongForm);
        var adjusted = request.Project.Timeline[0] with
        {
            GainDb = -2.5,
            FadeIn = TimeSpan.FromSeconds(0.4),
            FadeOut = TimeSpan.FromSeconds(0.6),
            PunchZoom = true,
            ReframeX = 0.65,
            ReframeY = 0.4
        };
        request = request with { Project = request.Project with { Timeline = [adjusted] } };

        var arguments = ProjectRenderPlan.BuildIntermediateArguments(request, Path.Combine(_directory, "adjusted.mkv"), null, "h264_mf");
        var filter = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Contains("volume=-2.5dB", filter, StringComparison.Ordinal);
        Assert.Contains("afade=t=in:st=0:d=0.4", filter, StringComparison.Ordinal);
        Assert.Contains("afade=t=out:st=4.4:d=0.6", filter, StringComparison.Ordinal);
        Assert.Contains("zoompan=z='min(max(zoom,1)+0.002,1.08)'", filter, StringComparison.Ordinal);
        Assert.Contains("fade=t=out:st=4.4:d=0.6", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiSourceExportUsesEachSourcesCaptionsAndMixSettings()
    {
        var request = CreateProjectRenderRequest(RenderKind.LongForm);
        var firstSource = request.Project.Sources[0];
        var secondSource = firstSource with
        {
            Id = Guid.NewGuid(),
            AbsolutePath = Path.Combine(_directory, "original-two.mkv")
        };
        var project = request.Project with
        {
            Sources = [firstSource, secondSource],
            Timeline =
            [
                request.Project.Timeline[0],
                request.Project.Timeline[1] with { SourceId = secondSource.Id }
            ]
        };
        var firstState = CreatorWorkflowState.Empty(firstSource.Id) with
        {
            Captions = [new CaptionCue(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11), "first source")],
            AudioSettings = new AudioMixSettings(MicrophoneGainDb: 2)
        };
        var secondState = CreatorWorkflowState.Empty(secondSource.Id) with
        {
            Captions = [new CaptionCue(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(31), "second source")],
            AudioSettings = new AudioMixSettings(GameGainDb: -4)
        };
        request = request with
        {
            Project = project,
            CreatorState = firstState,
            CreatorStates = new Dictionary<Guid, CreatorWorkflowState>
            {
                [firstSource.Id] = firstState,
                [secondSource.Id] = secondState
            }
        };

        var captions = ProjectRenderPlan.MapCaptions(request);
        var arguments = ProjectRenderPlan.BuildIntermediateArguments(request, Path.Combine(_directory, "multi.mkv"), null, "h264_mf");
        var filter = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Equal(["first source", "second source"], captions.Select(cue => cue.Text));
        Assert.Equal(TimeSpan.FromSeconds(5), captions[1].Start);
        Assert.Contains("volume=2dB[mic0]", filter, StringComparison.Ordinal);
        Assert.Contains("volume=-4dB[game1]", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void VerticalExportUsesSafeBlurUnlessFocusedCropIsConfidentOrManual()
    {
        var safeRequest = CreateProjectRenderRequest(RenderKind.Vertical);
        var safeArguments = ProjectRenderPlan.BuildIntermediateArguments(safeRequest, Path.Combine(_directory, "safe.mkv"), null, "h264_mf");
        var safeFilter = safeArguments[safeArguments.ToList().IndexOf("-filter_complex") + 1];
        var unsafeAutomatic = safeRequest with
        {
            Options = safeRequest.Options with { VerticalComposition = VerticalComposition.FocusedCrop, FocusConfidence = 0.4 }
        };
        var manual = unsafeAutomatic with { Options = unsafeAutomatic.Options with { FocusX = 0.35 } };

        Assert.Contains("gblur=sigma=20", safeFilter, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => ProjectRenderPlan.Validate(unsafeAutomatic));
        ProjectRenderPlan.Validate(manual);
    }

    [Fact]
    public void FinalPassCopiesVideoAndUsesMeasuredLoudnessValues()
    {
        var measurement = new AudioLoudnessMeasurement(1, "Program", -21.5, -3.2, 4.1, -31.7, -0.2);

        var arguments = ProjectRenderPlan.BuildFinalArguments("program.mkv", "final.mp4", new AudioMixSettings(), measurement);

        Assert.Contains("copy", arguments);
        Assert.Contains(arguments, argument => argument.Contains("measured_I=-21.5", StringComparison.Ordinal));
        Assert.Contains(arguments, argument => argument.Contains("TP=-1", StringComparison.Ordinal));
    }

    [Fact]
    public void SilentProgramBypassesGainInsteadOfAmplifyingCodecNoise()
    {
        var silence = new AudioLoudnessMeasurement(1, "Program", -99, -99, 0, -70, 99);

        var arguments = ProjectRenderPlan.BuildFinalArguments("program.mkv", "final.mp4", new AudioMixSettings(), silence);

        Assert.Equal("anull", arguments[arguments.ToList().IndexOf("-af") + 1]);
    }

    [Fact]
    public void AssCaptionsUseVerticalSafeZoneAndEscapeOverrideCharacters()
    {
        var document = AssCaptionDocument.Create(
            [new CaptionCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Use {item}\\path")],
            vertical: true);

        Assert.Contains("PlayResX: 1080", document, StringComparison.Ordinal);
        Assert.Contains(",240,1", document, StringComparison.Ordinal);
        Assert.Contains("Use \\{item\\}\\\\path", document, StringComparison.Ordinal);
    }

    [Fact]
    public void EncoderSelectionUsesNvidiaThenWindowsCpuFallback()
    {
        Assert.Equal("h264_nvenc", FfmpegEncoderCapabilities.Choose(["h264_nvenc", "h264_mf"], preferNvidia: true));
        Assert.Equal("h264_mf", FfmpegEncoderCapabilities.Choose(["h264_nvenc", "h264_mf"], preferNvidia: false));
        Assert.Equal(["h264_nvenc", "h264_mf", "libopenh264"], FfmpegEncoderCapabilities.Ranked(["h264_nvenc", "h264_mf", "libopenh264"], preferNvidia: true));
        Assert.Equal(["h264_mf", "libopenh264"], FfmpegEncoderCapabilities.Ranked(["h264_nvenc", "h264_mf", "libopenh264"], preferNvidia: false));
        Assert.Throws<InvalidOperationException>(() => FfmpegEncoderCapabilities.Choose([], preferNvidia: true));
    }

    [Fact]
    public void MetadataSuggestionsRemainLocalAndFollowTimelineOrder()
    {
        var request = CreateProjectRenderRequest(RenderKind.LongForm);
        var captions = new[]
        {
            new CaptionCue(TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(32), "The second exported clip appears first in source time")
        };

        var suggestion = CreatorMetadataSuggestions.Create(request.Project, captions);
        var chapters = CreatorMetadataSuggestions.ToChapterText(suggestion.Chapters);

        Assert.Equal("Export test Highlights", suggestion.Title);
        Assert.StartsWith("0:00 ", chapters, StringComparison.Ordinal);
        Assert.Contains("0:05 The second exported clip", chapters, StringComparison.Ordinal);
        Assert.Contains("processed on-device", suggestion.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputValidatorEnforcesCodecsVerticalDimensionsSyncAndAudioGate()
    {
        const string validJson = """
            { "format": { "duration": "10.000" }, "streams": [
              { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1080, "height": 1920, "start_time": "0.000", "duration": "10.000" },
              { "index": 1, "codec_type": "audio", "codec_name": "aac", "start_time": "0.000", "duration": "10.000" }
            ] }
            """;
        var loudness = new AudioLoudnessMeasurement(1, "Final", -14.5, -1.2, 5, -25, 0);

        var valid = MediaOutputValidator.Parse(validJson, RenderKind.Vertical, TimeSpan.FromSeconds(10), loudness, new AudioMixSettings());
        var invalid = MediaOutputValidator.Parse(
            validJson.Replace("1080", "1920", StringComparison.Ordinal),
            RenderKind.Vertical,
            TimeSpan.FromSeconds(9),
            loudness with { TruePeakDbtp = -0.5 },
            new AudioMixSettings());

        Assert.True(valid.IsValid);
        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Problems, problem => problem.Contains("1080x1920", StringComparison.Ordinal));
        Assert.Contains(invalid.Problems, problem => problem.Contains("duration", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(invalid.Problems, problem => problem.Contains("True peak", StringComparison.Ordinal));

        const string longFormJson = """
            { "format": { "duration": "10.000" }, "streams": [
              { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "start_time": "0.000", "duration": "10.000" },
              { "index": 1, "codec_type": "audio", "codec_name": "aac", "start_time": "0.000", "duration": "10.000" }
            ] }
            """;
        var longForm = MediaOutputValidator.Parse(
            longFormJson,
            RenderKind.LongForm,
            TimeSpan.FromSeconds(10),
            loudness,
            new AudioMixSettings(),
            expectedWidth: 1920,
            expectedHeight: 1080);
        Assert.True(longForm.IsValid);
    }

    [Fact]
    public void CreatorBenchmarkGateRequiresTenSessionsEightyPercentRecallAndSixtyPercentAcceptance()
    {
        var sessions = Enumerable.Range(1, 10).Select(index =>
        {
            var mustKeep = new BenchmarkMoment(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12));
            var accepted = new BenchmarkMoment(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(24));
            var recalled = index <= 8
                ? new HighlightCandidate(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(13), 1, [])
                : new HighlightCandidate(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(44), 1, []);
            var draft = index <= 6
                ? new HighlightCandidate(TimeSpan.FromSeconds(19), TimeSpan.FromSeconds(25), 1, [])
                : new HighlightCandidate(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(55), 1, []);
            return new CreatorBenchmarkSession($"session-{index}", [mustKeep], [accepted], [recalled], [draft]);
        }).ToArray();

        var report = CreatorBenchmarkGate.Evaluate(sessions);

        Assert.True(report.Passed);
        Assert.Equal(0.8, report.MustKeepRecall, 3);
        Assert.Equal(0.6, report.DraftAcceptance, 3);
        Assert.False(CreatorBenchmarkGate.Evaluate(sessions.Take(9).ToArray()).Passed);
    }

    [Fact]
    public void PerformanceGateEnforcesGpuTargetAndRecoveryWhileAllowingSlowerCpuFallback()
    {
        var gpu = AnalysisPerformanceGate.Evaluate(TimeSpan.FromHours(4), TimeSpan.FromHours(2), cpuOnly: false, completed: true, pauseResumeRecovered: true);
        var slowCpu = AnalysisPerformanceGate.Evaluate(TimeSpan.FromHours(4), TimeSpan.FromHours(8), cpuOnly: true, completed: true, pauseResumeRecovered: true);
        var failedRecovery = AnalysisPerformanceGate.Evaluate(TimeSpan.FromHours(4), TimeSpan.FromHours(1), cpuOnly: false, completed: true, pauseResumeRecovered: false);

        Assert.True(gpu.Passed);
        Assert.True(slowCpu.Passed);
        Assert.False(failedRecovery.Passed);
    }

    private ProjectRenderRequest CreateProjectRenderRequest(RenderKind kind)
    {
        var sourceId = Guid.NewGuid();
        var sourcePath = Path.Combine(_directory, "original.mkv");
        var source = new MediaSource(
            sourceId,
            sourcePath,
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            60,
            [
                new AudioTrack(1, "Main", 2, 48000, AudioTrackRole.Mixed),
                new AudioTrack(2, "Game", 2, 48000, AudioTrackRole.Game),
                new AudioTrack(3, "Voice", 1, 48000, AudioTrackRole.Microphone)
            ],
            AudioRolesConfirmed: true);
        var project = ProjectDocument.Create("Export test", DateTimeOffset.UtcNow) with
        {
            Sources = [source],
            Timeline =
            [
                new TimelineClip(Guid.NewGuid(), sourceId, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.Zero),
                new TimelineClip(Guid.NewGuid(), sourceId, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(36), TimeSpan.FromSeconds(5))
            ]
        };
        var state = CreatorWorkflowState.Empty(sourceId);
        return new ProjectRenderRequest(
            new ProjectPaths(Path.Combine(_directory, "project.gheproj")),
            project,
            state,
            Path.Combine(_directory, kind == RenderKind.Vertical ? "short.mp4" : "highlights.mp4"),
            new ProjectRenderOptions(kind));
    }

    private async Task<(ModelPackManifest Manifest, string Directory)> StageModelAsync(string version, string contents)
    {
        var directory = Path.Combine(_directory, $"staged-{version}");
        Directory.CreateDirectory(directory);
        var modelPath = Path.Combine(directory, "model.bin");
        await File.WriteAllTextAsync(modelPath, contents);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(modelPath)));
        return (new ModelPackManifest("balanced", version, "Rollback test", [new ModelFile("model.bin", hash, "MIT")]), directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
