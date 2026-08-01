using HighlightForge.Core.Domain;

namespace HighlightForge.Core.Captions;

public static class CaptionTimelineMapper
{
    public static IReadOnlyList<CaptionCue> MapToTimeline(
        IReadOnlyList<CaptionCue> sourceCues,
        IReadOnlyList<TimelineClip> timeline,
        Guid sourceId)
    {
        var mapped = new List<CaptionCue>();
        foreach (var clip in timeline.Where(clip => clip.SourceId == sourceId))
        {
            foreach (var cue in sourceCues.Where(cue => cue.End > clip.SourceIn && cue.Start < clip.SourceOut))
            {
                var sourceStart = cue.Start < clip.SourceIn ? clip.SourceIn : cue.Start;
                var sourceEnd = cue.End > clip.SourceOut ? clip.SourceOut : cue.End;
                var timelineStart = clip.TimelineIn + sourceStart - clip.SourceIn;
                var timelineEnd = clip.TimelineIn + sourceEnd - clip.SourceIn;
                if (timelineEnd <= timelineStart) continue;
                var mappedWords = cue.Words?
                    .Where(word => word.End > clip.SourceIn && word.Start < clip.SourceOut)
                    .Select(word =>
                    {
                        var wordStart = word.Start < clip.SourceIn ? clip.SourceIn : word.Start;
                        var wordEnd = word.End > clip.SourceOut ? clip.SourceOut : word.End;
                        return new CaptionWord(
                            clip.TimelineIn + wordStart - clip.SourceIn,
                            clip.TimelineIn + wordEnd - clip.SourceIn,
                            word.Text);
                    })
                    .Where(word => word.End > word.Start)
                    .ToArray();
                mapped.Add(new CaptionCue(timelineStart, timelineEnd, cue.Text, mappedWords));
            }
        }
        return mapped.OrderBy(cue => cue.Start).ToArray();
    }
}
