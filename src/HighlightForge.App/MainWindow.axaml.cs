using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Persistence;
using HighlightForge.Media.Import;
using HighlightForge.Media.Runtime;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Timeline;

namespace HighlightForge.App;

public partial class MainWindow : Window
{
    private ProjectDocument? _project;
    private ProjectStore? _projectStore;
    private string? _projectDirectory;

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
        _project = ProjectDocument.Create("Untitled", now);
        _projectStore = store;
        _projectDirectory = directory;
        await store.SaveAsync(_project);
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
            await AddImportedSourceAsync(imported.Source);
            var tracks = string.Join(", ", imported.SuggestedTrackMapping.Suggestions.Select(track => $"{track.Role} ({track.Confidence:P0})"));
            StatusText.Text = "Import complete. The full source is saved as the first timeline clip; local highlight analysis is the next step.";
            ImportedNameText.Text = Path.GetFileName(imported.Source.AbsolutePath);
            ImportedMediaText.Text = $"{imported.Source.Width}×{imported.Source.Height} • {FormatDuration(imported.Source.Duration)} • {imported.Source.FramesPerSecond:0.##} fps";
            ImportedTracksText.Text = $"Detected tracks: {tracks}";
            ImportedTimelineText.Text = $"Timeline: 1 source clip spanning {FormatDuration(imported.Source.Duration)}. The mixed track will be excluded whenever separate Game and Microphone tracks are confirmed.";
            ImportedProjectText.Text = $"Project saved locally: {_projectDirectory}";
            ImportedPanel.IsVisible = true;
            await HighlightForgeLog.InfoAsync($"Import completed for '{imported.Source.AbsolutePath}' and was saved to '{_projectDirectory}'.");
        }
        catch (Exception exception)
        {
            await HighlightForgeLog.ErrorAsync("OBS recording import failed.", exception);
            StatusText.Text = exception.Message.Contains("FFmpeg is required", StringComparison.Ordinal)
                ? $"Import needs setup: {FfmpegRuntime.MissingRuntimeMessage}"
                : $"Import failed: {exception.Message} Log: {HighlightForgeLog.CurrentLogPath}";
        }
    }

    private async Task AddImportedSourceAsync(MediaSource source)
    {
        if (_project is null || _projectStore is null)
        {
            var baseName = Path.GetFileNameWithoutExtension(source.AbsolutePath);
            var safeName = string.Concat(baseName.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            _projectDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HighlightForge", "projects", $"{safeName}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.gheproj");
            _projectStore = new ProjectStore(new ProjectPaths(_projectDirectory));
            _project = ProjectDocument.Create(baseName, DateTimeOffset.UtcNow);
        }

        var updatedSources = _project.Sources.Append(source).ToArray();
        var updatedTimeline = TimelineEditor.Append(_project.Timeline, source.Id, TimeSpan.Zero, source.Duration);
        _project = _project with { Sources = updatedSources, Timeline = updatedTimeline, ModifiedUtc = DateTimeOffset.UtcNow };
        await _projectStore.SaveAsync(_project);
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
        : $"{duration.Minutes}:{duration.Seconds:00}";
}
