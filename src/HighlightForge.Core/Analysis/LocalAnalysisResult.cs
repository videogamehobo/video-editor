namespace HighlightForge.Core.Analysis;

public sealed record LocalAnalysisResult(
    Guid JobId,
    Guid SourceId,
    AnalysisMode Mode,
    IReadOnlyList<FeatureEvent> Features,
    IReadOnlyList<HighlightCandidate> RankedCandidates,
    HighlightDraft Draft,
    DateTimeOffset CompletedUtc);

public sealed record AnalysisProgress(string Stage, double Progress, string Detail);
