using HighlightForge.Core.Analysis;
using HighlightForge.Core.Domain;

namespace HighlightForge.Core.Voiceover;

public sealed record VoiceoverSuggestion(TimeSpan TimelinePosition, TimeSpan MaximumDuration, string WhyNarrationHelps, string TalkingPoint);
public sealed record VoiceoverTake(Guid Id, string AbsolutePath, TimeSpan Start, TimeSpan Duration, bool IsSelected);

public static class VoiceoverPlanner
{
    public static IReadOnlyList<VoiceoverSuggestion> Suggest(
        IReadOnlyList<HighlightCandidate> clips,
        IReadOnlyList<NarrativeSuggestion>? narrativeSuggestions = null)
    {
        var narrativeByCandidate = (narrativeSuggestions ?? [])
            .GroupBy(suggestion => suggestion.CandidateId)
            .ToDictionary(group => group.Key, group => group.First().TalkingPoint);
        return clips
            .Where(clip => !clip.Reasons.Any(reason => reason.Kind == FeatureKind.Speech))
            .Select(clip =>
            {
                clip = HighlightScorer.EnsureIdentity(clip);
                var strongest = clip.Reasons.Count == 0 ? null : clip.Reasons[0];
                var detail = strongest?.Detail ?? "the upcoming gameplay moment";
                var talkingPoint = narrativeByCandidate.GetValueOrDefault(clip.Id)
                    ?? $"Set up {detail} and explain what made this moment matter.";
                return new VoiceoverSuggestion(
                    clip.SourceIn,
                    TimeSpan.FromSeconds(Math.Min(12, Math.Max(5, clip.Duration.TotalSeconds / 2))),
                    "This high-interest moment has no detected commentary, so a brief explanation can give viewers context.",
                    talkingPoint);
            })
            .ToArray();
    }

    public static IReadOnlyList<VoiceoverSuggestion> SuggestForTimeline(
        IReadOnlyList<HighlightCandidate> clips,
        IReadOnlyList<TimelineClip> timeline,
        Guid sourceId,
        IReadOnlyList<NarrativeSuggestion>? narrativeSuggestions = null)
    {
        var suggestions = Suggest(clips, narrativeSuggestions);
        return timeline
            .Where(clip => clip.SourceId == sourceId)
            .SelectMany(clip => suggestions
                .Where(suggestion => suggestion.TimelinePosition >= clip.SourceIn && suggestion.TimelinePosition < clip.SourceOut)
                .Select(suggestion => suggestion with
                {
                    TimelinePosition = clip.TimelineIn + suggestion.TimelinePosition - clip.SourceIn,
                    MaximumDuration = TimeSpan.FromTicks(Math.Min(
                        suggestion.MaximumDuration.Ticks,
                        (clip.SourceOut - suggestion.TimelinePosition).Ticks))
                }))
            .Where(suggestion => suggestion.MaximumDuration > TimeSpan.Zero)
            .OrderBy(suggestion => suggestion.TimelinePosition)
            .ToArray();
    }
}
