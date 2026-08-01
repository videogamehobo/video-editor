using HighlightForge.Core.Analysis;
using HighlightForge.Core.Persistence;

namespace HighlightForge.Core.Tests;

public sealed class HighlightScorerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HighlightForgeAnalysisTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LaughterAndActionOutrankRoutineMotionAndBuildADiverseDraft()
    {
        var input = new AnalysisInput(TimeSpan.FromMinutes(10), AnalysisMode.Balanced,
        [
            new(FeatureKind.Motion, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12), 0.6, "routine movement"),
            new(FeatureKind.Laughter, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(102), 0.95, "creator laughter"),
            new(FeatureKind.GameAudioPeak, TimeSpan.FromSeconds(101), TimeSpan.FromSeconds(103), 0.9, "fight climax"),
            new(FeatureKind.Laughter, TimeSpan.FromSeconds(108), TimeSpan.FromSeconds(110), 0.95, "same moment reaction"),
            new(FeatureKind.VocalExcitement, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(302), 0.9, "excited shout")
        ]);

        var candidates = HighlightScorer.CreateCandidates(input);
        var draft = HighlightScorer.BuildDraft(candidates, TimeSpan.FromSeconds(40));

        Assert.Equal(FeatureKind.Laughter, candidates[0].Reasons[0].Kind);
        Assert.Equal(2, draft.Clips.Count);
        Assert.Contains(draft.Clips, clip => clip.Reasons.Any(reason => reason.Detail == "fight climax"));
    }

    [Fact]
    public async Task CheckpointSurvivesAStoreReopen()
    {
        var store = new AnalysisJobStore(new ProjectPaths(_directory));
        var checkpoint = new AnalysisJobCheckpoint(Guid.NewGuid(), "transcription", 0.42, DateTimeOffset.UtcNow, "locally transcribing mic track");

        await store.SaveAsync(checkpoint);
        var restored = await store.LoadAsync(checkpoint.JobId);

        Assert.Equal(checkpoint, restored);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
