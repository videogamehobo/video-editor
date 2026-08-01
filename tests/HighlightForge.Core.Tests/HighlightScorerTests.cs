using HighlightForge.Core.Analysis;
using HighlightForge.Core.Persistence;
using HighlightForge.Core.Preferences;

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
        Assert.NotEqual(Guid.Empty, candidates[0].Id);
        Assert.Equal(candidates.MaxBy(candidate => candidate.Score)?.Id, draft.Clips[0].Id);
    }

    [Fact]
    public void LocalPreferencesRerankAcceptAndRejectWithoutFineTuning()
    {
        var funny = HighlightScorer.EnsureIdentity(new HighlightCandidate(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            1,
            [new SelectionReason(FeatureKind.Laughter, 0.7, "laugh")]));
        var action = HighlightScorer.EnsureIdentity(new HighlightCandidate(
            TimeSpan.FromSeconds(50),
            TimeSpan.FromSeconds(60),
            1,
            [new SelectionReason(FeatureKind.GameAudioPeak, 0.7, "boss")]));
        var preferences = new CreatorPreferences(
            FunnyWeight: 0.5,
            ActionWeight: 2,
            AcceptedCandidateIds: new HashSet<Guid> { action.Id },
            RejectedCandidateIds: new HashSet<Guid> { funny.Id });

        var ranked = HighlightScorer.Rerank([funny, action], preferences);

        var selected = Assert.Single(ranked);
        Assert.Equal(action.Id, selected.Id);
        Assert.True(selected.Score > action.Score);
        Assert.Equal(action.Id, HighlightScorer.EnsureIdentity(action with { Id = Guid.Empty }).Id);
    }

    [Fact]
    public void CandidateBoundariesDoNotCutThroughNearbySpeech()
    {
        var input = new AnalysisInput(TimeSpan.FromSeconds(60), AnalysisMode.Balanced,
        [
            new FeatureEvent(FeatureKind.Speech, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), 0.8, "setup sentence"),
            new FeatureEvent(FeatureKind.GameAudioPeak, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12), 0.9, "payoff"),
            new FeatureEvent(FeatureKind.Speech, TimeSpan.FromSeconds(18), TimeSpan.FromSeconds(21), 0.8, "reaction sentence")
        ]);

        var candidate = Assert.Single(HighlightScorer.CreateCandidates(input));

        Assert.Equal(TimeSpan.FromSeconds(5), candidate.SourceIn);
        Assert.Equal(TimeSpan.FromSeconds(21), candidate.SourceOut);
        Assert.Contains(candidate.Reasons, reason => reason.Detail == "payoff");
    }

    [Fact]
    public void DraftDiversifiesRepeatedSignalTypesWhenScoresAreClose()
    {
        static HighlightCandidate Candidate(int second, double score, FeatureKind kind) => HighlightScorer.EnsureIdentity(new HighlightCandidate(
            TimeSpan.FromSeconds(second),
            TimeSpan.FromSeconds(second + 10),
            score,
            [new SelectionReason(kind, score, kind.ToString())]));
        var candidates = new[]
        {
            Candidate(0, 1.0, FeatureKind.GameAudioPeak),
            Candidate(120, 0.96, FeatureKind.GameAudioPeak),
            Candidate(240, 0.90, FeatureKind.Laughter)
        };

        var draft = HighlightScorer.BuildDraft(candidates, TimeSpan.FromSeconds(20));

        Assert.Contains(draft.Clips, clip => clip.Reasons[0].Kind == FeatureKind.GameAudioPeak);
        Assert.Contains(draft.Clips, clip => clip.Reasons[0].Kind == FeatureKind.Laughter);
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
