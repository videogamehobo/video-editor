using HighlightForge.Core.Analysis;
using HighlightForge.Core.Captions;
using HighlightForge.Core.Models;
using HighlightForge.Core.Persistence;
using HighlightForge.Media.Analysis;

namespace HighlightForge.Core.Tests;

public sealed class LocalFeatureAnalyzerTests
{
    [Fact]
    public void ParsesFfmpegAudioMetadataAndIgnoresIncompleteFrames()
    {
        const string output = """
            frame:0 pts:0 pts_time:0
            lavfi.astats.Overall.RMS_level=-42.5
            frame:1 pts:4000 pts_time:0.5
            lavfi.astats.Overall.RMS_level=-18.25
            frame:2 pts:8000 pts_time:1
            """;

        var levels = LocalFeatureAnalyzer.ParseAudioLevels(output);

        Assert.Equal(2, levels.Count);
        Assert.Equal(TimeSpan.FromSeconds(0.5), levels[1].Position);
        Assert.Equal(-18.25, levels[1].RmsDb);
    }

    [Fact]
    public void ConvertsStrongMicrophoneAndGameSamplesIntoExplainableSignals()
    {
        var samples = new[]
        {
            (TimeSpan.Zero, -55d),
            (TimeSpan.FromSeconds(0.5), -40d),
            (TimeSpan.FromSeconds(1), -18d),
            (TimeSpan.FromSeconds(1.5), -17d),
            (TimeSpan.FromSeconds(4), -50d)
        };

        var microphone = LocalFeatureAnalyzer.CreateMicrophoneFeatures(samples, TimeSpan.FromSeconds(0.5));
        var game = LocalFeatureAnalyzer.CreateGameAudioFeatures(samples, TimeSpan.FromSeconds(0.5));

        Assert.Contains(microphone, feature => feature.Kind == FeatureKind.Speech);
        Assert.Contains(microphone, feature => feature.Kind == FeatureKind.VocalExcitement);
        Assert.Contains(game, feature => feature.Kind == FeatureKind.GameAudioPeak);
        Assert.All(microphone.Concat(game), feature => Assert.InRange(feature.Confidence, 0.35, 1));
    }

    [Fact]
    public void ParsesSparseSceneScores()
    {
        const string output = """
            frame:0 pts:16 pts_time:8
            lavfi.scene_score=0.418
            """;

        var scenes = LocalFeatureAnalyzer.ParseSceneSamples(output);

        var scene = Assert.Single(scenes);
        Assert.Equal(TimeSpan.FromSeconds(8), scene.Position);
        Assert.Equal(0.418, scene.Score);
    }

    [Fact]
    public void QuietButDynamicNormalizedAudioStillProducesRelativePeaks()
    {
        var samples = Enumerable.Range(0, 20)
            .Select(index => (TimeSpan.FromSeconds(index), index >= 17 ? -44d : -56d))
            .ToArray();

        var features = LocalFeatureAnalyzer.CreateGameAudioFeatures(samples, TimeSpan.FromSeconds(1));

        var peak = Assert.Single(features);
        Assert.Equal(FeatureKind.GameAudioPeak, peak.Kind);
        Assert.Equal(TimeSpan.FromSeconds(17), peak.Start);
    }

    [Theory]
    [InlineData("microphone", "microphone", true)]
    [InlineData("visual", "game-audio", true)]
    [InlineData("game-audio", "visual", false)]
    [InlineData("unknown", "microphone", false)]
    public void AnalysisStagesHaveDeterministicResumeOrdering(string completed, string requested, bool expected)
    {
        Assert.Equal(expected, LocalFeatureAnalyzer.StageCompleted(completed, requested));
    }

