using HighlightForge.Core.Diagnostics;

namespace HighlightForge.Core.Tests;

public sealed class HighlightForgeLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HighlightForgeLogTests", Guid.NewGuid().ToString("N"));
    private readonly string? _originalOverride = Environment.GetEnvironmentVariable("HIGHLIGHTFORGE_LOG_DIRECTORY");

    [Fact]
    public async Task InaccessibleConfiguredDirectoryDoesNotFailCallingOperation()
    {
        Directory.CreateDirectory(_directory);
        var fileInsteadOfDirectory = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(fileInsteadOfDirectory, "occupied");
        Environment.SetEnvironmentVariable("HIGHLIGHTFORGE_LOG_DIRECTORY", fileInsteadOfDirectory);

        var exception = await Record.ExceptionAsync(() => HighlightForgeLog.InfoAsync("fallback logging test"));

        Assert.Null(exception);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "HighlightForge", "logs")), HighlightForgeLog.CurrentLogPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(HighlightForgeLog.CurrentLogPath));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HIGHLIGHTFORGE_LOG_DIRECTORY", _originalOverride);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
