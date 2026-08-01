using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Diagnostics.CodeAnalysis;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Persistence;
using HighlightForge.Core.Timeline;
using HighlightForge.Media.Analysis;
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
    private LocalAnalysisResult? _analysisResult;
    private CancellationTokenSource? _analysisCancellation;
    private readonly Stack<IReadOnlyList<TimelineClip>> _undoTimeline = new();
    private readonly Stack<IReadOnlyList<TimelineClip>> _redoTimeline = new();

    public MainWindow()
    {
        InitializeComponent();
        InitializePlayer();
        Closed += (_, _) =>
        {
            _analysisCancellation?.Cancel();
            _analysisCancellation?.Dispose();
            DisposePlayer();
        };
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
        _analysisResult = null;
        _undoTimeline.Clear();
        _redoTimeline.Clear();
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
        _undoTimeline.Clear();
        _redoTimeline.Clear();
        ShowWorkspace();
        await LoadAnalysisResultAsync();
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
            Title = "Choose an OBS recording",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Video recordings") { Patterns = ["*.mkv", "*.mp4", "*.mov"] }]
        });
        if (files.Count == 0) return;
        try
        {
            var filePath = files[0].Path.LocalPath;
            await HighlightForgeLog.InfoAsync($"Import requested for '{filePath}'.");
            var imported = await SourceImportService.ImportAsync(filePath);
            await AddImportedSourceAsync(imported.Source);
            _analysisResult = null;
            _undoTimeline.Clear();
            _redoTimeline.Clear();
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

    private async void Analyze_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _projectDirectory is null || _project.Sources.Count == 0)
        {
            FooterStatusText.Text = "Import a recording before starting analysis.";
            return;
        }

        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        AnalyzeButton.IsEnabled = false;
        CancelAnalysisButton.IsEnabled = true;
        AnalysisProgressBar.Value = 0;
        try
        {
            var source = _project.Sources[0];
            var progress = new Progress<AnalysisProgress>(update =>
            {
                AnalysisProgressBar.Value = update.Progress * 100;
                FooterStatusText.Text = $"Analysis: {update.Detail}";
            });
            _analysisResult = await LocalFeatureAnalyzer.AnalyzeAsync(
                new ProjectPaths(_projectDirectory), source, SelectedAnalysisMode(), progress, cancellationToken: _analysisCancellation.Token);
            CandidateList.ItemsSource = BuildCandidateLabels(_analysisResult);
            if (_analysisResult.Draft.Clips.Count > 0)
            {
                IReadOnlyList<TimelineClip> timeline = [];
                foreach (var clip in _analysisResult.Draft.Clips)
                {
                    timeline = TimelineEditor.Append(timeline, source.Id, clip.SourceIn, clip.SourceOut);
                }
                await ApplyTimelineAsync(timeline, "Created an editable highlight draft from the ranked candidates.");
            }
            else
            {
                FooterStatusText.Text = "Analysis completed, but no strong candidates were found. The original timeline was kept.";
            }
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = "Analysis cancelled. The last saved project remains available.";
            await HighlightForgeLog.InfoAsync("Local analysis was cancelled.");
        }
        catch (Exception exception)
        {
            await HighlightForgeLog.ErrorAsync("Local highlight analysis failed.", exception);
            FooterStatusText.Text = $"Analysis failed: {exception.Message} Log: {HighlightForgeLog.CurrentLogPath}";
        }
        finally
        {
            AnalyzeButton.IsEnabled = true;
            CancelAnalysisButton.IsEnabled = false;
        }
    }

    private void CancelAnalysis_Click(object? sender, RoutedEventArgs e) => _analysisCancellation?.Cancel();

    private void CandidateList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = CandidateList.SelectedIndex;
        if (_analysisResult is null || index < 0 || index >= _analysisResult.RankedCandidates.Count || _mediaPlayer is null) return;
        _mediaPlayer.Time = (long)_analysisResult.RankedCandidates[index].SourceIn.TotalMilliseconds;
    }

    private void TimelineClipsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var clip = SelectedTimelineClip();
        if (clip is null || _mediaPlayer is null) return;
        _mediaPlayer.Time = (long)clip.SourceIn.TotalMilliseconds;
    }

    private async void SplitClip_Click(object? sender, RoutedEventArgs e)
    {
        var clip = SelectedTimelineClip();
        if (clip is null || _project is null || _mediaPlayer is null) return;
        var playhead = TimeSpan.FromMilliseconds(_mediaPlayer.Time);
        if (playhead <= clip.SourceIn || playhead >= clip.SourceOut)
        {
            FooterStatusText.Text = "Place the playhead inside the selected clip before splitting.";
            return;
        }
        await ApplyTimelineAsync(TimelineEditor.Split(_project.Timeline, clip.Id, playhead), "Split clip at the playhead.");
    }

    private async void SetClipIn_Click(object? sender, RoutedEventArgs e) => await TrimSelectedClipAsync(setIn: true);
    private async void SetClipOut_Click(object? sender, RoutedEventArgs e) => await TrimSelectedClipAsync(setIn: false);

    private async Task TrimSelectedClipAsync(bool setIn)
    {
        var clip = SelectedTimelineClip();
        if (clip is null || _project is null || _mediaPlayer is null) return;
        var playhead = TimeSpan.FromMilliseconds(_mediaPlayer.Time);
        var sourceIn = setIn ? playhead : clip.SourceIn;
        var sourceOut = setIn ? clip.SourceOut : playhead;
        if (sourceIn < TimeSpan.Zero || sourceOut <= sourceIn)
        {
            FooterStatusText.Text = "The playhead must leave a positive-duration clip.";
            return;
        }
        await ApplyTimelineAsync(TimelineEditor.Trim(_project.Timeline, clip.Id, sourceIn, sourceOut), setIn ? "Updated clip in point." : "Updated clip out point.");
    }

    private async void DeleteClip_Click(object? sender, RoutedEventArgs e)
    {
        var clip = SelectedTimelineClip();
        if (clip is null || _project is null) return;
        await ApplyTimelineAsync(TimelineEditor.DeleteWithRipple(_project.Timeline, clip.Id), "Ripple-deleted the selected clip.");
    }

    private async void MoveClipUp_Click(object? sender, RoutedEventArgs e) => await MoveSelectedClipAsync(-1);
    private async void MoveClipDown_Click(object? sender, RoutedEventArgs e) => await MoveSelectedClipAsync(1);

    private async Task MoveSelectedClipAsync(int offset)
    {
        var clip = SelectedTimelineClip();
        if (clip is null || _project is null) return;
        var currentIndex = _project.Timeline.ToList().FindIndex(item => item.Id == clip.Id);
        var destination = Math.Clamp(currentIndex + offset, 0, _project.Timeline.Count - 1);
        if (destination == currentIndex) return;
        await ApplyTimelineAsync(TimelineEditor.Move(_project.Timeline, clip.Id, destination), "Reordered the selected clip.");
        TimelineClipsList.SelectedIndex = destination;
    }

    private async void Undo_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _undoTimeline.Count == 0) return;
        _redoTimeline.Push(_project.Timeline.ToArray());
        var previous = _undoTimeline.Pop();
        await SetTimelineWithoutHistoryAsync(previous, "Undid the previous timeline edit.");
    }

    private async void Redo_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _redoTimeline.Count == 0) return;
        _undoTimeline.Push(_project.Timeline.ToArray());
        var next = _redoTimeline.Pop();
        await SetTimelineWithoutHistoryAsync(next, "Redid the timeline edit.");
    }

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

    private async Task LoadAnalysisResultAsync()
    {
        if (_project is null || _projectDirectory is null || _project.Sources.Count == 0) return;
        _analysisResult = await new AnalysisResultStore(new ProjectPaths(_projectDirectory)).LoadAsync(_project.Sources[0].Id);
        CandidateList.ItemsSource = _analysisResult is null ? Array.Empty<string>() : BuildCandidateLabels(_analysisResult);
        AnalysisProgressBar.Value = _analysisResult is null ? 0 : 100;
    }

    private static string[] BuildCandidateLabels(LocalAnalysisResult result) => result.RankedCandidates
        .Select((candidate, index) =>
        {
            var reasons = string.Join(", ", candidate.Reasons.Take(2).Select(reason => reason.Detail));
            return $"{index + 1}. {FormatDuration(candidate.SourceIn)}-{FormatDuration(candidate.SourceOut)} | score {candidate.Score:0.00} | {reasons}";
        })
        .ToArray();

    private TimelineClip? SelectedTimelineClip()
    {
        if (_project is null) return null;
        var index = TimelineClipsList.SelectedIndex;
        return index >= 0 && index < _project.Timeline.Count ? _project.Timeline[index] : null;
    }

    private async Task ApplyTimelineAsync(IReadOnlyList<TimelineClip> timeline, string status)
    {
        if (_project is null) return;
        _undoTimeline.Push(_project.Timeline.ToArray());
        _redoTimeline.Clear();
        await SetTimelineWithoutHistoryAsync(timeline, status);
    }

    private async Task SetTimelineWithoutHistoryAsync(IReadOnlyList<TimelineClip> timeline, string status)
    {
        if (_project is null || _projectStore is null) return;
        _project = _project with { Timeline = timeline, ModifiedUtc = DateTimeOffset.UtcNow };
        await _projectStore.SaveAsync(_project);
        RefreshTimeline();
        FooterStatusText.Text = $"{status} Project autosaved.";
    }

    private void RefreshTimeline()
    {
        if (_project is null) return;
        TimelineClipsList.ItemsSource = _project.Timeline.Select((clip, index) =>
        {
            var source = _project.Sources.FirstOrDefault(item => item.Id == clip.SourceId);
            var name = source is null ? "Missing source" : Path.GetFileName(source.AbsolutePath);
            return $"{index + 1}. {name} | {FormatDuration(clip.SourceIn)}-{FormatDuration(clip.SourceOut)} | {FormatDuration(clip.SourceOut - clip.SourceIn)}";
        }).ToArray();
        var duration = _project.Timeline.Aggregate(TimeSpan.Zero, (total, clip) => total + clip.SourceOut - clip.SourceIn);
        TimelineDurationText.Text = FormatDuration(duration);
        if (_project.Timeline.Count > 0 && TimelineClipsList.SelectedIndex < 0) TimelineClipsList.SelectedIndex = 0;
    }

    private AnalysisMode SelectedAnalysisMode() => AnalysisModeBox.SelectedIndex switch
    {
        0 => AnalysisMode.Fast,
        2 => AnalysisMode.Deep,
        _ => AnalysisMode.Balanced
    };

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
            TimelineClipsList.ItemsSource = Array.Empty<string>();
            TimelineDurationText.Text = "0:00";
            return;
        }
        SourceButton.Content = Path.GetFileName(source.AbsolutePath);
        MediaDetailsText.Text = $"{source.Width}×{source.Height} • {FormatDuration(source.Duration)} • {source.FramesPerSecond:0.##} fps";
        AudioTracksText.Text = string.Join(Environment.NewLine, source.AudioTracks.Select(track => $"• {track.DisplayName}: {track.Role}"));
        ProjectStatusText.Text = source.AudioTracks.Any(track => track.Role == AudioTrackRole.Mixed) && source.AudioTracks.Any(track => track.Role == AudioTrackRole.Microphone) && source.AudioTracks.Any(track => track.Role == AudioTrackRole.Game)
            ? "Separate Game and Microphone tracks will be used for editing; Main is retained as a reference."
            : "Confirm track roles before mixing.";
        RefreshTimeline();
        CandidateList.ItemsSource = _analysisResult is null ? Array.Empty<string>() : BuildCandidateLabels(_analysisResult);
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}" : $"{duration.Minutes}:{duration.Seconds:00}";

    private void DisposePlayer()
    {
        _media?.Dispose();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();
    }
}