    [Fact]
    public void WorkerClientLaunchesTheSeparatePackagedExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "HighlightForgeWorkerLaunch", Guid.NewGuid().ToString("N"));
        var workerDirectory = Path.Combine(root, "worker");
        Directory.CreateDirectory(workerDirectory);
        var executable = Path.Combine(workerDirectory, "HighlightForge.Worker.exe");
        File.WriteAllBytes(executable, []);
        try
        {
            var startInfo = AnalysisWorkerClient.CreateStartInfo("test-pipe", root);

            Assert.Equal(executable, startInfo.FileName);
            Assert.Equal(["--pipe", "test-pipe"], startInfo.ArgumentList);
            Assert.True(startInfo.RedirectStandardError);
            Assert.False(startInfo.UseShellExecute);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TranscriptContextDetectsSpeechLaughterAndExcitement()
    {
        var cues = new[]
        {
            new CaptionCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "haha no way!")
        };

        var features = LocalFeatureAnalyzer.CreateTranscriptFeatures(cues);

        Assert.Contains(features, feature => feature.Kind == FeatureKind.Speech && feature.Detail.Contains("haha", StringComparison.Ordinal));
        Assert.Contains(features, feature => feature.Kind == FeatureKind.Laughter);
        Assert.Contains(features, feature => feature.Kind == FeatureKind.VocalExcitement);
    }

    [Fact]
    public void MotionAndEntropyMetadataBecomeRelativeVisualSignals()
    {
        const string metadata = """
            frame:0 pts:0 pts_time:0
            lavfi.signalstats.YAVG=1.0
            frame:1 pts:1 pts_time:1
            lavfi.signalstats.YAVG=2.0
            frame:2 pts:2 pts_time:2
            lavfi.signalstats.YAVG=12.0
            """;

        var samples = LocalFeatureAnalyzer.ParseMetadataValues(metadata, "lavfi.signalstats.YAVG=");
        var features = LocalFeatureAnalyzer.CreateRelativeVisualFeatures(samples, FeatureKind.Motion, "motion");

        Assert.Equal(3, samples.Count);
        Assert.Contains(features, feature => feature.Kind == FeatureKind.Motion && feature.Start == TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void YamnetPreprocessingProducesFiniteExpectedTensorAndMapsInterestingLabels()
    {
        var samples = Enumerable.Range(0, YamnetSoundEventAnalyzer.RequiredSamples)
            .Select(index => (float)Math.Sin(2 * Math.PI * 440 * index / YamnetSoundEventAnalyzer.SampleRate))
            .ToArray();
        var patch = YamnetSoundEventAnalyzer.ComputeLogMelPatch(samples);
        var labels = YamnetSoundEventAnalyzer.ParseClassMap([
            "index,mid,display_name",
            "0,/m/speech,Speech",
            "1,/m/explosion,Explosion"
        ]);
        var window = new FeatureEvent(FeatureKind.GameAudioPeak, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), 0.8, "peak");
        var mapped = YamnetSoundEventAnalyzer.SelectInterestingEvent([0.2f, 0.8f], labels, window);

        Assert.Equal(YamnetSoundEventAnalyzer.PatchFrames * YamnetSoundEventAnalyzer.MelBands, patch.Length);
        Assert.All(patch, value => Assert.True(float.IsFinite(value)));
        Assert.Equal(FeatureKind.GameAudioPeak, mapped!.Kind);
        Assert.Contains("Explosion", mapped.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void YamnetCatalogPinsArchiveModelDataAndClassMapHashes()
    {
        var pack = YamnetModelCatalog.Pack;

        Assert.Equal(64, pack.ArchiveSha256.Length);
        Assert.Equal(3, pack.Manifest.Files.Count);
        Assert.All(pack.Manifest.Files, file => Assert.Equal(64, file.Sha256.Length));
        Assert.All(pack.Manifest.Files, file => Assert.Equal("Apache-2.0", file.License));
    }

    [Fact]
    public void OcrTextIsNormalizedAndEnglishModelIsPinned()
    {
        Assert.Equal("MISSION COMPLETE 1200 XP", OcrFeatureAnalyzer.NormalizeText(" MISSION\nCOMPLETE\t1200 XP "));
        Assert.Equal(64, OcrModelCatalog.English.Manifest.Files[0].Sha256.Length);
        Assert.Equal("Apache-2.0", OcrModelCatalog.English.Manifest.Files[0].License);
        Assert.Contains(OcrModelCatalog.Version, OcrModelCatalog.English.DownloadUri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void FlorenceVisualEncoderPreprocessingAndEmbeddingMathAreDeterministic()
    {
        var rgb = new byte[FlorenceVisualContextAnalyzer.ImageSize * FlorenceVisualContextAnalyzer.ImageSize * 3];
        Array.Fill<byte>(rgb, 255);
        var tensor = FlorenceVisualContextAnalyzer.CreateImageTensor(rgb);
        var pooled = FlorenceVisualContextAnalyzer.MeanPool([1, 2, 3, 3, 4, 5], 3);

        Assert.Equal([1, 3, 768, 768], tensor.Dimensions.ToArray());
        Assert.Equal([2f, 3f, 4f], pooled);
        Assert.Equal(1, FlorenceVisualContextAnalyzer.CosineSimilarity(pooled, pooled), 6);
        Assert.Equal(64, FlorenceModelCatalog.BaseFtVisualEncoder.Manifest.Files[0].Sha256.Length);
        Assert.All(FlorenceModelCatalog.BaseFtVisualEncoder.Manifest.Files, file => Assert.Equal("MIT", file.License));
    }

    [Fact]
    public void PhiNarrativePackPinsEveryRuntimeAssetAndBuildsBoundedEvidencePrompt()
    {
        var candidate = HighlightScorer.EnsureIdentity(new HighlightCandidate(
            TimeSpan.FromSeconds(12),
            TimeSpan.FromSeconds(20),
            1.1,
            [new SelectionReason(FeatureKind.OnScreenText, 0.8, "MISSION COMPLETE")]));
        var spoken = HighlightScorer.EnsureIdentity(new HighlightCandidate(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(35),
            1,
            [new SelectionReason(FeatureKind.Speech, 0.5, "already explained")]));
        var pack = PhiModelCatalog.MiniInstructCpuInt4;

        var prompt = PhiNarrativeService.BuildPrompt([candidate, spoken]);
        var suggestions = PhiNarrativeService.ParseResponse("1|Explain why the mission finish was close.\n2|Ignore me", [candidate, spoken]);

        Assert.Equal(10, pack.Manifest.Files.Count);
        Assert.True(pack.DownloadSize > 4_900_000_000);
        Assert.All(pack.Assets, asset =>
        {
            Assert.Equal(64, asset.File.Sha256.Length);
            Assert.Equal("MIT", asset.File.License);
            Assert.Contains(PhiModelCatalog.Revision, asset.DownloadUri.AbsoluteUri, StringComparison.Ordinal);
        });
        Assert.Contains("MISSION COMPLETE", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("already explained", prompt, StringComparison.Ordinal);
        Assert.Equal(candidate.Id, Assert.Single(suggestions).CandidateId);
    }
}

public sealed class AnalysisResultStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HighlightForgeResultTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CompletedAnalysisSurvivesProjectReopen()
    {
        var sourceId = Guid.NewGuid();
        var feature = new FeatureEvent(FeatureKind.GameAudioPeak, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12), 0.8, "boss defeat audio");
        var candidate = new HighlightCandidate(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(19), 0.8, [new SelectionReason(feature.Kind, 0.8, feature.Detail)]);
        var identified = HighlightScorer.EnsureIdentity(candidate);
        var result = new LocalAnalysisResult(
            Guid.NewGuid(),
            sourceId,
            AnalysisMode.Balanced,
            [feature],
            [identified],
            new HighlightDraft([identified], identified.Duration),
            DateTimeOffset.UtcNow,
            [new HighlightForge.Core.Voiceover.NarrativeSuggestion(identified.Id, "Explain the winning play.")]);
        var store = new AnalysisResultStore(new ProjectPaths(_directory));

        await store.SaveAsync(result);
        var restored = await new AnalysisResultStore(new ProjectPaths(_directory)).LoadAsync(sourceId);

        Assert.NotNull(restored);
        Assert.Equal(result.JobId, restored.JobId);
        Assert.Equal("boss defeat audio", Assert.Single(restored.Features).Detail);
        Assert.Equal(candidate.SourceIn, Assert.Single(restored.Draft.Clips).SourceIn);
        Assert.Equal("Explain the winning play.", Assert.Single(restored.NarrativeSuggestions!).TalkingPoint);
    }

    [Fact]
    public async Task LatestAnalysisCheckpointPersistsRecoveryFeaturesAndStatus()
    {
        var sourceId = Guid.NewGuid();
        var olderJob = Guid.NewGuid();
        var latestJob = Guid.NewGuid();
        var feature = new FeatureEvent(
            FeatureKind.Speech,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            0.8,
            "checkpointed speech");
        var store = new AnalysisJobStore(new ProjectPaths(_directory));
        await store.SaveAsync(new AnalysisJobCheckpoint(
            olderJob, "microphone", 0.2, DateTimeOffset.UtcNow.AddMinutes(-1), SourceId: sourceId));
        await store.SaveAsync(new AnalysisJobCheckpoint(
            latestJob,
            "game-audio",
            0.55,
            DateTimeOffset.UtcNow,
            "Paused safely",
            sourceId,
            AnalysisMode.Balanced,
            AnalysisJobStatus.Paused,
            [feature]));

        var restored = await store.LoadLatestForSourceAsync(sourceId, AnalysisMode.Balanced);

        Assert.NotNull(restored);
        Assert.Equal(latestJob, restored.JobId);
        Assert.Equal(AnalysisJobStatus.Paused, restored.Status);
        Assert.Equal("checkpointed speech", Assert.Single(restored.Features!).Detail);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
