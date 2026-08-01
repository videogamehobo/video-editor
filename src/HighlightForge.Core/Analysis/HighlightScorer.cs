namespace HighlightForge.Core.Analysis;

using System.Security.Cryptography;
using System.Text;
using HighlightForge.Core.Preferences;

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
        var speech = input.Features.Where(feature => feature.Kind == FeatureKind.Speech).ToArray();
        var seedFeatures = input.Features.Any(feature => feature.Kind != FeatureKind.Speech)
            ? input.Features.Where(feature => feature.Kind != FeatureKind.Speech)
            : input.Features;
        var eventWindows = seedFeatures
            .Where(feature => feature.Confidence >= 0.35 && feature.End > feature.Start)
            .OrderBy(feature => feature.Start)
            .Select(feature =>
            {
                var start = Clamp(feature.Start - TimeSpan.FromSeconds(4), TimeSpan.Zero, input.SourceDuration);
                var end = Clamp(feature.End + TimeSpan.FromSeconds(7), TimeSpan.Zero, input.SourceDuration);
                return new EventWindow(
                    AlignStartToSpeech(start, speech),
                    AlignEndToSpeech(end, speech, input.SourceDuration),
                    [feature]);
            })
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

        return merged
            .Select(window => window with
            {
                Events = window.Events
                    .Concat(speech.Where(item => item.End > window.Start && item.Start < window.End))
                    .Distinct()
                    .ToArray()
            })
            .Select(window => ToCandidate(window, input.Mode))
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();
    }

    public static HighlightDraft BuildDraft(IReadOnlyList<HighlightCandidate> candidates, TimeSpan targetDuration)
    {
        var selected = new List<HighlightCandidate>();
        var remainingCandidates = candidates.ToList();
        var kindCounts = new Dictionary<FeatureKind, int>();
        while (remainingCandidates.Count > 0)
        {
            var candidate = remainingCandidates
                .OrderByDescending(item => DiversityAdjustedScore(item, selected, kindCounts))
                .First();
            remainingCandidates.Remove(candidate);
            if (selected.Aggregate(TimeSpan.Zero, (total, clip) => total + clip.Duration) + candidate.Duration > targetDuration) continue;
            if (selected.Any(clip => OverlapsWithPadding(clip, candidate, TimeSpan.FromSeconds(18)))) continue;
            selected.Add(candidate);
            foreach (var kind in candidate.Reasons.Select(reason => reason.Kind).Distinct())
            {
                kindCounts[kind] = kindCounts.GetValueOrDefault(kind) + 1;
            }
        }
        if (selected.Count == 0) return new HighlightDraft([], TimeSpan.Zero);
        var hook = selected.MaxBy(candidate => candidate.Score)!;
        var remaining = selected.Where(candidate => candidate.Id != hook.Id || candidate != hook).ToList();
        var ending = remaining.Count == 0 ? null : remaining.MaxBy(candidate => candidate.Score);
        if (ending is not null) remaining.Remove(ending);
        var assembled = new List<HighlightCandidate> { hook };
        assembled.AddRange(remaining.OrderBy(candidate => candidate.SourceIn));
        if (ending is not null) assembled.Add(ending);
        return new HighlightDraft(assembled, assembled.Aggregate(TimeSpan.Zero, (total, clip) => total + clip.Duration));
    }

    public static IReadOnlyList<HighlightCandidate> Rerank(
        IReadOnlyList<HighlightCandidate> candidates,
        CreatorPreferences preferences)
    {
        ValidateWeight(preferences.FunnyWeight, nameof(preferences.FunnyWeight));
        ValidateWeight(preferences.ActionWeight, nameof(preferences.ActionWeight));
        ValidateWeight(preferences.StoryWeight, nameof(preferences.StoryWeight));
        var accepted = preferences.AcceptedCandidateIds ?? new HashSet<Guid>();
        var rejected = preferences.RejectedCandidateIds ?? new HashSet<Guid>();
        return candidates
            .Select(EnsureIdentity)
            .Where(candidate => !rejected.Contains(candidate.Id))
            .Select(candidate => candidate with
            {
                Score = Math.Round(candidate.Reasons.Sum(reason => reason.Contribution * PreferenceWeight(reason.Kind, preferences)) /
                    Math.Sqrt(Math.Max(1, candidate.Duration.TotalSeconds / 20)) + (accepted.Contains(candidate.Id) ? 0.35 : 0), 3)
            })
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();
    }

    public static HighlightCandidate EnsureIdentity(HighlightCandidate candidate) =>
        candidate.Id == Guid.Empty ? candidate with { Id = CreateCandidateId(candidate) } : candidate;

    private static HighlightCandidate ToCandidate(EventWindow window, AnalysisMode mode)
    {
        var modeMultiplier = mode switch { AnalysisMode.Fast => 0.85, AnalysisMode.Deep => 1.1, _ => 1.0 };
        var reasons = window.Events
            .Select(feature => new SelectionReason(feature.Kind, Math.Round(Weights[feature.Kind] * feature.Confidence * modeMultiplier, 3), feature.Detail))
            .OrderByDescending(reason => reason.Contribution)
            .ToArray();
        var score = reasons.Sum(reason => reason.Contribution) / Math.Sqrt(Math.Max(1, window.End.Subtract(window.Start).TotalSeconds / 20));
        return EnsureIdentity(new HighlightCandidate(window.Start, window.End, Math.Round(score, 3), reasons));
    }

    private static Guid CreateCandidateId(HighlightCandidate candidate)
    {
        var reasons = string.Join('|', candidate.Reasons.Select(reason => $"{reason.Kind}:{reason.Detail}"));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{candidate.SourceIn.Ticks}:{candidate.SourceOut.Ticks}:{reasons}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static double PreferenceWeight(FeatureKind kind, CreatorPreferences preferences) => kind switch
    {
        FeatureKind.Laughter => preferences.FunnyWeight,
        FeatureKind.GameAudioPeak or FeatureKind.Motion or FeatureKind.VocalExcitement => preferences.ActionWeight,
        FeatureKind.Speech or FeatureKind.OnScreenText or FeatureKind.VisualNovelty => preferences.StoryWeight,
        _ => 1
    };

    private static void ValidateWeight(double weight, string name)
    {
        if (!double.IsFinite(weight) || weight is < 0.5 or > 2) throw new ArgumentOutOfRangeException(name, "Style weights must be between 0.5 and 2.0.");
    }

    private static bool OverlapsWithPadding(HighlightCandidate left, HighlightCandidate right, TimeSpan padding) =>
        left.SourceIn - padding < right.SourceOut && right.SourceIn - padding < left.SourceOut;

    private static double DiversityAdjustedScore(
        HighlightCandidate candidate,
        IReadOnlyList<HighlightCandidate> selected,
        IReadOnlyDictionary<FeatureKind, int> kindCounts)
    {
        var repeatedKinds = candidate.Reasons.Select(reason => reason.Kind).Distinct().Sum(kind => kindCounts.GetValueOrDefault(kind));
        var temporalPenalty = selected.Any(existing => Math.Abs((existing.SourceIn - candidate.SourceIn).TotalSeconds) < 90) ? 0.12 : 0;
        return candidate.Score - (repeatedKinds * 0.16) - temporalPenalty;
    }

    private static TimeSpan AlignStartToSpeech(TimeSpan boundary, IReadOnlyList<FeatureEvent> speech)
    {
        var containing = speech.FirstOrDefault(item => item.Start < boundary && item.End > boundary);
        if (containing is not null) return containing.Start;
        var nearby = speech.Where(item => item.Start <= boundary && boundary - item.End <= TimeSpan.FromSeconds(1.25))
            .OrderByDescending(item => item.End)
            .FirstOrDefault();
        return nearby?.Start ?? boundary;
    }

    private static TimeSpan AlignEndToSpeech(TimeSpan boundary, IReadOnlyList<FeatureEvent> speech, TimeSpan sourceDuration)
    {
        var containing = speech.FirstOrDefault(item => item.Start < boundary && item.End > boundary);
        if (containing is not null) return Clamp(containing.End, TimeSpan.Zero, sourceDuration);
        var nearby = speech.Where(item => item.Start >= boundary && item.Start - boundary <= TimeSpan.FromSeconds(1.25))
            .OrderBy(item => item.Start)
            .FirstOrDefault();
        return nearby is null ? boundary : Clamp(nearby.End, TimeSpan.Zero, sourceDuration);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) => value < minimum ? minimum : value > maximum ? maximum : value;
    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private sealed record EventWindow(TimeSpan Start, TimeSpan End, IReadOnlyList<FeatureEvent> Events);
}
