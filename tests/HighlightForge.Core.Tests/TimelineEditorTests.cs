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
}
