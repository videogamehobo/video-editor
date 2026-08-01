using HighlightForge.Core.Analysis;
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
        var result = new LocalAnalysisResult(Guid.NewGuid(), sourceId, AnalysisMode.Balanced, [feature], [candidate], new HighlightDraft([candidate], candidate.Duration), DateTimeOffset.UtcNow);
        var store = new AnalysisResultStore(new ProjectPaths(_directory));

        await store.SaveAsync(result);
        var restored = await new AnalysisResultStore(new ProjectPaths(_directory)).LoadAsync(sourceId);

        Assert.NotNull(restored);
        Assert.Equal(result.JobId, restored.JobId);
        Assert.Equal("boss defeat audio", Assert.Single(restored.Features).Detail);
        Assert.Equal(candidate.SourceIn, Assert.Single(restored.Draft.Clips).SourceIn);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
