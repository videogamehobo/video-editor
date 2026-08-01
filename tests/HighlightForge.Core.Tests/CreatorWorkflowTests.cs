using System.Net;
using System.Security.Cryptography;
using System.Text;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Audio;
using HighlightForge.Core.Captions;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Models;
using HighlightForge.Core.Persistence;
using HighlightForge.Core.Preferences;
using HighlightForge.Core.Voiceover;
using HighlightForge.Media.Audio;
using HighlightForge.Media.Captions;
using HighlightForge.Media.Models;
using HighlightForge.Media.Render;
using HighlightForge.Media.Runtime;

namespace HighlightForge.Core.Tests;

public sealed class CreatorWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HighlightForgeCreatorTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CaptionExportCreatesPlatformReadySrtAndVtt()
    {
        var cues = new[] { new CaptionCue(TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(3.4), "That was close!") };

        Assert.Contains("00:00:01,200 --> 00:00:03,400", CaptionDocument.ToSrt(cues));
        Assert.StartsWith("WEBVTT", CaptionDocument.ToWebVtt(cues));
    }

    [Fact]
    public void CaptionBurnInUsesEditableValidatedStyle()
    {
        var style = new CaptionStyleSettings("Arial", 48, "#12ABEF", "#010203", 4, CaptionPlacement.Top);

        var ass = AssCaptionDocument.Create(
            [new CaptionCue(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Styled")],
            vertical: false,
            style);

        Assert.Contains("Style: Default,Arial,48,&H00EFAB12", ass, StringComparison.Ordinal);
        Assert.Contains(",4,1,8,", ass, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new CaptionStyleSettings(PrimaryColor: "white").Validated());
    }

    [Fact]
    public void CaptionEditsPersistTextAndValidatedTimestamps()
    {
        var cues = new[] { new CaptionCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Old") };

        var updated = CaptionDocument.UpdateCue(cues, 0, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3), " New text ");

        Assert.Equal(TimeSpan.FromSeconds(1.5), updated[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(3), updated[0].End);
        Assert.Equal("New text", updated[0].Text);
        Assert.Null(updated[0].Words);
        Assert.Throws<ArgumentException>(() => CaptionDocument.UpdateCue(cues, 0, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2), "Invalid"));
    }

    [Fact]
    public void WordTimingIsGroupedIntoReadableCaptionCues()
    {
        var words = new[]
        {
            new CaptionWord(TimeSpan.Zero, TimeSpan.FromSeconds(0.4), "That"),
            new CaptionWord(TimeSpan.FromSeconds(0.42), TimeSpan.FromSeconds(0.8), "was"),
            new CaptionWord(TimeSpan.FromSeconds(0.82), TimeSpan.FromSeconds(1.2), "close!"),
            new CaptionWord(TimeSpan.FromSeconds(2.1), TimeSpan.FromSeconds(2.5), "Again")
        };

        var cues = CaptionDocument.GroupWords(words);

        Assert.Equal(2, cues.Count);
        Assert.Equal("That was close!", cues[0].Text);
        Assert.Equal(3, cues[0].Words!.Count);
        Assert.Equal(TimeSpan.FromSeconds(2.1), cues[1].Start);
    }

    [Fact]
    public void CaptionsFollowTrimmedAndReorderedTimelineClips()
    {
        var sourceId = Guid.NewGuid();
        var cues = new[]
        {
            new CaptionCue(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(12), "Trimmed at the in point"),
            new CaptionCue(TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(22), "Second clip")
        };
        var timeline = new[]
        {
            new TimelineClip(Guid.NewGuid(), sourceId, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(25), TimeSpan.Zero),
            new TimelineClip(Guid.NewGuid(), sourceId, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5))
        };

        var mapped = CaptionTimelineMapper.MapToTimeline(cues, timeline, sourceId);

        Assert.Equal("Second clip", mapped[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), mapped[0].Start);
        Assert.Equal("Trimmed at the in point", mapped[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(5), mapped[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(7), mapped[1].End);
    }

    [Fact]
    public void WordTimingFollowsTimelineTrimAndReorder()
    {
        var sourceId = Guid.NewGuid();
        var cue = new CaptionCue(
            TimeSpan.FromSeconds(9),
            TimeSpan.FromSeconds(13),
            "outside inside kept",
            [
                new CaptionWord(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(9.5), "outside"),
                new CaptionWord(TimeSpan.FromSeconds(10.2), TimeSpan.FromSeconds(10.8), "inside"),
                new CaptionWord(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(12.5), "kept")
            ]);
        var timeline = new[]
        {
            new TimelineClip(Guid.NewGuid(), sourceId, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(5))
        };

        var mapped = Assert.Single(CaptionTimelineMapper.MapToTimeline([cue], timeline, sourceId));

        Assert.Equal(2, mapped.Words!.Count);
        Assert.Equal(TimeSpan.FromSeconds(5.2), mapped.Words[0].Start);
        Assert.Equal("kept", mapped.Words[1].Text);
    }

    [Fact]
    public void VoiceoverPlannerMarksUnexplainedAction()
    {
        var clips = new[] { new HighlightCandidate(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2.5), 1.2, [new SelectionReason(FeatureKind.GameAudioPeak, 0.8, "boss fight finish")]) };

        var suggestion = Assert.Single(VoiceoverPlanner.Suggest(clips));

        Assert.Contains("boss fight finish", suggestion.TalkingPoint);
    }

    [Fact]
    public void VoiceoverPlannerUsesLocalNarrativeForMatchingCandidate()
    {
        var candidate = HighlightScorer.EnsureIdentity(new HighlightCandidate(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            1.2,
            [new SelectionReason(FeatureKind.GameAudioPeak, 0.8, "boss finish")]));
        var narrative = new NarrativeSuggestion(candidate.Id, "Explain the risky choice before the final hit.");

        var suggestion = Assert.Single(VoiceoverPlanner.Suggest([candidate], [narrative]));

        Assert.Equal(narrative.TalkingPoint, suggestion.TalkingPoint);
    }

    [Fact]
    public void VoiceoverSuggestionsFollowTrimmedAndReorderedTimeline()
    {
        var sourceId = Guid.NewGuid();
        var candidates = new[]
        {
            new HighlightCandidate(TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(40), 1.2, [new SelectionReason(FeatureKind.GameAudioPeak, 0.8, "boss finish")]),
            new HighlightCandidate(TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(18), 1.1, [new SelectionReason(FeatureKind.Motion, 0.7, "surprise")])
        };
        var timeline = new[]
        {
            new TimelineClip(Guid.NewGuid(), sourceId, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(36), TimeSpan.Zero),
            new TimelineClip(Guid.NewGuid(), sourceId, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(6))
        };

        var suggestions = VoiceoverPlanner.SuggestForTimeline(candidates, timeline, sourceId);

        Assert.Equal(TimeSpan.FromSeconds(1), suggestions[0].TimelinePosition);
        Assert.Contains("boss finish", suggestions[0].TalkingPoint, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(7), suggestions[1].TimelinePosition);
        Assert.Equal(TimeSpan.FromSeconds(4), suggestions[1].MaximumDuration);
    }

    [Fact]
    public void MixPlanNeverUsesMixedTrackWithDiscreteTracks()
    {
        var mapping = AudioTrackMapper.Suggest([new(1, "Mixed", 2, 48000), new(2, "Microphone", 1, 48000), new(3, "Game", 2, 48000)]);
        var plan = AudioMixPlanner.Create(mapping.Tracks, mapping.UsesDiscreteTracks);

        Assert.True(plan.UsesDiscreteTracks);
        Assert.DoesNotContain(plan.InputTracks, track => track.Role == AudioTrackRole.Mixed);
        Assert.Contains("loudnorm=I=-14", AudioMixPlanner.BuildFinalLoudnessFilter(plan.Settings));
        var microphone = Assert.Single(plan.InputTracks, track => track.Role == AudioTrackRole.Microphone);
        var game = Assert.Single(plan.InputTracks, track => track.Role == AudioTrackRole.Game);
        var ducking = AudioMixPlanner.BuildDiscreteDuckingFilter(microphone, game, plan.Settings);
        Assert.Contains("sidechaincompress", ducking, StringComparison.Ordinal);
        Assert.Contains("attack=60:release=450", ducking, StringComparison.Ordinal);
        Assert.DoesNotContain("makeup=", ducking, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualMixControlsChangeOnlyTheGeneratedMixPlan()
    {
        var microphone = new AudioTrack(2, "Microphone", 1, 48000, AudioTrackRole.Microphone);
        var game = new AudioTrack(3, "Game", 2, 48000, AudioTrackRole.Game);
        var settings = new AudioMixSettings(
            DuckingDb: -14,
            DuckingAttackMs: 75,
            DuckingReleaseMs: 600,
            MicrophoneGainDb: 2.5,
            GameGainDb: -3);

        var filter = AudioMixPlanner.BuildDiscreteDuckingFilter(microphone, game, settings);

        Assert.Contains("volume=2.5dB[mic]", filter, StringComparison.Ordinal);
        Assert.Contains("volume=-3dB[game]", filter, StringComparison.Ordinal);
        Assert.Contains("ratio=8", filter, StringComparison.Ordinal);
        Assert.Contains("attack=75:release=600", filter, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => (settings with { MicrophoneGainDb = 30 }).Validated());
    }

    [Fact]
    public void TrackRolesRequireAnExplicitUsableAndUnambiguousMapping()
    {
        var discrete = AudioTrackRoleValidator.Validate(
        [
            new(1, "Main", 2, 48000, AudioTrackRole.Mixed),
            new(2, "Voice", 1, 48000, AudioTrackRole.Microphone),
            new(3, "Game", 2, 48000, AudioTrackRole.Game)
        ]);
        var missing = AudioTrackRoleValidator.Validate([new(1, "Unknown", 2, 48000)]);
        var duplicate = AudioTrackRoleValidator.Validate(
        [
            new(1, "Voice 1", 1, 48000, AudioTrackRole.Microphone),
            new(2, "Voice 2", 1, 48000, AudioTrackRole.Microphone),
            new(3, "Game", 2, 48000, AudioTrackRole.Game)
        ]);

        Assert.True(discrete.IsValid);
        Assert.True(discrete.UsesDiscreteTracks);
        Assert.False(missing.IsValid);
        Assert.False(duplicate.IsValid);
    }

    [Fact]
    public async Task PreferencesRemainLocalToTheProject()
    {
        var store = new CreatorPreferencesStore(_directory);
        var accepted = Guid.NewGuid();
        var rejected = Guid.NewGuid();
        await store.SaveAsync(new CreatorPreferences(FunnyWeight: 1.3, AcceptedCandidateIds: new HashSet<Guid> { accepted }, RejectedCandidateIds: new HashSet<Guid> { rejected }));

        var loaded = await store.LoadAsync();

        Assert.Equal(1.3, loaded.FunnyWeight);
        Assert.Contains(accepted, loaded.AcceptedCandidateIds ?? new HashSet<Guid>());
        Assert.Contains(rejected, loaded.RejectedCandidateIds ?? new HashSet<Guid>());
        Assert.True(File.Exists(Path.Combine(_directory, "preferences.json")));
        Assert.Empty(Directory.EnumerateFiles(_directory, "preferences.json.*.tmp"));
    }

    [Fact]
    public async Task CreatorStatePersistsEditableCaptionsTakesAndMeasurements()
    {
        var sourceId = Guid.NewGuid();
        var store = new CreatorWorkflowStore(new ProjectPaths(_directory));
        var state = new CreatorWorkflowState(
            sourceId,
            [new CaptionCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Edited locally")],
            [new VoiceoverTake(Guid.NewGuid(), Path.Combine(_directory, "takes", "take.wav"), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), true)],
            [new AudioLoudnessMeasurement(2, "Microphone", -20, -4, 3, -30, 0.2)],
            DateTimeOffset.UtcNow);

        await store.SaveAsync(state);
        var restored = await store.LoadAsync(sourceId);

        Assert.Equal("Edited locally", restored.Captions.Single().Text);
        Assert.True(restored.VoiceoverTakes.Single().IsSelected);
        Assert.Equal(-20, restored.LoudnessMeasurements.Single().IntegratedLufs);
    }

    [Fact]
    public void GeneratedMediaMustStayInsideItsControlledProjectDirectory()
    {
        var cache = Path.Combine(_directory, "project", "cache");
        var safe = Path.Combine(cache, "transcription", "commentary.wav");
        var unsafePath = Path.Combine(_directory, "original-recording.mkv");

        Assert.Equal(Path.GetFullPath(safe), MediaPathSafety.RequireOutputWithinDirectory(cache, safe, "Test"));
        Assert.Throws<InvalidOperationException>(() => MediaPathSafety.RequireOutputWithinDirectory(cache, unsafePath, "Test"));
    }

    [Fact]
    public void TranscriptionExtractionReadsSourceAndWritesASeparateWaveFile()
    {
        var source = Path.Combine(_directory, "original.mkv");
        var output = Path.Combine(_directory, "project", "cache", "speech.wav");

        var arguments = WhisperTranscriptionService.BuildExtractionArguments(source, 2, output);
        var argumentList = arguments.ToList();

        Assert.Equal(source, arguments[argumentList.IndexOf("-i") + 1]);
        Assert.Equal("0:2", arguments[argumentList.IndexOf("-map") + 1]);
        Assert.Equal(output, arguments[^1]);
        Assert.NotEqual(source, arguments[^1]);
    }

    [Fact]
    public void LoudnessMeasurementBuildsVerifiedSecondPassFilter()
    {
        const string output = """
            [Parsed_loudnorm_0] {
                "input_i" : "-21.50",
                "input_tp" : "-3.20",
                "input_lra" : "4.10",
                "input_thresh" : "-31.70",
                "output_i" : "-13.80",
                "output_tp" : "-1.00",
                "output_lra" : "3.90",
                "output_thresh" : "-24.20",
                "normalization_type" : "dynamic",
                "target_offset" : "-0.20"
            }
            """;
        var track = new AudioTrack(2, "Microphone", 1, 48000, AudioTrackRole.Microphone);

        var measurement = AudioLoudnessAnalyzer.Parse(track, output);
        var filter = AudioMixPlanner.BuildMeasuredLoudnessFilter(new AudioMixSettings(), measurement);

        Assert.Equal(-21.5, measurement.IntegratedLufs);
        Assert.Contains("measured_I=-21.5", filter, StringComparison.Ordinal);
        Assert.Contains("TP=-1", filter, StringComparison.Ordinal);
        Assert.Contains("linear=false", filter, StringComparison.Ordinal);
        Assert.Contains("alimiter=limit=0.85:level=false", filter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelInstallerVerifiesBeforeInstallingAndKeepsVersionsSeparate()
    {
        var bytes = Encoding.UTF8.GetBytes("small local test model");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var manifest = new ModelPackManifest("test-whisper", "revision-1", "Test", [new ModelFile("model.bin", hash, "MIT")]);
        var pack = new WhisperModelPack(AnalysisMode.Fast, "Test", new Uri("https://models.invalid/model.bin"), bytes.Length, manifest);
        using var client = new HttpClient(new StaticResponseHandler(bytes));
        var root = Path.Combine(_directory, "models");
        var installer = new WhisperModelInstaller(client, root);

        var installedPath = await installer.InstallAsync(pack);

        Assert.Equal(Path.Combine(root, "test-whisper", "revision-1", "model.bin"), installedPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(installedPath));
        Assert.False(Directory.EnumerateDirectories(Path.Combine(root, ".staging")).Any());
    }

    [Fact]
    public async Task ModelInstallerRejectsAnUnverifiedDownloadWithoutInstallingIt()
    {
        var bytes = Encoding.UTF8.GetBytes("corrupt model payload");
        var manifest = new ModelPackManifest("test-whisper", "revision-bad", "Test", [new ModelFile("model.bin", new string('0', 64), "MIT")]);
        var pack = new WhisperModelPack(AnalysisMode.Fast, "Test", new Uri("https://models.invalid/model.bin"), bytes.Length, manifest);
        using var client = new HttpClient(new StaticResponseHandler(bytes));
        var root = Path.Combine(_directory, "bad-models");
        var installer = new WhisperModelInstaller(client, root);

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(pack));

        Assert.False(File.Exists(WhisperModelCatalog.InstalledModelPath(root, pack)));
        Assert.False(Directory.EnumerateDirectories(Path.Combine(root, ".staging")).Any());
    }

    [Fact]
    public async Task WhisperRollbackChangesTheModelPathUsedForTranscription()
    {
        var root = Path.Combine(_directory, "rollback-models");
        var firstBytes = Encoding.UTF8.GetBytes("first model");
        var secondBytes = Encoding.UTF8.GetBytes("second model");
        var first = CreateTestPack("revision-1", firstBytes);
        var second = CreateTestPack("revision-2", secondBytes);
        using var firstClient = new HttpClient(new StaticResponseHandler(firstBytes));
        using var secondClient = new HttpClient(new StaticResponseHandler(secondBytes));
        await new WhisperModelInstaller(firstClient, root).InstallAsync(first);
        var secondInstaller = new WhisperModelInstaller(secondClient, root);
        await secondInstaller.InstallAsync(second);

        await new ModelPackManager(root).RollbackAsync(first.Manifest.Id);
        var activePath = await secondInstaller.GetActiveModelPathAsync(second);

        Assert.NotNull(activePath);
        Assert.Contains("revision-1", activePath, StringComparison.Ordinal);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(activePath));
    }

    [Fact]
    public void WhisperCatalogPinsEveryModelToAHashAndImmutableRevision()
    {
        Assert.All(WhisperModelCatalog.All, pack =>
        {
            Assert.Equal(WhisperModelCatalog.Revision, pack.Manifest.Version);
            Assert.Equal(64, pack.ModelFile.Sha256.Length);
            Assert.Contains(WhisperModelCatalog.Revision, pack.DownloadUri.AbsoluteUri, StringComparison.Ordinal);
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static WhisperModelPack CreateTestPack(string version, byte[] bytes)
    {
        var file = new ModelFile("model.bin", Convert.ToHexString(SHA256.HashData(bytes)), "MIT");
        return new WhisperModelPack(
            AnalysisMode.Fast,
            $"Test {version}",
            new Uri($"https://models.invalid/{version}/model.bin"),
            bytes.Length,
            new ModelPackManifest("test-whisper-rollback", version, "Test", [file]));
    }

    private sealed class StaticResponseHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
    }
}
