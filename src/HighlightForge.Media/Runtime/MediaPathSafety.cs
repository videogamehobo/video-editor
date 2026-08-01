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

    public static string RequireOutputWithinDirectory(string allowedDirectory, string outputPath, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var root = Path.GetFullPath(allowedDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var output = Path.GetFullPath(outputPath);
        if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{operation} must write inside '{allowedDirectory}', never beside or over an original recording.");
        }

        return output;
    }
}
