using HighlightForge.Core.Domain;

namespace HighlightForge.Core.Timeline;

public static class TimelineEditor
{
    public static IReadOnlyList<TimelineClip> Append(IReadOnlyList<TimelineClip> clips, Guid sourceId, TimeSpan sourceIn, TimeSpan sourceOut)
    {
        if (sourceOut <= sourceIn) throw new ArgumentOutOfRangeException(nameof(sourceOut), "A clip must have a positive duration.");
        var timelineIn = clips.Count == 0 ? TimeSpan.Zero : clips.Max(clip => clip.TimelineIn + (clip.SourceOut - clip.SourceIn));
        return clips.Append(new TimelineClip(Guid.NewGuid(), sourceId, sourceIn, sourceOut, timelineIn)).ToArray();
    }

    public static IReadOnlyList<TimelineClip> Trim(IReadOnlyList<TimelineClip> clips, Guid clipId, TimeSpan sourceIn, TimeSpan sourceOut)
    {
        if (sourceOut <= sourceIn) throw new ArgumentOutOfRangeException(nameof(sourceOut), "A clip must have a positive duration.");
        return Reflow(clips.Select(clip => clip.Id == clipId ? clip with { SourceIn = sourceIn, SourceOut = sourceOut } : clip));
    }

    public static IReadOnlyList<TimelineClip> Split(IReadOnlyList<TimelineClip> clips, Guid clipId, TimeSpan sourceTime)
    {
        var original = clips.Single(clip => clip.Id == clipId);
        if (sourceTime <= original.SourceIn || sourceTime >= original.SourceOut) throw new ArgumentOutOfRangeException(nameof(sourceTime));
        var replacement = clips.SelectMany(clip => clip.Id == clipId
            ? new[] { clip with { SourceOut = sourceTime }, new TimelineClip(Guid.NewGuid(), clip.SourceId, sourceTime, clip.SourceOut, clip.TimelineIn) }
            : new[] { clip });
        return Reflow(replacement);
    }

    public static IReadOnlyList<TimelineClip> DeleteWithRipple(IReadOnlyList<TimelineClip> clips, Guid clipId) =>
        Reflow(clips.Where(clip => clip.Id != clipId));

    public static IReadOnlyList<TimelineClip> Move(IReadOnlyList<TimelineClip> clips, Guid clipId, int destinationIndex)
    {
        var ordered = clips.OrderBy(clip => clip.TimelineIn).ToList();
        var clip = ordered.Single(item => item.Id == clipId);
        ordered.Remove(clip);
        ordered.Insert(Math.Clamp(destinationIndex, 0, ordered.Count), clip);
        return Reflow(ordered);
    }

    private static TimelineClip[] Reflow(IEnumerable<TimelineClip> clips)
    {
        var cursor = TimeSpan.Zero;
        return clips.Select(clip =>
        {
            var positioned = clip with { TimelineIn = cursor };
            cursor += clip.SourceOut - clip.SourceIn;
            return positioned;
        }).ToArray();
    }
}
