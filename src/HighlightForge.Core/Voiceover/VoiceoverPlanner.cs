using HighlightForge.Core.Analysis;

namespace HighlightForge.Core.Voiceover;

public sealed record VoiceoverSuggestion(TimeSpan TimelinePosition, TimeSpan MaximumDuration, string WhyNarrationHelps, string TalkingPoint);
public sealed record VoiceoverTake(Guid Id, string AbsolutePath, TimeSpan Start, TimeSpan Duration, bool IsSelected);

public static class VoiceoverPlanner
{
    public static IReadOnlyList<VoiceoverSuggestion> Suggest(IReadOnlyList<HighlightCandidate> clips)
    {
        return clips
            .Where(clip => !clip.Reasons.Any(reason => reason.Kind == FeatureKind.Speech))
            .Select(clip =>
            {
                var strongest = clip.Reasons.Count == 0 ? null : clip.Reasons[0];
                var detail = strongest?.Detail ?? "the upcoming gameplay moment";
                return new VoiceoverSuggestion(
                    clip.SourceIn,
                    TimeSpan.FromSeconds(Math.Min(12, Math.Max(5, clip.Duration.TotalSeconds / 2))),
                    "This high-interest moment has no detected commentary, so a brief explanation can give viewers context.",
                    $"Set up {detail} and explain what made this moment matter.");
            })
            .ToArray();
    }
}
