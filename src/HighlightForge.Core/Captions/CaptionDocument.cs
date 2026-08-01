using System.Globalization;
using System.Text;

namespace HighlightForge.Core.Captions;

public sealed record CaptionCue(TimeSpan Start, TimeSpan End, string Text);

public static class CaptionDocument
{
    public static IReadOnlyList<CaptionCue> UpdateCue(
        IReadOnlyList<CaptionCue> cues,
        int index,
        TimeSpan start,
        TimeSpan end,
        string text)
    {
        if (index < 0 || index >= cues.Count) throw new ArgumentOutOfRangeException(nameof(index));
        ArgumentOutOfRangeException.ThrowIfLessThan(start, TimeSpan.Zero);
        if (end <= start) throw new ArgumentException("A caption end time must be after its start time.", nameof(end));
        var updated = cues.ToArray();
        updated[index] = new CaptionCue(start, end, text.Trim());
        return updated;
    }

    public static string ToSrt(IReadOnlyList<CaptionCue> cues)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            builder.AppendLine((index + 1).ToString(CultureInfo.InvariantCulture));
            builder.Append(FormatSrt(cue.Start)).Append(" --> ").AppendLine(FormatSrt(cue.End));
            builder.AppendLine(cue.Text);
            builder.AppendLine();
        }
        return builder.ToString();
    }

    public static string ToWebVtt(IReadOnlyList<CaptionCue> cues)
    {
        var builder = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in cues)
        {
            builder.Append(FormatVtt(cue.Start)).Append(" --> ").AppendLine(FormatVtt(cue.End));
            builder.AppendLine(cue.Text);
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string FormatSrt(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
    private static string FormatVtt(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
}
