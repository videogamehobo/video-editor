namespace HighlightForge.Core.Analysis;

public static class HighlightScorer
{
    private static readonly Dictionary<FeatureKind, double> Weights = new()
    {
        [FeatureKind.Laughter] = 1.00,
        [FeatureKind.VocalExcitement] = 0.90,
        [FeatureKind.GameAudioPeak] = 0.72,
        [FeatureKind.VisualNovelty] = 0.68,
        [FeatureKind.OnScreenText] = 0.50,
        [FeatureKind.SceneChange] = 0.32,
        [FeatureKind.Motion] = 0.45,
        [FeatureKind.Speech] = 0.28
    };

    public static IReadOnlyList<HighlightCandidate> CreateCandidates(AnalysisInput input)
    {
        var eventWindows = input.Features
            .Where(feature => feature.Confidence >= 0.35 && feature.End > feature.Start)
            .OrderBy(feature => feature.Start)
            .Select(feature => new EventWindow(
                Clamp(feature.Start - TimeSpan.FromSeconds(4), TimeSpan.Zero, input.SourceDuration),
                Clamp(feature.End + TimeSpan.FromSeconds(7), TimeSpan.Zero, input.SourceDuration),
                [feature]))
            .ToList();
        if (eventWindows.Count == 0) return [];

        var merged = new List<EventWindow>();
        foreach (var candidate in eventWindows)
        {
            if (merged.LastOrDefault() is { } previous && candidate.Start <= previous.End + TimeSpan.FromSeconds(3))
            {
                merged[^1] = previous with { End = Max(previous.End, candidate.End), Events = previous.Events.Concat(candidate.Events).ToArray() };
            }
            else
            {
                merged.Add(candidate);
            }
        }

        return merged.Select(window => ToCandidate(window, input.Mode)).OrderByDescending(candidate => candidate.Score).ToArray();
    }

    public static HighlightDraft BuildDraft(IReadOnlyList<HighlightCandidate> candidates, TimeSpan targetDuration)
    {
        var selected = new List<HighlightCandidate>();
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Score))
        {
            if (selected.Aggregate(TimeSpan.Zero, (total, clip) => total + clip.Duration) + candidate.Duration > targetDuration) continue;
            if (selected.Any(clip => OverlapsWithPadding(clip, candidate, TimeSpan.FromSeconds(18)))) continue;
            selected.Add(candidate);
        }
        var chronological = selected.OrderBy(clip => clip.SourceIn).ToArray();
        return new HighlightDraft(chronological, chronological.Aggregate(TimeSpan.Zero, (total, clip) => total + clip.Duration));
    }

    private static HighlightCandidate ToCandidate(EventWindow window, AnalysisMode mode)
    {
        var modeMultiplier = mode switch { AnalysisMode.Fast => 0.85, AnalysisMode.Deep => 1.1, _ => 1.0 };
        var reasons = window.Events
            .Select(feature => new SelectionReason(feature.Kind, Math.Round(Weights[feature.Kind] * feature.Confidence * modeMultiplier, 3), feature.Detail))
            .OrderByDescending(reason => reason.Contribution)
            .ToArray();
        var score = reasons.Sum(reason => reason.Contribution) / Math.Sqrt(Math.Max(1, window.End.Subtract(window.Start).TotalSeconds / 20));
        return new HighlightCandidate(window.Start, window.End, Math.Round(score, 3), reasons);
    }

    private static bool OverlapsWithPadding(HighlightCandidate left, HighlightCandidate right, TimeSpan padding) =>
        left.SourceIn - padding < right.SourceOut && right.SourceIn - padding < left.SourceOut;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) => value < minimum ? minimum : value > maximum ? maximum : value;
    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private sealed record EventWindow(TimeSpan Start, TimeSpan End, IReadOnlyList<FeatureEvent> Events);
}
