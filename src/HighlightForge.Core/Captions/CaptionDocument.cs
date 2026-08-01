using System.Globalization;
using System.Text;

namespace HighlightForge.Core.Captions;

public sealed record CaptionWord(TimeSpan Start, TimeSpan End, string Text);

public sealed record CaptionCue(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    IReadOnlyList<CaptionWord>? Words = null);

public enum CaptionPlacement
{
    Bottom,
    Middle,
    Top
}

public sealed record CaptionStyleSettings(
    string FontName = "Inter",
    int FontSize = 42,
    string PrimaryColor = "#FFFFFF",
    string OutlineColor = "#101010",
    double OutlineSize = 3,
    CaptionPlacement Placement = CaptionPlacement.Bottom)
{
    public CaptionStyleSettings Validated()
    {
        if (string.IsNullOrWhiteSpace(FontName) || FontName.Length > 80)
        {
            throw new ArgumentException("Caption font name must contain 1 to 80 characters.", nameof(FontName));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(FontSize, 18);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FontSize, 96);
        ArgumentOutOfRangeException.ThrowIfLessThan(OutlineSize, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(OutlineSize, 8);
        ValidateColor(PrimaryColor, nameof(PrimaryColor));
        ValidateColor(OutlineColor, nameof(OutlineColor));
        return this with { FontName = FontName.Trim(), PrimaryColor = PrimaryColor.ToUpperInvariant(), OutlineColor = OutlineColor.ToUpperInvariant() };
    }

    private static void ValidateColor(string color, string parameterName)
    {
        if (color.Length == 7 && color[0] == '#' && color.AsSpan(1).ToArray().All(Uri.IsHexDigit)) return;
        throw new ArgumentException("Caption colors must use #RRGGBB notation.", parameterName);
    }
}

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
        // A manual edit changes the relationship between text and the original
        // recognition tokens, so stale word timing must not be retained.
        updated[index] = new CaptionCue(start, end, text.Trim());
        return updated;
    }

    public static IReadOnlyList<CaptionCue> GroupWords(
        IReadOnlyList<CaptionWord> words,
        TimeSpan? maximumDuration = null,
        int maximumCharacters = 42,
        TimeSpan? maximumGap = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        var durationLimit = maximumDuration ?? TimeSpan.FromSeconds(2.5);
        var gapLimit = maximumGap ?? TimeSpan.FromSeconds(0.65);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(durationLimit, TimeSpan.Zero, nameof(maximumDuration));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(gapLimit, TimeSpan.Zero, nameof(maximumGap));

        var ordered = words
            .Where(word => word.End > word.Start && !string.IsNullOrWhiteSpace(word.Text))
            .Select(word => word with { Text = word.Text.Trim() })
            .OrderBy(word => word.Start)
            .ToArray();
        if (ordered.Length == 0) return [];

        var result = new List<CaptionCue>();
        var current = new List<CaptionWord>();
        foreach (var word in ordered)
        {
            if (current.Count > 0)
            {
                var candidateLength = string.Join(' ', current.Select(item => item.Text).Append(word.Text)).Length;
                var candidateDuration = word.End - current[0].Start;
                var gap = word.Start - current[^1].End;
                if (candidateLength > maximumCharacters || candidateDuration > durationLimit || gap > gapLimit)
                {
                    result.Add(CreateCue(current));
                    current.Clear();
                }
            }
            current.Add(word);
        }
        if (current.Count > 0) result.Add(CreateCue(current));
        return result;

        static CaptionCue CreateCue(IReadOnlyList<CaptionWord> cueWords) => new(
            cueWords[0].Start,
            cueWords[^1].End,
            string.Join(' ', cueWords.Select(word => word.Text)),
            cueWords.ToArray());
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
