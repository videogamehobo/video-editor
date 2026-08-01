namespace HighlightForge.Media.Runtime;

public static class MediaPathSafety
{
    public static void RequireSeparateOutput(string sourcePath, string outputPath, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var source = Path.GetFullPath(sourcePath);
        var output = Path.GetFullPath(outputPath);
        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{operation} cannot overwrite the original recording. Choose a different output path.");
        }
    }
}
