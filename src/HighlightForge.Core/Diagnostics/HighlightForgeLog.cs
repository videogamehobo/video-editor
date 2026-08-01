using System.Text;

namespace HighlightForge.Core.Diagnostics;

public static class HighlightForgeLog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HighlightForge", "logs");
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
        Directory.CreateDirectory(DirectoryPath);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(CurrentLogPath, entry.ToString(), cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }
}
