using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Audio;
using HighlightForge.Core.Captions;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Export;
using HighlightForge.Core.Models;
using HighlightForge.Core.Persistence;
using HighlightForge.Core.Preferences;
using HighlightForge.Core.Timeline;
using HighlightForge.Core.Voiceover;
using HighlightForge.Media.Analysis;
using HighlightForge.Media.Audio;
using HighlightForge.Media.Captions;
using HighlightForge.Media.Import;
using HighlightForge.Media.Models;
using HighlightForge.Media.Proxy;
using HighlightForge.Media.Render;
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
    private CancellationTokenSource? _exportCancellation;
    private CancellationTokenSource? _mediaCacheCancellation;
    private Bitmap? _waveformBitmap;
    private readonly List<Bitmap> _thumbnailBitmaps = [];
    private ProxyTimeMap? _previewTimeMap;
    private Guid? _activeSourceId;
    private Guid? _previewSourceId;
    private bool _updatingSourceSelection;
    private CreatorPreferences _creatorPreferences = new();
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
            _exportCancellation?.Cancel();
            _exportCancellation?.Dispose();
            _mediaCacheCancellation?.Cancel();
            _mediaCacheCancellation?.Dispose();
            DisposeCacheImages();
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
        _activeSourceId = null;
        _analysisResult = null;
        _creatorState = null;
        _undoTimeline.Clear();
        _redoTimeline.Clear();
        await _projectStore.SaveAsync(_project);
        await LoadPreferencesAsync();
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
        _activeSourceId = project.Sources.Count == 0 ? null : project.Sources[0].Id;
        _analysisResult = null;
        _creatorState = null;
        _undoTimeline.Clear();
        _redoTimeline.Clear();
        ShowWorkspace();
        await LoadPreferencesAsync();
        await LoadAnalysisResultAsync();
        await LoadCreatorStateAsync();
        await RefreshMediaCacheAsync();
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
            _activeSourceId = imported.Source.Id;
            await LoadPreferencesAsync();
            _analysisResult = null;
            await LoadCreatorStateAsync();
            _undoTimeline.Clear();
            _redoTimeline.Clear();
            ShowWorkspace();
            await OpenSourceAsync(imported.Source);
            FooterStatusText.Text = "Import complete. The recording is ready to watch and edit on the timeline.";
            await HighlightForgeLog.InfoAsync($"Import completed for '{imported.Source.AbsolutePath}' and was saved to '{_projectDirectory}'.");
            await GeneratePreviewCacheAsync(imported.Source);
        }
        catch (Exception exception)
        {
            await HighlightForgeLog.ErrorAsync("OBS recording import failed.", exception);
            FooterStatusText.Text = exception.Message.Contains("FFmpeg is required", StringComparison.Ordinal)
                ? $"Import needs setup: {FfmpegRuntime.MissingRuntimeMessage}"
                : $"Import failed: {exception.Message} Log: {HighlightForgeLog.CurrentLogPath}";
        }
    }

    private async void SourceButton_Click(object? sender, RoutedEventArgs e)
    {
        var source = SelectedSource();
        if (source is not null) await OpenSourceAsync(source);
    }

    private async void SourceListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSourceSelection || _project is null || SourceListBox.SelectedIndex < 0 || SourceListBox.SelectedIndex >= _project.Sources.Count) return;
        var source = _project.Sources[SourceListBox.SelectedIndex];
        _activeSourceId = source.Id;
        MediaDetailsText.Text = $"{source.Width}×{source.Height} • {FormatDuration(source.Duration)} • {source.FramesPerSecond:0.##} fps";
        RefreshTrackInspector(source);
        await LoadAnalysisResultAsync();
        await LoadCreatorStateAsync();
        await RefreshMediaCacheAsync();
        await OpenSourceAsync(source);
    }

    private async void GenerateCache_Click(object? sender, RoutedEventArgs e)
    {
        var source = SelectedSource();
        if (source is null) return;
        await GeneratePreviewCacheAsync(source);
    }

    private void CancelCache_Click(object? sender, RoutedEventArgs e) => _mediaCacheCancellation?.Cancel();

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
        var source = SelectedSource();
        if (_project is null || _projectDirectory is null || source is null)
        {
            FooterStatusText.Text = "Import a recording before starting analysis.";
            return;
        }
        if (!source.AudioRolesConfirmed)
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
            var progress = new Progress<AnalysisWorkerMessage>(update =>
            {
                AnalysisProgressBar.Value = update.Progress * 100;
                FooterStatusText.Text = update.Capabilities is null
                    ? $"Analysis: {update.Detail}"
                    : $"Analysis worker: {update.Detail} ({update.Capabilities.LogicalProcessorCount} CPU threads, NVIDIA {(update.Capabilities.NvidiaAvailable ? "available" : "not detected")})";
            });
            _analysisResult = await AnalysisWorkerClient.AnalyzeAsync(
                new ProjectPaths(_projectDirectory), source, SelectedAnalysisMode(), progress, _analysisCancellation.Token);
            ApplyPreferencesToAnalysis();
            CandidateList.ItemsSource = BuildCandidateLabels(_analysisResult);
            RefreshVoiceoverSuggestions();
            if (_analysisResult.Draft.Clips.Count > 0)
            {
                var timeline = AssembleDraftTimeline(source, _analysisResult.Draft);
                await ApplyTimelineAsync(timeline, "Created an editable highlight draft from the ranked candidates.");
            }
            else
            {
                FooterStatusText.Text = "Analysis completed, but no strong candidates were found. The original timeline was kept.";
            }
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = "Analysis paused at a safe checkpoint. Choose Analyze & build draft to resume.";
            await HighlightForgeLog.InfoAsync("Local analysis was paused and its checkpoint was saved.");
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

    private async void RollbackModel_Click(object? sender, RoutedEventArgs e)
    {
        var pack = WhisperModelCatalog.ForMode(SelectedAnalysisMode());
        try
        {
            var restored = await new ModelPackManager(WhisperModelCatalog.DefaultRootDirectory).RollbackAsync(pack.Manifest.Id);
            await RefreshModelStatusAsync();
            FooterStatusText.Text = $"Restored verified model version {restored.Version}. All model files remain local.";
        }
        catch (Exception exception)
        {
            await HighlightForgeLog.ErrorAsync("Local model rollback failed.", exception);
            FooterStatusText.Text = $"Model rollback unavailable: {exception.Message}";
        }
    }

    private async void AnalysisMode_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            await RefreshModelStatusAsync();
        }
        catch (Exception exception)
        {
            await HighlightForgeLog.ErrorAsync("Local model status refresh failed.", exception);
            ModelStatusText.Text = "Model status unavailable; see the local log.";
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
            ModelStatusText.Text = "Installing verified local YAMNet sound-event model…";
            var yamnetProgress = new Progress<ModelDownloadProgress>(update =>
            {
                CreatorProgressBar.Value = update.Fraction * 100;
                ModelStatusText.Text = $"Downloading local YAMNet sound-event model: {update.Fraction:P0}";
            });
            await new YamnetModelInstaller(_httpClient).InstallAsync(yamnetProgress, _creatorCancellation.Token);
            ModelStatusText.Text = "Installing verified local English OCR model…";
            await new OcrModelInstaller(_httpClient).InstallAsync(yamnetProgress, _creatorCancellation.Token);
            if (pack.Mode != AnalysisMode.Fast)
            {
                ModelStatusText.Text = "Installing verified local Florence-2 visual context model…";
                await new FlorenceModelInstaller(_httpClient).InstallAsync(yamnetProgress, _creatorCancellation.Token);
                var phiProgress = new Progress<ModelDownloadProgress>(update =>
                {
                    CreatorProgressBar.Value = update.Fraction * 100;
                    ModelStatusText.Text = $"Downloading local Phi-4 narrative model: {update.Fraction:P0} of 4.93 GB";
                });
                await new PhiModelInstaller(_httpClient).InstallAsync(phiProgress, _creatorCancellation.Token);
            }
            await RefreshModelStatusAsync();
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
        var source = SelectedSource();
        if (_project is null || _projectDirectory is null || source is null)
        {
            FooterStatusText.Text = "Import a recording before creating captions.";
            return;
        }
        if (!source.AudioRolesConfirmed)
        {
            FooterStatusText.Text = "Review and confirm the OBS audio-track roles before transcription.";
            return;
        }

        TranscribeButton.IsEnabled = false;
        try
        {
            var pack = WhisperModelCatalog.ForMode(SelectedAnalysisMode());
            var installer = new WhisperModelInstaller(_httpClient);
            var modelPath = await installer.GetActiveModelPathAsync(pack);
            if (modelPath is null) modelPath = await InstallSelectedModelAsync();
            BeginCreatorOperation();
            CancelCreatorButton.IsEnabled = true;
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
        SeekSourceTime(cue.Start);
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

    private async void SaveCaptionStyle_Click(object? sender, RoutedEventArgs e)
    {
        var source = SelectedSource();
        if (_projectDirectory is null || source is null) return;
        try
        {
            var placement = CaptionPlacementBox.SelectedIndex switch
            {
                1 => CaptionPlacement.Middle,
                2 => CaptionPlacement.Top,
                _ => CaptionPlacement.Bottom
            };
            var style = new CaptionStyleSettings(
                CaptionFontBox.Text ?? "Inter",
                Convert.ToInt32(CaptionFontSizeBox.Value, CultureInfo.InvariantCulture),
                CaptionColorBox.Text ?? "#FFFFFF",
                Placement: placement).Validated();
            var state = _creatorState ?? CreatorWorkflowState.Empty(source.Id);
            _creatorState = state with { CaptionStyle = style, ModifiedUtc = DateTimeOffset.UtcNow };
            await SaveCreatorStateAsync();
            RefreshCreatorControls();
            FooterStatusText.Text = "Caption burn-in style saved locally to the project.";
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            FooterStatusText.Text = exception.Message;
        }
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
        var source = SelectedSource();
        if (source is null) return;
        MediaPathSafety.RequireSeparateOutput(source.AbsolutePath, outputPath, "Caption export");
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
                    : CurrentTimelineTime();
                var path = _voiceoverRecorder.Start(new ProjectPaths(_projectDirectory), start);
                RecordVoiceoverButton.Content = "Stop and save take";
                FooterStatusText.Text = $"Recording locally to the project takes folder: {Path.GetFileName(path)}";
            }
            else
            {
                var take = await _voiceoverRecorder.StopAsync();
                var source = SelectedSource();
                if (source is null) return;
                var state = _creatorState ?? CreatorWorkflowState.Empty(source.Id);
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
        var source = SelectedSource();
        if (_project is null || _projectDirectory is null || source is null) return;
        if (!source.AudioRolesConfirmed)
        {
            FooterStatusText.Text = "Review and confirm the OBS audio-track roles before mastering.";
            return;
        }
        BeginCreatorOperation();
        MeasureAudioButton.IsEnabled = false;
        CancelAudioButton.IsEnabled = true;
        try
        {
            var usesDiscrete = source.AudioTracks.Any(track => track.Role == AudioTrackRole.Microphone) && source.AudioTracks.Any(track => track.Role == AudioTrackRole.Game);
            var plan = AudioMixPlanner.Create(source.AudioTracks, usesDiscrete, CurrentAudioSettings());
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

    private async void SaveAudioMix_Click(object? sender, RoutedEventArgs e)
    {
        var source = SelectedSource();
        if (_projectDirectory is null || source is null) return;
        try
        {
            var settings = new AudioMixSettings(
                DuckingDb: Convert.ToDouble(DuckingGainBox.Value, CultureInfo.InvariantCulture),
                DuckingAttackMs: Convert.ToInt32(DuckingAttackBox.Value, CultureInfo.InvariantCulture),
                DuckingReleaseMs: Convert.ToInt32(DuckingReleaseBox.Value, CultureInfo.InvariantCulture),
                MicrophoneGainDb: Convert.ToDouble(MicrophoneGainBox.Value, CultureInfo.InvariantCulture),
                GameGainDb: Convert.ToDouble(GameGainBox.Value, CultureInfo.InvariantCulture)).Validated();
            var state = _creatorState ?? CreatorWorkflowState.Empty(source.Id);
            _creatorState = state with { AudioSettings = settings, ModifiedUtc = DateTimeOffset.UtcNow };
            await SaveCreatorStateAsync();
            RefreshAudioPlan();
            FooterStatusText.Text = "Manual mix settings saved locally and will be used during export.";
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            FooterStatusText.Text = exception.Message;
        }
    }

    private async void SuggestMetadata_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _projectDirectory is null) return;
        var states = await LoadAllCreatorStatesAsync();
        var suggestion = CreatorMetadataSuggestions.Create(
            _project,
            states.ToDictionary(pair => pair.Key, pair => pair.Value.Captions));
        SuggestedTitleTextBox.Text = suggestion.Title;
        SuggestedDescriptionTextBox.Text = suggestion.Description;
        SuggestedChaptersTextBox.Text = CreatorMetadataSuggestions.ToChapterText(suggestion.Chapters);
        FooterStatusText.Text = "Created local title, description, and chapter suggestions. Edit them freely before posting.";
    }

    private void CancelExport_Click(object? sender, RoutedEventArgs e) => _exportCancellation?.Cancel();

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null || _projectDirectory is null || _project.Sources.Count == 0)
        {
            FooterStatusText.Text = "Open a project with an edited timeline before exporting.";
            return;
        }
        if (_project.Sources.Any(source => !source.AudioRolesConfirmed))
        {
            FooterStatusText.Text = "Review and confirm every source's OBS audio roles before exporting.";
            return;
        }
        var kind = ExportKindBox.SelectedIndex == 1 ? RenderKind.Vertical : RenderKind.LongForm;
        var output = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = kind == RenderKind.Vertical ? "Export vertical Short" : "Export long-form highlights",
            SuggestedFileName = $"{_project.Name}{(kind == RenderKind.Vertical ? "-short" : "-highlights")}.mp4",
            DefaultExtension = "mp4",
            FileTypeChoices = [new FilePickerFileType("H.264/AAC MP4") { Patterns = ["*.mp4"] }]
        });
        if (output is null) return;

        _exportCancellation?.Cancel();
        _exportCancellation?.Dispose();
        _exportCancellation = new CancellationTokenSource();
        ExportButton.IsEnabled = false;
        CancelExportButton.IsEnabled = true;
        ExportProgressBar.Value = 0;
        try
        {
            var source = SelectedSource() ?? _project.Sources[0];
            var creatorStates = await LoadAllCreatorStatesAsync(_exportCancellation.Token);
            var state = creatorStates[source.Id];
            var focusedCrop = kind == RenderKind.Vertical && VerticalCompositionBox.SelectedIndex == 1;
            var options = new ProjectRenderOptions(
                kind,
                BurnInCaptionsCheck.IsChecked == true,
                WriteSrtCheck.IsChecked == true,
                WriteVttCheck.IsChecked == true,
                focusedCrop ? VerticalComposition.FocusedCrop : VerticalComposition.FullFrameBlurred,
                FocusXSlider.Value,
                FocusConfidence: focusedCrop ? 1 : 0,
                PreferNvidia: PreferNvidiaCheck.IsChecked == true);
            var progress = new Progress<ProjectRenderProgress>(update =>
            {
                ExportProgressBar.Value = update.Fraction * 100;
                FooterStatusText.Text = $"Export — {update.Stage}: {update.Detail}";
            });
            var result = await ProjectRenderService.RenderAsync(
                new ProjectRenderRequest(
                    new ProjectPaths(_projectDirectory),
                    _project,
                    state,
                    output.Path.LocalPath,
                    options,
                    CreatorStates: creatorStates),
                progress,
                _exportCancellation.Token);
            try
            {
                await WriteMetadataSuggestionsAsync(result.OutputPath, _exportCancellation.Token);
                FooterStatusText.Text = $"Export complete: {result.OutputPath} ({result.OutputLoudness.IntegratedLufs:0.0} LUFS, {result.OutputLoudness.TruePeakDbtp:0.0} dBTP).";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await HighlightForgeLog.ErrorAsync("The video export completed, but metadata suggestions could not be saved.", exception);
                FooterStatusText.Text = $"Video export complete: {result.OutputPath}. Metadata sidecar failed; see {HighlightForgeLog.CurrentLogPath}.";
            }
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = "Export cancelled. The original recordings and previous completed output are unchanged.";
            await HighlightForgeLog.InfoAsync("Project export was cancelled.");
        }
        catch (Exception exception)
        {
            await HighlightForgeLog.ErrorAsync("Project export failed.", exception);
            FooterStatusText.Text = $"Export failed: {exception.Message} Log: {HighlightForgeLog.CurrentLogPath}";
        }
        finally
        {
            ExportButton.IsEnabled = true;
            CancelExportButton.IsEnabled = false;
        }
    }

    private async Task WriteMetadataSuggestionsAsync(string outputPath, CancellationToken cancellationToken)
    {
        if (_project is null) return;
        if (string.IsNullOrWhiteSpace(SuggestedTitleTextBox.Text))
        {
            var states = await LoadAllCreatorStatesAsync(cancellationToken);
            var suggestion = CreatorMetadataSuggestions.Create(
                _project,
                states.ToDictionary(pair => pair.Key, pair => pair.Value.Captions));
            SuggestedTitleTextBox.Text = suggestion.Title;
            SuggestedDescriptionTextBox.Text = suggestion.Description;
            SuggestedChaptersTextBox.Text = CreatorMetadataSuggestions.ToChapterText(suggestion.Chapters);
        }
        var metadataPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
            $"{Path.GetFileNameWithoutExtension(outputPath)}-metadata.txt");
        foreach (var source in _project.Sources) MediaPathSafety.RequireSeparateOutput(source.AbsolutePath, metadataPath, "Metadata export");
        var contents = $"Title{Environment.NewLine}{SuggestedTitleTextBox.Text}{Environment.NewLine}{Environment.NewLine}" +
            $"Description{Environment.NewLine}{SuggestedDescriptionTextBox.Text}{Environment.NewLine}{Environment.NewLine}" +
            $"Chapters{Environment.NewLine}{SuggestedChaptersTextBox.Text}";
        await File.WriteAllTextAsync(metadataPath, contents, cancellationToken);
    }

    private void CandidateList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = CandidateList.SelectedIndex;
        if (_analysisResult is null || index < 0 || index >= _analysisResult.RankedCandidates.Count || _mediaPlayer is null) return;
        SeekSourceTime(_analysisResult.RankedCandidates[index].SourceIn);
    }

    private async void AcceptCandidate_Click(object? sender, RoutedEventArgs e) => await SaveCandidateFeedbackAsync(accepted: true);

    private async void RejectCandidate_Click(object? sender, RoutedEventArgs e) => await SaveCandidateFeedbackAsync(accepted: false);

    private async Task SaveCandidateFeedbackAsync(bool accepted)
    {
        if (_analysisResult is null || _projectDirectory is null) return;
        var index = CandidateList.SelectedIndex;
        if (index < 0 || index >= _analysisResult.RankedCandidates.Count)
        {
            FooterStatusText.Text = "Select a candidate before recording feedback.";
            return;
        }
        var candidate = HighlightScorer.EnsureIdentity(_analysisResult.RankedCandidates[index]);
        var acceptedIds = (_creatorPreferences.AcceptedCandidateIds ?? new HashSet<Guid>()).ToHashSet();
        var rejectedIds = (_creatorPreferences.RejectedCandidateIds ?? new HashSet<Guid>()).ToHashSet();
        if (accepted)
        {
            acceptedIds.Add(candidate.Id);
            rejectedIds.Remove(candidate.Id);
        }
        else
        {
            rejectedIds.Add(candidate.Id);
            acceptedIds.Remove(candidate.Id);
        }
        _creatorPreferences = _creatorPreferences with { AcceptedCandidateIds = acceptedIds, RejectedCandidateIds = rejectedIds };
        await new CreatorPreferencesStore(_projectDirectory).SaveAsync(_creatorPreferences);
        await ReloadAnalysisWithPreferencesAsync();
        FooterStatusText.Text = accepted
            ? "Accepted candidate saved locally and boosted for future re-ranking."
            : "Rejected candidate saved locally and removed from future drafts.";
    }

    private async void ApplyStylePreferences_Click(object? sender, RoutedEventArgs e)
    {
        if (_projectDirectory is null) return;
        _creatorPreferences = _creatorPreferences with
        {
            FunnyWeight = Convert.ToDouble(FunnyWeightBox.Value ?? 1, CultureInfo.InvariantCulture),
            ActionWeight = Convert.ToDouble(ActionWeightBox.Value ?? 1, CultureInfo.InvariantCulture),
            StoryWeight = Convert.ToDouble(StoryWeightBox.Value ?? 1, CultureInfo.InvariantCulture)
        };
        await new CreatorPreferencesStore(_projectDirectory).SaveAsync(_creatorPreferences);
        await ReloadAnalysisWithPreferencesAsync();
        FooterStatusText.Text = "Local style preferences saved and candidates re-ranked without model fine-tuning.";
    }

    private async void RegenerateDraft_Click(object? sender, RoutedEventArgs e)
    {
        var source = SelectedSource();
        if (_project is null || _analysisResult is null || source is null) return;
        var target = _analysisResult.Draft.TotalDuration > TimeSpan.Zero ? _analysisResult.Draft.TotalDuration : TimeSpan.FromMinutes(5);
        var draft = HighlightScorer.BuildDraft(_analysisResult.RankedCandidates, target);
        _analysisResult = _analysisResult with { Draft = draft };
        await ApplyTimelineAsync(AssembleDraftTimeline(source, draft), "Regenerated the draft while preserving locked clips.");
    }

    private void AudioTrackList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var source = SelectedSource();
        if (source is null) return;
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
        var source = SelectedSource();
        if (_project is null || _projectStore is null || source is null) return;
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
        var source = SelectedSource();
        if (_project is null || _projectStore is null || source is null) return;
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

    private async void TimelineClipsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var clip = SelectedTimelineClip();
        if (clip is null) return;
        PopulateClipAdjustments(clip);
        if (_project is not null && _previewSourceId != clip.SourceId)
        {
            var source = _project.Sources.FirstOrDefault(item => item.Id == clip.SourceId);
            if (source is not null)
            {
                _activeSourceId = source.Id;
                _updatingSourceSelection = true;
                SourceListBox.SelectedIndex = _project.Sources.ToList().FindIndex(item => item.Id == source.Id);
                _updatingSourceSelection = false;
                MediaDetailsText.Text = $"{source.Width}×{source.Height} • {FormatDuration(source.Duration)} • {source.FramesPerSecond:0.##} fps";
                RefreshTrackInspector(source);
                await LoadAnalysisResultAsync();
                await LoadCreatorStateAsync();
                await RefreshMediaCacheAsync();
                await OpenSourceAsync(source);
            }
        }
        if (_mediaPlayer is null) return;
        SeekSourceTime(clip.SourceIn);
    }

    private async void SplitClip_Click(object? sender, RoutedEventArgs e)
    {
        var clip = SelectedTimelineClip();
        if (clip is null || _project is null || _mediaPlayer is null) return;
        if (RejectLockedEdit(clip)) return;
        var playhead = CurrentSourceTime();
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
        if (RejectLockedEdit(clip)) return;
        var playhead = CurrentSourceTime();
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
        if (RejectLockedEdit(clip)) return;
        await ApplyTimelineAsync(TimelineEditor.DeleteWithRipple(_project.Timeline, clip.Id), "Ripple-deleted the selected clip.");
    }

    private async void MoveClipUp_Click(object? sender, RoutedEventArgs e) => await MoveSelectedClipAsync(-1);
    private async void MoveClipDown_Click(object? sender, RoutedEventArgs e) => await MoveSelectedClipAsync(1);

    private async Task MoveSelectedClipAsync(int offset)
    {
        var clip = SelectedTimelineClip();
        if (clip is null || _project is null) return;
        if (RejectLockedEdit(clip)) return;
        var currentIndex = _project.Timeline.ToList().FindIndex(item => item.Id == clip.Id);
        var destination = Math.Clamp(currentIndex + offset, 0, _project.Timeline.Count - 1);
        if (destination == currentIndex) return;
        await ApplyTimelineAsync(TimelineEditor.Move(_project.Timeline, clip.Id, destination), "Reordered the selected clip.");
        TimelineClipsList.SelectedIndex = destination;
    }

    private async void ApplyClipAdjustments_Click(object? sender, RoutedEventArgs e)
    {
        var clip = SelectedTimelineClip();
        if (clip is null || _project is null || RejectLockedEdit(clip)) return;
        try
        {
            var updated = TimelineEditor.SetAdjustments(
                _project.Timeline,
                clip.Id,
                Convert.ToDouble(ClipGainBox.Value ?? 0, CultureInfo.InvariantCulture),
                TimeSpan.FromSeconds(Convert.ToDouble(ClipFadeInBox.Value ?? 0, CultureInfo.InvariantCulture)),
                TimeSpan.FromSeconds(Convert.ToDouble(ClipFadeOutBox.Value ?? 0, CultureInfo.InvariantCulture)),
                ClipPunchZoomCheck.IsChecked == true,
                Convert.ToDouble(ClipCropScaleBox.Value ?? 1, CultureInfo.InvariantCulture),
                Convert.ToDouble(ClipReframeXBox.Value ?? 0.5m, CultureInfo.InvariantCulture),
                Convert.ToDouble(ClipReframeYBox.Value ?? 0.5m, CultureInfo.InvariantCulture));
            await ApplyTimelineAsync(updated, "Applied non-destructive gain, fades, and reframing.");
        }
        catch (ArgumentOutOfRangeException exception)
        {
            FooterStatusText.Text = exception.Message;
        }
    }

    private async void ToggleClipLock_Click(object? sender, RoutedEventArgs e)
    {
        var clip = SelectedTimelineClip();
        if (clip is null || _project is null) return;
        await ApplyTimelineAsync(TimelineEditor.ToggleLock(_project.Timeline, clip.Id), clip.IsLocked ? "Unlocked clip." : "Locked clip against accidental edits.");
    }

    private void PopulateClipAdjustments(TimelineClip clip)
    {
        ClipGainBox.Value = Convert.ToDecimal(clip.GainDb, CultureInfo.InvariantCulture);
        ClipFadeInBox.Value = Convert.ToDecimal(clip.FadeIn.TotalSeconds, CultureInfo.InvariantCulture);
        ClipFadeOutBox.Value = Convert.ToDecimal(clip.FadeOut.TotalSeconds, CultureInfo.InvariantCulture);
        ClipPunchZoomCheck.IsChecked = clip.PunchZoom;
        ClipCropScaleBox.Value = Convert.ToDecimal(clip.CropScale, CultureInfo.InvariantCulture);
        ClipReframeXBox.Value = Convert.ToDecimal(clip.ReframeX, CultureInfo.InvariantCulture);
        ClipReframeYBox.Value = Convert.ToDecimal(clip.ReframeY, CultureInfo.InvariantCulture);
        ToggleClipLockButton.Content = clip.IsLocked ? "Unlock clip" : "Lock clip";
    }

    private bool RejectLockedEdit(TimelineClip clip)
    {
        if (!clip.IsLocked) return false;
        FooterStatusText.Text = "Unlock the selected clip before changing it.";
        return true;
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
            _activeSourceId ??= _project.Sources[0].Id;
            var source = SelectedSource() ?? _project.Sources[0];
            await OpenSourceAsync(source);
        }
    }

    private async Task OpenSourceAsync(MediaSource source)
    {
        try
        {
            _media?.Dispose();
            if (_libVlc is null || _mediaPlayer is null) throw new InvalidOperationException("The local video preview is unavailable.");
            var cache = _projectDirectory is null ? null : await MediaCacheService.TryLoadAsync(new ProjectPaths(_projectDirectory), source);
            var previewPath = cache?.ProxyPath ?? source.AbsolutePath;
            _previewTimeMap = cache?.TimeMap;
            _previewSourceId = source.Id;
            _media = new VlcMedia(_libVlc, previewPath, FromType.FromPath);
            _mediaPlayer.Media = _media;
            _mediaPlayer.Play();
            PlayPauseButton.Content = "Pause";
            await HighlightForgeLog.InfoAsync(cache is null
                ? $"Opened source preview for '{source.AbsolutePath}'."
                : $"Opened disposable proxy preview for source '{source.AbsolutePath}'.");
        }
        catch (Exception exception)
        {
            await HighlightForgeLog.ErrorAsync("Project video preview failed.", exception);
            FooterStatusText.Text = $"Could not preview this recording. Log: {HighlightForgeLog.CurrentLogPath}";
        }
    }

    private async Task GeneratePreviewCacheAsync(MediaSource source)
    {
        if (_projectDirectory is null) return;
        _mediaCacheCancellation?.Cancel();
        _mediaCacheCancellation?.Dispose();
        _mediaCacheCancellation = new CancellationTokenSource();
        GenerateCacheButton.IsEnabled = false;
        CancelCacheButton.IsEnabled = true;
        try
        {
            var progress = new Progress<MediaCacheProgress>(update =>
            {
                MediaCacheProgressBar.Value = update.Fraction * 100;
                MediaCacheStatusText.Text = $"{update.Stage}: {update.Detail}";
            });
            var bundle = await MediaCacheService.GenerateAsync(
                new ProjectPaths(_projectDirectory),
                source,
                progress,
                _mediaCacheCancellation.Token);
            await DisplayMediaCacheAsync(bundle);
            var position = CurrentSourceTime();
            await OpenSourceAsync(source);
            SeekSourceTime(position);
            FooterStatusText.Text = "Preview proxy, thumbnails, and waveform are ready in the disposable project cache.";
        }
        catch (OperationCanceledException)
        {
            MediaCacheStatusText.Text = "Preview-cache generation cancelled; partial cache data was removed.";
            await HighlightForgeLog.InfoAsync("Preview-cache generation was cancelled.");
        }
        catch (Exception exception)
        {
            await HighlightForgeLog.ErrorAsync("Preview-cache generation failed.", exception);
            MediaCacheStatusText.Text = $"Preview cache failed: {exception.Message}";
        }
        finally
        {
            GenerateCacheButton.IsEnabled = true;
            CancelCacheButton.IsEnabled = false;
        }
    }

    private async Task RefreshMediaCacheAsync()
    {
        var source = SelectedSource();
        if (_project is null || _projectDirectory is null || source is null)
        {
            DisposeCacheImages();
            MediaCacheStatusText.Text = "No recording selected.";
            return;
        }
        var bundle = await MediaCacheService.TryLoadAsync(new ProjectPaths(_projectDirectory), source);
        if (bundle is null)
        {
            DisposeCacheImages();
            MediaCacheStatusText.Text = "No source-matched preview cache. Choose Build to create one.";
            return;
        }
        await DisplayMediaCacheAsync(bundle);
    }

    private Task DisplayMediaCacheAsync(MediaCacheBundle bundle)
    {
        DisposeCacheImages();
        if (bundle.WaveformPath is not null) _waveformBitmap = new Bitmap(bundle.WaveformPath);
        WaveformImage.Source = _waveformBitmap;
        foreach (var path in bundle.ThumbnailPaths.Take(30)) _thumbnailBitmaps.Add(new Bitmap(path));
        ThumbnailStrip.ItemsSource = _thumbnailBitmaps.ToArray();
        MediaCacheProgressBar.Value = 100;
        MediaCacheStatusText.Text = $"Ready: proxy, {bundle.ThumbnailPaths.Count} thumbnails{(bundle.WaveformPath is null ? string.Empty : ", waveform")}.";
        return Task.CompletedTask;
    }

    private void DisposeCacheImages()
    {
        WaveformImage.Source = null;
        ThumbnailStrip.ItemsSource = null;
        _waveformBitmap?.Dispose();
        _waveformBitmap = null;
        foreach (var thumbnail in _thumbnailBitmaps) thumbnail.Dispose();
        _thumbnailBitmaps.Clear();
    }

    private TimeSpan CurrentSourceTime()
    {
        var previewTime = TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer?.Time ?? 0));
        return _previewTimeMap?.ProxyToSource(previewTime) ?? previewTime;
    }

    private void SeekSourceTime(TimeSpan sourceTime)
    {
        if (_mediaPlayer is null) return;
        var previewTime = _previewTimeMap?.SourceToProxy(sourceTime) ?? sourceTime;
        _mediaPlayer.Time = (long)Math.Max(0, previewTime.TotalMilliseconds);
    }

    private TimeSpan CurrentTimelineTime()
    {
        var clip = SelectedTimelineClip();
        var sourceTime = CurrentSourceTime();
        return clip is not null && sourceTime >= clip.SourceIn && sourceTime <= clip.SourceOut
            ? clip.TimelineIn + sourceTime - clip.SourceIn
            : TimeSpan.Zero;
    }

    private async Task LoadAnalysisResultAsync()
    {
        var source = SelectedSource();
        if (_project is null || _projectDirectory is null || source is null) return;
        _analysisResult = await new AnalysisResultStore(new ProjectPaths(_projectDirectory)).LoadAsync(source.Id);
        ApplyPreferencesToAnalysis();
        CandidateList.ItemsSource = _analysisResult is null ? Array.Empty<string>() : BuildCandidateLabels(_analysisResult);
        AnalysisProgressBar.Value = _analysisResult is null ? 0 : 100;
    }

    private async Task LoadCreatorStateAsync()
    {
        var source = SelectedSource();
        if (_project is null || _projectDirectory is null || source is null) return;
        _creatorState = await new CreatorWorkflowStore(new ProjectPaths(_projectDirectory)).LoadAsync(source.Id);
        RefreshCreatorControls();
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

    private async Task<IReadOnlyDictionary<Guid, CreatorWorkflowState>> LoadAllCreatorStatesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_project is null || _projectDirectory is null) return new Dictionary<Guid, CreatorWorkflowState>();
        var store = new CreatorWorkflowStore(new ProjectPaths(_projectDirectory));
        var states = new Dictionary<Guid, CreatorWorkflowState>();
        foreach (var source in _project.Sources)
        {
            states[source.Id] = _creatorState?.SourceId == source.Id
                ? _creatorState
                : await store.LoadAsync(source.Id, cancellationToken);
        }
        return states;
    }

    private async Task LoadPreferencesAsync()
    {
        if (_projectDirectory is null) return;
        var loaded = await new CreatorPreferencesStore(_projectDirectory).LoadAsync();
        var accepted = (loaded.AcceptedCandidateIds ?? new HashSet<Guid>()).ToHashSet();
        var rejected = (loaded.RejectedCandidateIds ?? new HashSet<Guid>()).Where(id => !accepted.Contains(id)).ToHashSet();
        _creatorPreferences = loaded with
        {
            FunnyWeight = Math.Clamp(double.IsFinite(loaded.FunnyWeight) ? loaded.FunnyWeight : 1, 0.5, 2),
            ActionWeight = Math.Clamp(double.IsFinite(loaded.ActionWeight) ? loaded.ActionWeight : 1, 0.5, 2),
            StoryWeight = Math.Clamp(double.IsFinite(loaded.StoryWeight) ? loaded.StoryWeight : 1, 0.5, 2),
            AcceptedCandidateIds = accepted,
            RejectedCandidateIds = rejected
        };
        FunnyWeightBox.Value = Convert.ToDecimal(_creatorPreferences.FunnyWeight, CultureInfo.InvariantCulture);
        ActionWeightBox.Value = Convert.ToDecimal(_creatorPreferences.ActionWeight, CultureInfo.InvariantCulture);
        StoryWeightBox.Value = Convert.ToDecimal(_creatorPreferences.StoryWeight, CultureInfo.InvariantCulture);
    }

    private async Task ReloadAnalysisWithPreferencesAsync()
    {
        await LoadAnalysisResultAsync();
        RefreshVoiceoverSuggestions();
    }

    private void ApplyPreferencesToAnalysis()
    {
        if (_analysisResult is null) return;
        var ranked = HighlightScorer.Rerank(_analysisResult.RankedCandidates, _creatorPreferences);
        var target = _analysisResult.Draft.TotalDuration > TimeSpan.Zero ? _analysisResult.Draft.TotalDuration : TimeSpan.FromMinutes(5);
        _analysisResult = _analysisResult with { RankedCandidates = ranked, Draft = HighlightScorer.BuildDraft(ranked, target) };
    }

    private IReadOnlyList<TimelineClip> AssembleDraftTimeline(MediaSource source, HighlightDraft draft)
    {
        var locked = _project?.Timeline.Where(clip => clip.IsLocked).OrderBy(clip => clip.TimelineIn).ToList() ?? [];
        IReadOnlyList<TimelineClip> timeline = TimelineEditor.Normalize(locked);
        foreach (var candidate in draft.Clips)
        {
            if (locked.Any(clip => clip.SourceId == source.Id && clip.SourceIn < candidate.SourceOut && candidate.SourceIn < clip.SourceOut)) continue;
            timeline = TimelineEditor.Append(timeline, source.Id, candidate.SourceIn, candidate.SourceOut);
        }
        return timeline;
    }

    private void BeginCreatorOperation()
    {
        _creatorCancellation?.Cancel();
        _creatorCancellation?.Dispose();
        _creatorCancellation = new CancellationTokenSource();
    }

    private async Task RefreshModelStatusAsync()
    {
        if (ModelStatusText is null || AnalysisModeBox is null) return;
        var selectedMode = SelectedAnalysisMode();
        var installer = new WhisperModelInstaller(_httpClient);
        var manager = new ModelPackManager(WhisperModelCatalog.DefaultRootDirectory);
        var statuses = new List<string>();
        foreach (var pack in WhisperModelCatalog.All)
        {
            var path = await installer.GetInstalledModelPathAsync(pack);
            var active = await manager.GetActiveVersionAsync(pack.Manifest.Id);
            var prefix = pack.Mode == selectedMode ? "▶" : " ";
            statuses.Add(path is null
                ? $"{prefix} {pack.DisplayName}: not installed ({pack.DownloadSize / 1_000_000d:0} MB)"
                : active is null
                    ? $"{prefix} {pack.DisplayName}: verified, offline ready"
                    : $"{prefix} {pack.DisplayName}: verified, offline ready (active {active.Manifest.Version})");
        }
        var yamnet = await new YamnetModelInstaller(_httpClient).GetInstalledDirectoryAsync();
        statuses.Add(yamnet is null
            ? "  YAMNet sound events: not installed (included with any model install)"
            : "  YAMNet sound events: verified, offline ready");
        var ocr = await new OcrModelInstaller(_httpClient).GetInstalledDirectoryAsync();
        statuses.Add(ocr is null
            ? "  English OCR: not installed (included with any model install)"
            : "  English OCR: verified, offline ready");
        var florence = await new FlorenceModelInstaller(_httpClient).GetInstalledDirectoryAsync();
        statuses.Add(florence is null
            ? "  Florence-2 visual context: not installed (Balanced/Deep packs)"
            : "  Florence-2 visual context: verified, offline ready");
        var phi = await new PhiModelInstaller(_httpClient).GetInstalledDirectoryAsync();
        statuses.Add(phi is null
            ? $"  Phi-4 mini narrative: not installed (Balanced/Deep, {PhiModelCatalog.MiniInstructCpuInt4.DownloadSize / 1_000_000_000d:0.00} GB)"
            : "  Phi-4 mini narrative: verified, offline ready");
        ModelStatusText.Text = string.Join(Environment.NewLine, statuses);
    }

    private void RefreshCaptions()
    {
        CaptionList.ItemsSource = _creatorState?.Captions.Select((cue, index) =>
            $"{index + 1}. {FormatDuration(cue.Start)}–{FormatDuration(cue.End)}  {cue.Text}").ToArray() ?? [];
    }

    private IReadOnlyList<VoiceoverSuggestion> CurrentVoiceoverSuggestions()
    {
        if (_analysisResult is null || _project is null || _creatorState is null) return [];
        return VoiceoverPlanner.SuggestForTimeline(
            _analysisResult.RankedCandidates,
            _project.Timeline,
            _creatorState.SourceId,
            _analysisResult.NarrativeSuggestions);
    }

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
        var settings = CurrentAudioSettings();
        AudioPlanText.Text = discrete
            ? $"Plan: mic {settings.MicrophoneGainDb:+0.0;-0.0;0.0} dB, game {settings.GameGainDb:+0.0;-0.0;0.0} dB, duck {settings.DuckingDb:0} dB ({settings.DuckingAttackMs} ms attack / {settings.DuckingReleaseMs} ms release), exclude the combined OBS track, then normalize to −14 LUFS / −1 dBTP."
            : "Plan: preserve the single mixed track and normalize the final mix to −14 LUFS and at most −1 dBTP.";
    }

    private AudioMixSettings CurrentAudioSettings() => (_creatorState?.AudioSettings ?? new AudioMixSettings()).Validated();

    private void RefreshCreatorControls()
    {
        var style = (_creatorState?.CaptionStyle ?? new CaptionStyleSettings()).Validated();
        CaptionFontBox.Text = style.FontName;
        CaptionFontSizeBox.Value = style.FontSize;
        CaptionColorBox.Text = style.PrimaryColor;
        CaptionPlacementBox.SelectedIndex = style.Placement switch
        {
            CaptionPlacement.Middle => 1,
            CaptionPlacement.Top => 2,
            _ => 0
        };

        var audio = CurrentAudioSettings();
        MicrophoneGainBox.Value = Convert.ToDecimal(audio.MicrophoneGainDb, CultureInfo.InvariantCulture);
        GameGainBox.Value = Convert.ToDecimal(audio.GameGainDb, CultureInfo.InvariantCulture);
        DuckingGainBox.Value = Convert.ToDecimal(audio.DuckingDb, CultureInfo.InvariantCulture);
        DuckingAttackBox.Value = audio.DuckingAttackMs;
        DuckingReleaseBox.Value = audio.DuckingReleaseMs;
    }

    private string[] BuildCandidateLabels(LocalAnalysisResult result) => result.RankedCandidates
        .Select((candidate, index) =>
        {
            var reasons = string.Join(", ", candidate.Reasons.Take(2).Select(reason => reason.Detail));
            var feedback = (_creatorPreferences.AcceptedCandidateIds?.Contains(candidate.Id) ?? false) ? "accepted | " : string.Empty;
            return $"{index + 1}. {feedback}{FormatDuration(candidate.SourceIn)}-{FormatDuration(candidate.SourceOut)} | score {candidate.Score:0.00} | {reasons}";
        })
        .ToArray();

    private TimelineClip? SelectedTimelineClip()
    {
        if (_project is null) return null;
        var index = TimelineClipsList.SelectedIndex;
        return index >= 0 && index < _project.Timeline.Count ? _project.Timeline[index] : null;
    }

    private MediaSource? SelectedSource()
    {
        if (_project is null || _project.Sources.Count == 0) return null;
        return _activeSourceId is { } active
            ? _project.Sources.FirstOrDefault(source => source.Id == active) ?? _project.Sources[0]
            : _project.Sources[0];
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
            var settings = $"{clip.GainDb:+0.#;-0.#;0} dB" +
                (clip.FadeIn > TimeSpan.Zero || clip.FadeOut > TimeSpan.Zero ? $" | fades {clip.FadeIn.TotalSeconds:0.#}/{clip.FadeOut.TotalSeconds:0.#}s" : string.Empty) +
                (clip.PunchZoom ? " | punch zoom" : clip.CropScale > 1 ? $" | crop {clip.CropScale:0.##}×" : string.Empty) +
                (clip.IsLocked ? " | locked" : string.Empty);
            return $"{index + 1}. {name} | {FormatDuration(clip.SourceIn)}-{FormatDuration(clip.SourceOut)} | {FormatDuration(clip.SourceOut - clip.SourceIn)} | {settings}";
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
        var source = SelectedSource();
        if (source is null)
        {
            SourceListBox.ItemsSource = Array.Empty<string>();
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
        _updatingSourceSelection = true;
        SourceListBox.ItemsSource = _project.Sources.Select(item => Path.GetFileName(item.AbsolutePath)).ToArray();
        SourceListBox.SelectedIndex = _project.Sources.ToList().FindIndex(item => item.Id == source.Id);
        _updatingSourceSelection = false;
        SourceButton.Content = "Open selected source";
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
