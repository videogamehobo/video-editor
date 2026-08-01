using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Audio;
using HighlightForge.Core.Captions;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Models;
using HighlightForge.Core.Persistence;
using HighlightForge.Core.Timeline;
using HighlightForge.Core.Voiceover;
using HighlightForge.Media.Analysis;
using HighlightForge.Media.Audio;
using HighlightForge.Media.Captions;
using HighlightForge.Media.Import;
using HighlightForge.Media.Models;
using HighlightForge.Media.Runtime;
using HighlightForge.Media.Voiceover;
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
    private CreatorWorkflowState? _creatorState;
    private CancellationTokenSource? _analysisCancellation;
    private CancellationTokenSource? _creatorCancellation;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromHours(2) };
    private readonly VoiceoverRecorder _voiceoverRecorder = new();
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
            _creatorCancellation?.Cancel();
            _creatorCancellation?.Dispose();
            _voiceoverRecorder.Dispose();
            _httpClient.Dispose();
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
        _creatorState = null;
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
        await LoadCreatorStateAsync();
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
            await LoadCreatorStateAsync();
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
        if (!_project.Sources[0].AudioRolesConfirmed)
        {
            FooterStatusText.Text = "Review and confirm the OBS audio-track roles before analysis.";
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
            RefreshVoiceoverSuggestions();
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

    private void CancelCreator_Click(object? sender, RoutedEventArgs e) => _creatorCancellation?.Cancel();

    private async void InstallModel_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await InstallSelectedModelAsync();
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = "Local model installation cancelled.";
        }
        catch (Exception exception)
        {
            await HighlightForgeLog.ErrorAsync("Local Whisper model installation failed.", exception);
            FooterStatusText.Text = $"Model installation failed: {exception.Message} Log: {HighlightForgeLog.CurrentLogPath}";
        }
    }

    private async Task<string> InstallSelectedModelAsync()
    {
        BeginCreatorOperation();
        InstallModelButton.IsEnabled = false;
        CancelCreatorButton.IsEnabled = true;
        var pack = WhisperModelCatalog.ForMode(SelectedAnalysisMode());
        try
        {
            var installer = new WhisperModelInstaller(_httpClient);
            var progress = new Progress<ModelDownloadProgress>(update =>
            {
                CreatorProgressBar.Value = update.Fraction * 100;
                ModelStatusText.Text = $"Downloading {pack.DisplayName}: {update.Fraction:P0}";
            });
            var modelPath = await installer.InstallAsync(pack, progress, _creatorCancellation!.Token);
            ModelStatusText.Text = $"Installed and verified: {pack.DisplayName}";
            CreatorProgressBar.Value = 100;
            return modelPath;
        }
        finally
        {
            InstallModelButton.IsEnabled = true;
            CancelCreatorButton.IsEnabled = false;
        }
    }

    private async void Transcribe_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _projectDirectory is null || _project.Sources.Count == 0)
        {
            FooterStatusText.Text = "Import a recording before creating captions.";
            return;
        }
        if (!_project.Sources[0].AudioRolesConfirmed)
        {
            FooterStatusText.Text = "Review and confirm the OBS audio-track roles before transcription.";
            return;
        }

        TranscribeButton.IsEnabled = false;
        try
        {
            var pack = WhisperModelCatalog.ForMode(SelectedAnalysisMode());
            var installer = new WhisperModelInstaller(_httpClient);
            var modelPath = await installer.GetInstalledModelPathAsync(pack);
            if (modelPath is null) modelPath = await InstallSelectedModelAsync();
            BeginCreatorOperation();
            CancelCreatorButton.IsEnabled = true;
            var source = _project.Sources[0];
            var progress = new Progress<TranscriptionProgress>(update =>
            {
                CreatorProgressBar.Value = update.Fraction * 100;
                FooterStatusText.Text = update.Detail;
            });
            var captions = await WhisperTranscriptionService.TranscribeAsync(
                new ProjectPaths(_projectDirectory), source, modelPath, progress, cancellationToken: _creatorCancellation!.Token);
            _creatorState = (_creatorState ?? CreatorWorkflowState.Empty(source.Id)) with
            {
                Captions = captions,
                ModifiedUtc = DateTimeOffset.UtcNow
            };
            await SaveCreatorStateAsync();
            RefreshCaptions();
            FooterStatusText.Text = $"Created {captions.Count} editable local caption cues. Project autosaved.";
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = "Local transcription cancelled; the original recording remains untouched.";
        }
        catch (Exception exception)
        {
            await HighlightForgeLog.ErrorAsync("Local transcription failed.", exception);
            FooterStatusText.Text = $"Transcription failed: {exception.Message} Log: {HighlightForgeLog.CurrentLogPath}";
        }
        finally
        {
            TranscribeButton.IsEnabled = true;
            CancelCreatorButton.IsEnabled = false;
        }
    }

    private void CaptionList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = CaptionList.SelectedIndex;
        if (_creatorState is null || index < 0 || index >= _creatorState.Captions.Count) return;
        var cue = _creatorState.Captions[index];
        CaptionStartTextBox.Text = cue.Start.ToString("hh\\:mm\\:ss\\.fff", CultureInfo.InvariantCulture);
        CaptionEndTextBox.Text = cue.End.ToString("hh\\:mm\\:ss\\.fff", CultureInfo.InvariantCulture);
        CaptionTextBox.Text = cue.Text;
        if (_mediaPlayer is not null) _mediaPlayer.Time = (long)cue.Start.TotalMilliseconds;
    }

    private async void UpdateCaption_Click(object? sender, RoutedEventArgs e)
    {
        var index = CaptionList.SelectedIndex;
        if (_creatorState is null || index < 0 || index >= _creatorState.Captions.Count) return;
        if (!TimeSpan.TryParse(CaptionStartTextBox.Text, CultureInfo.InvariantCulture, out var start) ||
            !TimeSpan.TryParse(CaptionEndTextBox.Text, CultureInfo.InvariantCulture, out var end))
        {
            FooterStatusText.Text = "Caption times must use hh:mm:ss.fff format.";
            return;
        }
        IReadOnlyList<CaptionCue> captions;
        try
        {
            captions = CaptionDocument.UpdateCue(_creatorState.Captions, index, start, end, CaptionTextBox.Text ?? string.Empty);
        }
        catch (ArgumentException exception)
        {
            FooterStatusText.Text = exception.Message;
            return;
        }
        _creatorState = _creatorState with { Captions = captions, ModifiedUtc = DateTimeOffset.UtcNow };
        await SaveCreatorStateAsync();
        RefreshCaptions();
        CaptionList.SelectedIndex = index;
        FooterStatusText.Text = "Caption edit saved locally.";
    }

    private async void ExportSrt_Click(object? sender, RoutedEventArgs e) => await ExportCaptionsAsync(isVtt: false);
    private async void ExportVtt_Click(object? sender, RoutedEventArgs e) => await ExportCaptionsAsync(isVtt: true);

    private async Task ExportCaptionsAsync(bool isVtt)
    {
        if (_creatorState is null || _creatorState.Captions.Count == 0 || _project is null || _project.Sources.Count == 0)
        {
            FooterStatusText.Text = "Create captions before exporting a sidecar file.";
            return;
        }
        var extension = isVtt ? "vtt" : "srt";
        var output = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export {extension.ToUpperInvariant()} captions",
            SuggestedFileName = $"{_project.Name}.{extension}",
            FileTypeChoices = [new FilePickerFileType($"{extension.ToUpperInvariant()} captions") { Patterns = [$"*.{extension}"] }]
        });
        if (output is null) return;
        var outputPath = output.Path.LocalPath;
        MediaPathSafety.RequireSeparateOutput(_project.Sources[0].AbsolutePath, outputPath, "Caption export");
        var contents = isVtt ? CaptionDocument.ToWebVtt(_creatorState.Captions) : CaptionDocument.ToSrt(_creatorState.Captions);
        await File.WriteAllTextAsync(outputPath, contents);
        FooterStatusText.Text = $"Exported captions to {outputPath}.";
        await HighlightForgeLog.InfoAsync($"Exported caption sidecar to '{outputPath}'.");
    }

    private async void RecordVoiceover_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _projectDirectory is null || _project.Sources.Count == 0) return;
        try
        {
            if (!_voiceoverRecorder.IsRecording)
            {
                var suggestionIndex = VoiceoverSuggestionList.SelectedIndex;
                var suggestions = CurrentVoiceoverSuggestions();
                var start = suggestionIndex >= 0 && suggestionIndex < suggestions.Count
                    ? suggestions[suggestionIndex].TimelinePosition
                    : TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer?.Time ?? 0));
                var path = _voiceoverRecorder.Start(new ProjectPaths(_projectDirectory), start);
                RecordVoiceoverButton.Content = "Stop and save take";
                FooterStatusText.Text = $"Recording locally to the project takes folder: {Path.GetFileName(path)}";
            }
            else
            {
                var take = await _voiceoverRecorder.StopAsync();
                var state = _creatorState ?? CreatorWorkflowState.Empty(_project.Sources[0].Id);
                _creatorState = state with { VoiceoverTakes = state.VoiceoverTakes.Append(take).ToArray(), ModifiedUtc = DateTimeOffset.UtcNow };
                await SaveCreatorStateAsync();
                RefreshVoiceoverTakes();
                RecordVoiceoverButton.Content = "Record microphone take";
                FooterStatusText.Text = "Voice-over take saved inside this project.";
            }
        }
        catch (Exception exception)
        {
            RecordVoiceoverButton.Content = "Record microphone take";
            await HighlightForgeLog.ErrorAsync("Voice-over recording failed.", exception);
            FooterStatusText.Text = $"Voice-over recording failed: {exception.Message} Log: {HighlightForgeLog.CurrentLogPath}";
        }
    }

    private async void SelectVoiceoverTake_Click(object? sender, RoutedEventArgs e)
    {
        var index = VoiceoverTakeList.SelectedIndex;
        if (_creatorState is null || index < 0 || index >= _creatorState.VoiceoverTakes.Count) return;
        var selectedId = _creatorState.VoiceoverTakes[index].Id;
        _creatorState = _creatorState with
        {
            VoiceoverTakes = _creatorState.VoiceoverTakes.Select(take => take with { IsSelected = take.Id == selectedId }).ToArray(),
            ModifiedUtc = DateTimeOffset.UtcNow
        };
        await SaveCreatorStateAsync();
        RefreshVoiceoverTakes();
        VoiceoverTakeList.SelectedIndex = index;
        FooterStatusText.Text = "Selected voice-over take saved to the project.";
    }

    private async void MeasureAudio_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _projectDirectory is null || _project.Sources.Count == 0) return;
        if (!_project.Sources[0].AudioRolesConfirmed)
        {
            FooterStatusText.Text = "Review and confirm the OBS audio-track roles before mastering.";
            return;
        }
        BeginCreatorOperation();
        MeasureAudioButton.IsEnabled = false;
        CancelAudioButton.IsEnabled = true;
        try
        {
            var source = _project.Sources[0];
            var usesDiscrete = source.AudioTracks.Any(track => track.Role == AudioTrackRole.Microphone) && source.AudioTracks.Any(track => track.Role == AudioTrackRole.Game);
            var plan = AudioMixPlanner.Create(source.AudioTracks, usesDiscrete);
            var measurements = new List<AudioLoudnessMeasurement>();
            for (var index = 0; index < plan.InputTracks.Count; index++)
            {
                var track = plan.InputTracks[index];
                FooterStatusText.Text = $"Measuring {track.DisplayName} without changing the recording…";
                measurements.Add(await AudioLoudnessAnalyzer.MeasureAsync(source.AbsolutePath, track, cancellationToken: _creatorCancellation!.Token));
                AudioProgressBar.Value = (index + 1d) / plan.InputTracks.Count * 100;
            }
            if (plan.UsesDiscreteTracks && measurements.All(measurement => measurement.IntegratedLufs <= -70))
            {
                var mixed = source.AudioTracks.FirstOrDefault(track => track.Role == AudioTrackRole.Mixed);
                if (mixed is not null)
                {
                    FooterStatusText.Text = "The discrete tracks are silent; checking the combined OBS track as a read-only fallback…";
                    var fallback = await AudioLoudnessAnalyzer.MeasureAsync(source.AbsolutePath, mixed, cancellationToken: _creatorCancellation!.Token);
                    if (fallback.IntegratedLufs > -70) measurements = [fallback];
                }
            }
            var state = _creatorState ?? CreatorWorkflowState.Empty(source.Id);
            _creatorState = state with { LoudnessMeasurements = measurements, ModifiedUtc = DateTimeOffset.UtcNow };
            await SaveCreatorStateAsync();
            RefreshAudioPlan();
            FooterStatusText.Text = "Audio measurement completed and the non-destructive mastering plan was saved.";
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = "Audio measurement cancelled; no source audio was changed.";
        }
        catch (Exception exception)
        {
            await HighlightForgeLog.ErrorAsync("Audio loudness measurement failed.", exception);
            FooterStatusText.Text = $"Audio measurement failed: {exception.Message} Log: {HighlightForgeLog.CurrentLogPath}";
        }
        finally
        {
            MeasureAudioButton.IsEnabled = true;
            CancelAudioButton.IsEnabled = false;
        }
    }

    private void CandidateList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = CandidateList.SelectedIndex;
        if (_analysisResult is null || index < 0 || index >= _analysisResult.RankedCandidates.Count || _mediaPlayer is null) return;
        _mediaPlayer.Time = (long)_analysisResult.RankedCandidates[index].SourceIn.TotalMilliseconds;
    }

    private void AudioTrackList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_project is null || _project.Sources.Count == 0) return;
        var source = _project.Sources[0];
        var index = AudioTrackListBox.SelectedIndex;
        if (index < 0 || index >= source.AudioTracks.Count) return;
        TrackRoleBox.SelectedIndex = source.AudioTracks[index].Role switch
        {
            AudioTrackRole.Mixed => 1,
            AudioTrackRole.Microphone => 2,
            AudioTrackRole.Game => 3,
            _ => 0
        };
    }

    private async void UpdateTrackRole_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _projectStore is null || _project.Sources.Count == 0) return;
        var source = _project.Sources[0];
        var index = AudioTrackListBox.SelectedIndex;
        if (index < 0 || index >= source.AudioTracks.Count)
        {
            FooterStatusText.Text = "Select an audio track before changing its role.";
            return;
        }
        var role = TrackRoleBox.SelectedIndex switch
        {
            1 => AudioTrackRole.Mixed,
            2 => AudioTrackRole.Microphone,
            3 => AudioTrackRole.Game,
            _ => AudioTrackRole.Unassigned
        };
        var tracks = source.AudioTracks.ToArray();
        tracks[index] = tracks[index] with { Role = role };
        var updatedSource = source with { AudioTracks = tracks, AudioRolesConfirmed = false };
        _project = _project with
        {
            Sources = _project.Sources.Select(item => item.Id == source.Id ? updatedSource : item).ToArray(),
            ModifiedUtc = DateTimeOffset.UtcNow
        };
        await _projectStore.SaveAsync(_project);
        RefreshTrackInspector(updatedSource);
        FooterStatusText.Text = "Track role updated. Confirm the full mapping before analysis.";
    }

    private async void ConfirmTrackRoles_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _projectStore is null || _project.Sources.Count == 0) return;
        var source = _project.Sources[0];
        var validation = AudioTrackRoleValidator.Validate(source.AudioTracks);
        if (!validation.IsValid)
        {
            FooterStatusText.Text = validation.Message;
            return;
        }
        var updatedSource = source with { AudioRolesConfirmed = true };
        _project = _project with
        {
            Sources = _project.Sources.Select(item => item.Id == source.Id ? updatedSource : item).ToArray(),
            ModifiedUtc = DateTimeOffset.UtcNow
        };
        await _projectStore.SaveAsync(_project);
        RefreshTrackInspector(updatedSource);
        FooterStatusText.Text = $"Track roles confirmed. {validation.Message}";
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

    private async Task LoadCreatorStateAsync()
    {
        if (_project is null || _projectDirectory is null || _project.Sources.Count == 0) return;
        var source = _project.Sources[0];
        _creatorState = await new CreatorWorkflowStore(new ProjectPaths(_projectDirectory)).LoadAsync(source.Id);
        RefreshCaptions();
        RefreshVoiceoverSuggestions();
        RefreshVoiceoverTakes();
        RefreshAudioPlan();
        await RefreshModelStatusAsync();
    }

    private async Task SaveCreatorStateAsync()
    {
        if (_creatorState is null || _projectDirectory is null) return;
        await new CreatorWorkflowStore(new ProjectPaths(_projectDirectory)).SaveAsync(_creatorState);
    }

    private void BeginCreatorOperation()
    {
        _creatorCancellation?.Cancel();
        _creatorCancellation?.Dispose();
        _creatorCancellation = new CancellationTokenSource();
    }

    private async Task RefreshModelStatusAsync()
    {
        var pack = WhisperModelCatalog.ForMode(SelectedAnalysisMode());
        var path = await new WhisperModelInstaller(_httpClient).GetInstalledModelPathAsync(pack);
        ModelStatusText.Text = path is null
            ? $"Not installed: {pack.DisplayName} ({pack.DownloadSize / 1_000_000d:0} MB, one-time verified download)"
            : $"Installed and verified: {pack.DisplayName}";
    }

    private void RefreshCaptions()
    {
        CaptionList.ItemsSource = _creatorState?.Captions.Select((cue, index) =>
            $"{index + 1}. {FormatDuration(cue.Start)}–{FormatDuration(cue.End)}  {cue.Text}").ToArray() ?? [];
    }

    private IReadOnlyList<VoiceoverSuggestion> CurrentVoiceoverSuggestions() =>
        _analysisResult is null ? [] : VoiceoverPlanner.Suggest(_analysisResult.RankedCandidates);

    private void RefreshVoiceoverSuggestions()
    {
        VoiceoverSuggestionList.ItemsSource = CurrentVoiceoverSuggestions().Select((suggestion, index) =>
            $"{index + 1}. {FormatDuration(suggestion.TimelinePosition)} — {suggestion.TalkingPoint}").ToArray();
    }

    private void RefreshVoiceoverTakes()
    {
        VoiceoverTakeList.ItemsSource = _creatorState?.VoiceoverTakes.Select((take, index) =>
            $"{(take.IsSelected ? "✓ " : string.Empty)}{index + 1}. {FormatDuration(take.Start)} | {take.Duration:mm\\:ss} | {Path.GetFileName(take.AbsolutePath)}").ToArray() ?? [];
    }

    private void RefreshAudioPlan()
    {
        var measurements = _creatorState?.LoudnessMeasurements ?? [];
        LoudnessList.ItemsSource = measurements.Select(measurement =>
            $"{measurement.DisplayName}: {measurement.IntegratedLufs:0.0} LUFS, {measurement.TruePeakDbtp:0.0} dBTP").ToArray();
        if (measurements.Count == 0)
        {
            AudioPlanText.Text = "Measure the selected tracks to create a conservative mastering plan.";
            return;
        }
        var discrete = measurements.Count > 1;
        AudioPlanText.Text = discrete
            ? "Plan: clean microphone conservatively, sidechain-duck game audio during speech (60 ms attack / 450 ms release), exclude the combined OBS track, then normalize the final mix to −14 LUFS and at most −1 dBTP."
            : "Plan: preserve the single mixed track and normalize the final mix to −14 LUFS and at most −1 dBTP.";
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
            AudioTrackListBox.ItemsSource = Array.Empty<string>();
            CandidateList.ItemsSource = Array.Empty<string>();
            TimelineClipsList.ItemsSource = Array.Empty<string>();
            TimelineDurationText.Text = "0:00";
            RefreshCaptions();
            RefreshVoiceoverSuggestions();
            RefreshVoiceoverTakes();
            RefreshAudioPlan();
            return;
        }
        SourceButton.Content = Path.GetFileName(source.AbsolutePath);
        MediaDetailsText.Text = $"{source.Width}×{source.Height} • {FormatDuration(source.Duration)} • {source.FramesPerSecond:0.##} fps";
        RefreshTrackInspector(source);
        RefreshTimeline();
        CandidateList.ItemsSource = _analysisResult is null ? Array.Empty<string>() : BuildCandidateLabels(_analysisResult);
        RefreshCaptions();
        RefreshVoiceoverSuggestions();
        RefreshVoiceoverTakes();
        RefreshAudioPlan();
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}" : $"{duration.Minutes}:{duration.Seconds:00}";

    private void RefreshTrackInspector(MediaSource source)
    {
        AudioTracksText.Text = string.Join(Environment.NewLine, source.AudioTracks.Select(track => $"• {track.DisplayName}: {track.Role}"));
        AudioTrackListBox.ItemsSource = source.AudioTracks.Select(track => $"{track.StreamIndex}: {track.DisplayName}").ToArray();
        if (source.AudioTracks.Count > 0 && AudioTrackListBox.SelectedIndex < 0) AudioTrackListBox.SelectedIndex = 0;
        ProjectStatusText.Text = source.AudioRolesConfirmed
            ? "Audio roles confirmed. Original source tracks remain read-only."
            : "Review the inferred roles, correct any mistakes, then select Confirm roles.";
    }

    private void DisposePlayer()
    {
        _media?.Dispose();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();
    }
}
