using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Persistence;
using HighlightForge.Media.Import;
using HighlightForge.Media.Runtime;
using HighlightForge.Core.Diagnostics;

namespace HighlightForge.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void CreateProject_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder for the HighlightForge project",
            AllowMultiple = false
        });
        if (folder.Count == 0) return;

        var directory = Path.Combine(folder[0].Path.LocalPath, "Untitled.gheproj");
        var store = new ProjectStore(new ProjectPaths(directory));
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(ProjectDocument.Create("Untitled", now));
        StatusText.Text = $"Created a non-destructive project at {directory}";
    }

    private async void ImportRecording_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an OBS recording",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Video recordings") { Patterns = ["*.mkv", "*.mp4", "*.mov"] }]
        });
        if (files.Count == 0) return;
        try
        {
            await HighlightForgeLog.InfoAsync($"Import requested for '{files[0].Path.LocalPath}'.");
            var imported = await SourceImportService.ImportAsync(files[0].Path.LocalPath);
            var tracks = string.Join(", ", imported.SuggestedTrackMapping.Suggestions.Select(track => $"{track.Role} ({track.Confidence:P0})"));
            StatusText.Text = $"Imported {Path.GetFileName(imported.Source.AbsolutePath)} • {imported.Source.Width}×{imported.Source.Height} • suggested tracks: {tracks}";
        }
        catch (Exception exception)
        {
            await HighlightForgeLog.ErrorAsync("OBS recording import failed.", exception);
            StatusText.Text = exception.Message.Contains("FFmpeg is required", StringComparison.Ordinal)
                ? $"Import needs setup: {FfmpegRuntime.MissingRuntimeMessage}"
                : $"Import failed: {exception.Message} Log: {HighlightForgeLog.CurrentLogPath}";
        }
    }
}
