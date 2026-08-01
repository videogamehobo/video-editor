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
}
