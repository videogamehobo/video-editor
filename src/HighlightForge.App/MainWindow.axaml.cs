using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Diagnostics.CodeAnalysis;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Persistence;
using HighlightForge.Core.Timeline;
using HighlightForge.Media.Import;
using HighlightForge.Media.Runtime;
using LibVLCSharp.Shared;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace HighlightForge.App;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Avalonia invokes the Closed handler to dispose player resources.")]
public partial class MainWindow : Window
{
    private ProjectDocument? _project;
    private ProjectStore? _projectStore;
    private string? _projectDirectory;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private VlcMedia? _media;

    public MainWindow()
    {
        InitializeComponent();
        InitializePlayer();
        Closed += (_, _) => DisposePlayer();
    }

    private void InitializePlayer()
    {
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            _libVlc = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVlc);
            VideoView.MediaPlayer = _mediaPlayer;
        }
        catch (Exception exception)
        {
            HighlightForgeLog.ErrorAsync("The project video preview could not be initialized.", exception).GetAwaiter().GetResult();
            FooterStatusText.Text = $"Video preview unavailable. Log: {HighlightForgeLog.CurrentLogPath}";
        }
    }

    private async void CreateProject_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder for the HighlightForge project", AllowMultiple = false });
        if (folder.Count == 0) return;
        _projectDirectory = Path.Combine(folder[0].Path.LocalPath, "Untitled.gheproj");
        _projectStore = new ProjectStore(new ProjectPaths(_projectDirectory));
        _project = ProjectDocument.Create("Untitled", DateTimeOffset.UtcNow);
        await _projectStore.SaveAsync(_project);
        ShowWorkspace();
        await HighlightForgeLog.InfoAsync($"Created project '{_projectDirectory}'.");
    }

    private async void OpenProject_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a .gheproj folder", AllowMultiple = false });
        if (folders.Count == 0) return;
        var directory = folders[0].Path.LocalPath;
        var store = new ProjectStore(new ProjectPaths(directory));
        var project = await store.LoadAsync();
        if (project is null)
        {
            FooterStatusText.Text = "That folder does not contain a HighlightForge project.";
            return;
        }
        _projectDirectory = directory;
        _projectStore = store;
        _project = project;
        ShowWorkspace();
        await OpenFirstSourceAsync();
        await HighlightForgeLog.InfoAsync($"Opened project '{directory}'.");
    }

    private async void SaveProject_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _projectStore is null)
        {
            FooterStatusText.Text = "Create or open a project first.";
            return;
        }
        _project = _project with { ModifiedUtc = DateTimeOffset.UtcNow };
        await _projectStore.SaveAsync(_project);
        FooterStatusText.Text = $"Saved {Path.GetFileName(_projectDirectory)}.";
        await HighlightForgeLog.InfoAsync($"Saved project '{_projectDirectory}'.");
    }

    private async void ImportRecording_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an OBS recording", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Video recordings") { Patterns = ["*.mkv", "*.mp4", "*.mov"] }]
        });
        if (files.Count == 0) return;
        try
        {
            var filePath = files[0].Path.LocalPath;
            await HighlightForgeLog.InfoAsync($"Import requested for '{filePath}'.");
            var imported = await SourceImportService.ImportAsync(filePath);
            await AddImportedSourceAsync(imported.Source);
            ShowWorkspace();
            await OpenSourceAsync(imported.Source);
            FooterStatusText.Text = "Import complete. The recording is ready to watch and edit on the timeline.";
            await HighlightForgeLog.InfoAsync($"Import completed for '{imported.Source.AbsolutePath}' and was saved to '{_projectDirectory}'.");
        }
        catch (Exception exception)
        {
            await HighlightForgeLog.ErrorAsync("OBS recording import failed.", exception);
            FooterStatusText.Text = exception.Message.Contains("FFmpeg is required", StringComparison.Ordinal)
                ? $"Import needs setup: {FfmpegRuntime.MissingRuntimeMessage}"
                : $"Import failed: {exception.Message} Log: {HighlightForgeLog.CurrentLogPath}";
        }
    }

    private async void SourceButton_Click(object? sender, RoutedEventArgs e) => await OpenFirstSourceAsync();

    private void PlayPause_Click(object? sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null) return;
        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
            PlayPauseButton.Content = "Play";
        }
        else
        {
            _mediaPlayer.Play();
            PlayPauseButton.Content = "Pause";
        }
    }

    private void GoToStart_Click(object? sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null) return;
        _mediaPlayer.Time = 0;
        _mediaPlayer.Play();
        PlayPauseButton.Content = "Pause";
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    private async Task AddImportedSourceAsync(MediaSource source)
    {
        if (_project is null || _projectStore is null)
        {
            var baseName = Path.GetFileNameWithoutExtension(source.AbsolutePath);
            var safeName = string.Concat(baseName.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            _projectDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HighlightForge", "projects", $"{safeName}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.gheproj");
            _projectStore = new ProjectStore(new ProjectPaths(_projectDirectory));
            _project = ProjectDocument.Create(baseName, DateTimeOffset.UtcNow);
        }
        _project = _project with
        {
            Sources = _project.Sources.Append(source).ToArray(),
            Timeline = TimelineEditor.Append(_project.Timeline, source.Id, TimeSpan.Zero, source.Duration),
            ModifiedUtc = DateTimeOffset.UtcNow
        };
        await _projectStore.SaveAsync(_project);
    }

    private async Task OpenFirstSourceAsync()
    {
        if (_project is not null && _project.Sources.Count > 0)
        {
            await OpenSourceAsync(_project.Sources[0]);
        }
    }

    private async Task OpenSourceAsync(MediaSource source)
    {
        try
        {
            _media?.Dispose();
            if (_libVlc is null || _mediaPlayer is null) throw new InvalidOperationException("The local video preview is unavailable.");
            _media = new VlcMedia(_libVlc, source.AbsolutePath, FromType.FromPath);
            _mediaPlayer.Media = _media;
            _mediaPlayer.Play();
            PlayPauseButton.Content = "Pause";
            await HighlightForgeLog.InfoAsync($"Opened preview for '{source.AbsolutePath}'.");
        }
        catch (Exception exception)
        {
            await HighlightForgeLog.ErrorAsync("Project video preview failed.", exception);
            FooterStatusText.Text = $"Could not preview this recording. Log: {HighlightForgeLog.CurrentLogPath}";
        }
    }

    private void ShowWorkspace()
    {
        if (_project is null) return;
        WelcomePanel.IsVisible = false;
        WorkspacePanel.IsVisible = true;
        ProjectNameText.Text = _project.Name;
        ProjectPathText.Text = _projectDirectory;
        MediaSource? source = _project.Sources.Count == 0 ? null : _project.Sources[0];
        if (source is null)
        {
            SourceButton.Content = "No recordings imported";
            MediaDetailsText.Text = "Import an OBS recording to begin.";
            AudioTracksText.Text = string.Empty;
            TimelineClipText.Text = "No clips yet.";
            TimelineDurationText.Text = "0:00";
            return;
        }
        SourceButton.Content = Path.GetFileName(source.AbsolutePath);
        MediaDetailsText.Text = $"{source.Width}×{source.Height} • {FormatDuration(source.Duration)} • {source.FramesPerSecond:0.##} fps";
        AudioTracksText.Text = string.Join(Environment.NewLine, source.AudioTracks.Select(track => $"• {track.DisplayName}: {track.Role}"));
        ProjectStatusText.Text = source.AudioTracks.Any(track => track.Role == AudioTrackRole.Mixed) && source.AudioTracks.Any(track => track.Role == AudioTrackRole.Microphone) && source.AudioTracks.Any(track => track.Role == AudioTrackRole.Game)
            ? "Separate Game and Microphone tracks will be used for editing; Main is retained as a reference."
            : "Confirm track roles before mixing.";
        TimelineClipText.Text = $"{Path.GetFileName(source.AbsolutePath)}  •  full source clip  •  {FormatDuration(source.Duration)}";
        TimelineDurationText.Text = FormatDuration(source.Duration);
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}" : $"{duration.Minutes}:{duration.Seconds:00}";

    private void DisposePlayer()
    {
        _media?.Dispose();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();
    }
}
