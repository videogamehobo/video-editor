using System.Text;
using HighlightForge.Core.Captions;
using HighlightForge.Core.Domain;

namespace HighlightForge.Core.Export;

public sealed record ChapterSuggestion(TimeSpan Start, string Title);
public sealed record CreatorMetadataSuggestion(string Title, string Description, IReadOnlyList<ChapterSuggestion> Chapters);

public static class CreatorMetadataSuggestions
{
    public static CreatorMetadataSuggestion Create(ProjectDocument project, IReadOnlyList<CaptionCue> sourceCaptions)
    {
        var firstSourceId = project.Sources.Count == 0 ? Guid.Empty : project.Sources[0].Id;
        return Create(project, new Dictionary<Guid, IReadOnlyList<CaptionCue>> { [firstSourceId] = sourceCaptions });
    }

    public static CreatorMetadataSuggestion Create(
        ProjectDocument project,
        IReadOnlyDictionary<Guid, IReadOnlyList<CaptionCue>> captionsBySource)
    {
        var projectName = string.IsNullOrWhiteSpace(project.Name) ? "Gaming Highlights" : project.Name.Trim();
        var title = projectName.Contains("highlight", StringComparison.OrdinalIgnoreCase)
            ? projectName
            : $"{projectName} Highlights";
        var chapters = project.Timeline.OrderBy(clip => clip.TimelineIn).Select((clip, index) =>
        {
            captionsBySource.TryGetValue(clip.SourceId, out var sourceCaptions);
            var caption = sourceCaptions?.FirstOrDefault(cue => cue.Start < clip.SourceOut && cue.End > clip.SourceIn);
            var detail = caption is null ? $"Highlight {index + 1}" : TrimTitle(caption.Text);
            return new ChapterSuggestion(clip.TimelineIn, detail);
        }).ToArray();
        var description = $"A locally edited highlight reel from {projectName}. " +
            $"{project.Timeline.Count} selected moment{(project.Timeline.Count == 1 ? string.Empty : "s")}; captions and audio were processed on-device with HighlightForge.";
        return new CreatorMetadataSuggestion(title, description, chapters);
    }

    public static string ToChapterText(IReadOnlyList<ChapterSuggestion> chapters)
    {
        var builder = new StringBuilder();
        foreach (var chapter in chapters)
        {
            builder.Append(FormatTime(chapter.Start)).Append(' ').AppendLine(chapter.Title);
        }
        return builder.ToString();
    }

    private static string TrimTitle(string text)
    {
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 60 ? normalized : $"{normalized[..57]}…";
    }

    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
}
