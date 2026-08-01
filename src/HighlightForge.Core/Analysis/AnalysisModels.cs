namespace HighlightForge.Core.Analysis;

public enum AnalysisMode
{
    Fast,
    Balanced,
    Deep
}

public enum FeatureKind
{
    Speech,
    Laughter,
    VocalExcitement,
    GameAudioPeak,
    SceneChange,
    Motion,
    VisualNovelty,
    OnScreenText
}

public sealed record FeatureEvent(FeatureKind Kind, TimeSpan Start, TimeSpan End, double Confidence, string Detail)
{
    public TimeSpan Duration => End - Start;
}

public sealed record SelectionReason(FeatureKind Kind, double Contribution, string Detail);

public sealed record HighlightCandidate(TimeSpan SourceIn, TimeSpan SourceOut, double Score, IReadOnlyList<SelectionReason> Reasons, Guid Id = default)
{
    public TimeSpan Duration => SourceOut - SourceIn;
}

public sealed record HighlightDraft(IReadOnlyList<HighlightCandidate> Clips, TimeSpan TotalDuration);

public sealed record AnalysisInput(TimeSpan SourceDuration, AnalysisMode Mode, IReadOnlyList<FeatureEvent> Features);
