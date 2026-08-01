using HighlightForge.Core.Domain;

namespace HighlightForge.Core.Timeline;

public sealed record TimelineValidation(bool IsValid, IReadOnlyList<string> Problems);

public static class TimelineEditor
{
    public static TimelineValidation Validate(IReadOnlyList<TimelineClip> clips, IReadOnlyList<MediaSource> sources)
    {
        var problems = new List<string>();
        var sourceById = sources.ToDictionary(source => source.Id);
        if (clips.Select(clip => clip.Id).Distinct().Count() != clips.Count) problems.Add("Timeline clip IDs must be unique.");
        var cursor = TimeSpan.Zero;
        foreach (var clip in clips.OrderBy(clip => clip.TimelineIn))
        {
            if (!sourceById.TryGetValue(clip.SourceId, out var source)) problems.Add($"Clip {clip.Id} references a missing source.");
            else if (clip.SourceIn < TimeSpan.Zero || clip.SourceOut <= clip.SourceIn || clip.SourceOut > source.Duration) problems.Add($"Clip {clip.Id} has invalid source bounds.");
            if (clip.TimelineIn != cursor) problems.Add($"Clip {clip.Id} does not begin at the ripple timeline cursor.");
            if (!double.IsFinite(clip.GainDb) || clip.GainDb is < -24 or > 12) problems.Add($"Clip {clip.Id} has invalid gain.");
            if (clip.FadeIn < TimeSpan.Zero || clip.FadeOut < TimeSpan.Zero || clip.FadeIn + clip.FadeOut > clip.SourceOut - clip.SourceIn) problems.Add($"Clip {clip.Id} has invalid fades.");
            if (!double.IsFinite(clip.CropScale) || clip.CropScale is < 1 or > 1.2 ||
                !double.IsFinite(clip.ReframeX) || clip.ReframeX is < 0 or > 1 ||
                !double.IsFinite(clip.ReframeY) || clip.ReframeY is < 0 or > 1)
            {
                problems.Add($"Clip {clip.Id} has invalid reframing values.");
            }
            cursor += clip.SourceOut - clip.SourceIn;
        }
        return new TimelineValidation(problems.Count == 0, problems);
    }

    public static IReadOnlyList<TimelineClip> Append(IReadOnlyList<TimelineClip> clips, Guid sourceId, TimeSpan sourceIn, TimeSpan sourceOut)
    {
        if (sourceOut <= sourceIn) throw new ArgumentOutOfRangeException(nameof(sourceOut), "A clip must have a positive duration.");
        var timelineIn = clips.Count == 0 ? TimeSpan.Zero : clips.Max(clip => clip.TimelineIn + (clip.SourceOut - clip.SourceIn));
        return clips.Append(new TimelineClip(Guid.NewGuid(), sourceId, sourceIn, sourceOut, timelineIn)).ToArray();
    }

    public static IReadOnlyList<TimelineClip> Trim(IReadOnlyList<TimelineClip> clips, Guid clipId, TimeSpan sourceIn, TimeSpan sourceOut)
    {
        EnsureEditable(clips, clipId);
        if (sourceOut <= sourceIn) throw new ArgumentOutOfRangeException(nameof(sourceOut), "A clip must have a positive duration.");
        var original = clips.Single(clip => clip.Id == clipId);
        if (sourceIn < original.SourceIn || sourceIn >= original.SourceOut || sourceOut <= original.SourceIn || sourceOut > original.SourceOut)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIn), "Trim points must remain inside the source clip.");
        }
        return Reflow(clips.Select(clip => clip.Id == clipId ? clip with { SourceIn = sourceIn, SourceOut = sourceOut } : clip));
    }

    public static IReadOnlyList<TimelineClip> Split(IReadOnlyList<TimelineClip> clips, Guid clipId, TimeSpan sourceTime)
    {
        var original = clips.Single(clip => clip.Id == clipId);
        if (original.IsLocked) throw new InvalidOperationException("Unlock the clip before editing it.");
        if (sourceTime <= original.SourceIn || sourceTime >= original.SourceOut) throw new ArgumentOutOfRangeException(nameof(sourceTime));
        var replacement = clips.SelectMany(clip => clip.Id == clipId
            ? new[] { clip with { SourceOut = sourceTime }, new TimelineClip(Guid.NewGuid(), clip.SourceId, sourceTime, clip.SourceOut, clip.TimelineIn) }
            : new[] { clip });
        return Reflow(replacement);
    }

    public static IReadOnlyList<TimelineClip> DeleteWithRipple(IReadOnlyList<TimelineClip> clips, Guid clipId)
    {
        EnsureEditable(clips, clipId);
        return Reflow(clips.Where(clip => clip.Id != clipId));
    }

    public static IReadOnlyList<TimelineClip> Move(IReadOnlyList<TimelineClip> clips, Guid clipId, int destinationIndex)
    {
        var ordered = clips.OrderBy(clip => clip.TimelineIn).ToList();
        var clip = ordered.Single(item => item.Id == clipId);
        if (clip.IsLocked) throw new InvalidOperationException("Unlock the clip before editing it.");
        ordered.Remove(clip);
        ordered.Insert(Math.Clamp(destinationIndex, 0, ordered.Count), clip);
        return Reflow(ordered);
    }

    public static IReadOnlyList<TimelineClip> SetAdjustments(
        IReadOnlyList<TimelineClip> clips,
        Guid clipId,
        double gainDb,
        TimeSpan fadeIn,
        TimeSpan fadeOut,
        bool punchZoom,
        double cropScale,
        double reframeX,
        double reframeY)
    {
        EnsureEditable(clips, clipId);
        ArgumentOutOfRangeException.ThrowIfLessThan(gainDb, -24);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(gainDb, 12);
        ArgumentOutOfRangeException.ThrowIfLessThan(fadeIn, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(fadeOut, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(cropScale, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cropScale, 1.2);
        ArgumentOutOfRangeException.ThrowIfLessThan(reframeX, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reframeX, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(reframeY, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reframeY, 1);
        var clip = clips.Single(item => item.Id == clipId);
        if (fadeIn + fadeOut > clip.SourceOut - clip.SourceIn)
        {
            throw new ArgumentOutOfRangeException(nameof(fadeOut), "Combined fades cannot exceed the clip duration.");
        }
        return clips.Select(item => item.Id == clipId
            ? item with
            {
                GainDb = gainDb,
                FadeIn = fadeIn,
                FadeOut = fadeOut,
                PunchZoom = punchZoom,
                CropScale = cropScale,
                ReframeX = reframeX,
                ReframeY = reframeY
            }
            : item).ToArray();
    }

    public static IReadOnlyList<TimelineClip> ToggleLock(IReadOnlyList<TimelineClip> clips, Guid clipId) =>
        clips.Select(clip => clip.Id == clipId ? clip with { IsLocked = !clip.IsLocked } : clip).ToArray();

    public static IReadOnlyList<TimelineClip> Normalize(IEnumerable<TimelineClip> clips) => Reflow(clips);

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

    private static void EnsureEditable(IReadOnlyList<TimelineClip> clips, Guid clipId)
    {
        if (clips.Single(clip => clip.Id == clipId).IsLocked) throw new InvalidOperationException("Unlock the clip before editing it.");
    }
}
