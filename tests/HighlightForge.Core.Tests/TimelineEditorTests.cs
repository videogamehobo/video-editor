using HighlightForge.Core.Domain;
using HighlightForge.Core.Timeline;

namespace HighlightForge.Core.Tests;

public sealed class TimelineEditorTests
{
    [Fact]
    public void SplitAndDeleteWithRippleKeepTimelineContiguous()
    {
        var source = Guid.NewGuid();
        var clips = TimelineEditor.Append([], source, TimeSpan.Zero, TimeSpan.FromSeconds(20));
        var split = TimelineEditor.Split(clips, clips[0].Id, TimeSpan.FromSeconds(8));
        var edited = TimelineEditor.DeleteWithRipple(split, split[0].Id);

        var remaining = Assert.Single(edited);
        Assert.Equal(TimeSpan.Zero, remaining.TimelineIn);
        Assert.Equal(TimeSpan.FromSeconds(8), remaining.SourceIn);
    }

    [Fact]
    public void MoveChangesClipOrderAndReflowsPositions()
    {
        var sourceId = Guid.NewGuid();
        var first = new TimelineClip(Guid.NewGuid(), sourceId, TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.Zero);
        var second = new TimelineClip(Guid.NewGuid(), sourceId, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5));

        var moved = TimelineEditor.Move([first, second], second.Id, 0);

        Assert.Equal(second.Id, moved[0].Id);
        Assert.Equal(TimeSpan.Zero, moved[0].TimelineIn);
        Assert.Equal(first.Id, moved[1].Id);
        Assert.Equal(TimeSpan.FromSeconds(10), moved[1].TimelineIn);
    }

    [Fact]
    public void ClipAdjustmentsAreNonDestructiveAndLockedClipsRejectEdits()
    {
        var sourceId = Guid.NewGuid();
        var original = new TimelineClip(Guid.NewGuid(), sourceId, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.Zero);

        var adjusted = TimelineEditor.SetAdjustments(
            [original],
            original.Id,
            gainDb: -3.5,
            fadeIn: TimeSpan.FromSeconds(0.5),
            fadeOut: TimeSpan.FromSeconds(1),
            punchZoom: true,
            cropScale: 1.08,
            reframeX: 0.7,
            reframeY: 0.4);
        var locked = TimelineEditor.ToggleLock(adjusted, original.Id);

        Assert.Equal(original.SourceIn, locked[0].SourceIn);
        Assert.Equal(original.SourceOut, locked[0].SourceOut);
        Assert.Equal(-3.5, locked[0].GainDb);
        Assert.True(locked[0].PunchZoom);
        Assert.True(locked[0].IsLocked);
        Assert.Throws<InvalidOperationException>(() => TimelineEditor.DeleteWithRipple(locked, original.Id));
        Assert.Throws<InvalidOperationException>(() => TimelineEditor.Trim(locked, original.Id, TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(19)));
    }

    [Fact]
    public void ClipAdjustmentsRejectUnsafeRanges()
    {
        var clip = new TimelineClip(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(() => TimelineEditor.SetAdjustments([clip], clip.Id, 13, TimeSpan.Zero, TimeSpan.Zero, false, 1, 0.5, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimelineEditor.SetAdjustments([clip], clip.Id, 0, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), false, 1, 0.5, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimelineEditor.SetAdjustments([clip], clip.Id, 0, TimeSpan.Zero, TimeSpan.Zero, false, 1.3, 0.5, 0.5));
    }

    [Fact]
    public void TimelineValidationChecksSourceBoundsContiguityAndAdjustmentRanges()
    {
        var source = new MediaSource(Guid.NewGuid(), "source.mkv", TimeSpan.FromSeconds(20), 1920, 1080, 60, []);
        var valid = new TimelineClip(Guid.NewGuid(), source.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.Zero);
        var invalid = new TimelineClip(Guid.NewGuid(), source.Id, TimeSpan.Zero, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(8), GainDb: 20);

        Assert.True(TimelineEditor.Validate([valid], [source]).IsValid);
        var result = TimelineEditor.Validate([valid, invalid], [source]);
        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, problem => problem.Contains("bounds", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem => problem.Contains("cursor", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem => problem.Contains("gain", StringComparison.Ordinal));
    }
}
