using System.Text;
using System.Diagnostics;

namespace HighlightForge.Core.Diagnostics;

public static class HighlightForgeLog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _activeDirectoryPath;

    public static string DirectoryPath => Volatile.Read(ref _activeDirectoryPath) ?? PreferredDirectoryPath;
    public static string CurrentLogPath => Path.Combine(DirectoryPath, $"highlightforge-{DateTime.UtcNow:yyyy-MM-dd}.log");

    public static Task InfoAsync(string message, CancellationToken cancellationToken = default) => WriteAsync("INFO", message, null, cancellationToken);
    public static Task ErrorAsync(string message, Exception exception, CancellationToken cancellationToken = default) => WriteAsync("ERROR", message, exception, cancellationToken);

    private static async Task WriteAsync(string level, string message, Exception? exception, CancellationToken cancellationToken)
    {
        var entry = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            .Append(' ').Append(level).Append(' ').Append(message);
        if (exception is not null) entry.AppendLine().Append(exception);
        entry.AppendLine();
        await Gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var directory in CandidateDirectories().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, $"highlightforge-{DateTime.UtcNow:yyyy-MM-dd}.log");
                    await File.AppendAllTextAsync(path, entry.ToString(), cancellationToken);
                    Volatile.Write(ref _activeDirectoryPath, directory);
                    return;
                }
                catch (Exception writeException) when (writeException is UnauthorizedAccessException or IOException)
                {
                    Debug.WriteLine($"HighlightForge logging failed at '{directory}': {writeException.Message}");
                }
            }

            // Diagnostics are best-effort. A locked or inaccessible log location must never
            // turn a media import, analysis job, or render into an application failure.
            Debug.WriteLine(entry.ToString());
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string PreferredDirectoryPath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("HIGHLIGHTFORGE_LOG_DIRECTORY");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HighlightForge", "logs")
                : Path.GetFullPath(configured);
        }
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        yield return PreferredDirectoryPath;
        yield return Path.Combine(Path.GetTempPath(), "HighlightForge", "logs");
    }
}
