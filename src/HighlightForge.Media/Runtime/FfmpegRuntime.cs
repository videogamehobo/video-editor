namespace HighlightForge.Media.Runtime;

public static class FfmpegRuntime
{
    public static string ResolveFfprobePath() => Resolve("ffprobe.exe", Environment.GetEnvironmentVariable("HIGHLIGHTFORGE_FFPROBE_PATH"));
    public static string ResolveFfmpegPath() => Resolve("ffmpeg.exe", Environment.GetEnvironmentVariable("HIGHLIGHTFORGE_FFMPEG_PATH"));

    public static string Resolve(string executableName, string? explicitPath, IEnumerable<string>? additionalCandidates = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", executableName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HighlightForge", "tools", "ffmpeg", executableName)
        };
        candidates.AddRange(FindFromRegisteredPath(EnvironmentVariableTarget.User, executableName));
        candidates.AddRange(FindFromRegisteredPath(EnvironmentVariableTarget.Machine, executableName));
        if (additionalCandidates is not null) candidates.AddRange(additionalCandidates);
        return candidates.FirstOrDefault(File.Exists) ?? Path.GetFileNameWithoutExtension(executableName);
    }

    private static IEnumerable<string> FindFromRegisteredPath(EnvironmentVariableTarget target, string executableName)
    {
        var path = Environment.GetEnvironmentVariable("Path", target);
        if (string.IsNullOrWhiteSpace(path)) yield break;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(directory, executableName);
        }
    }

    public static string MissingRuntimeMessage =>
        "FFmpeg is required to inspect OBS recordings. Install an LGPL FFmpeg build, restart HighlightForge, or set HIGHLIGHTFORGE_FFPROBE_PATH to the full path of ffprobe.exe.";
}
