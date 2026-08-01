using System.IO.Pipes;
using System.Text.Json;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Audio;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Models;
using HighlightForge.Core.Persistence;
using HighlightForge.Core.Release;
using HighlightForge.Media.Analysis;
using HighlightForge.Media.Audio;
using HighlightForge.Media.Captions;
using HighlightForge.Media.Import;
using HighlightForge.Media.Models;
using HighlightForge.Media.Proxy;
using HighlightForge.Media.Render;
using Microsoft.ML.OnnxRuntime;

if (args.Contains("--health", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(JsonSerializer.Serialize(new { service = "HighlightForge.Worker", status = "ready", localOnly = true }));
    return;
}

var inspectOnnxArgument = Array.FindIndex(args, argument => string.Equals(argument, "--inspect-onnx", StringComparison.OrdinalIgnoreCase));
if (inspectOnnxArgument >= 0)
{
    if (args.Length <= inspectOnnxArgument + 1)
    {
        Console.Error.WriteLine("Usage: HighlightForge.Worker --inspect-onnx <model.onnx>");
        Environment.ExitCode = 2;
        return;
    }
    using var session = new InferenceSession(args[inspectOnnxArgument + 1]);
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
    {
        inputs = session.InputMetadata.ToDictionary(pair => pair.Key, pair => new { ElementType = pair.Value.ElementType.Name, pair.Value.Dimensions }),
        outputs = session.OutputMetadata.ToDictionary(pair => pair.Key, pair => new { ElementType = pair.Value.ElementType.Name, pair.Value.Dimensions })
    }));
    return;
}

var cacheArgument = Array.FindIndex(args, argument => string.Equals(argument, "--cache-source", StringComparison.OrdinalIgnoreCase));
if (cacheArgument >= 0)
{
    if (args.Length <= cacheArgument + 2)
    {
        Console.Error.WriteLine("Usage: HighlightForge.Worker --cache-source <project-directory> <media-path>");
        Environment.ExitCode = 2;
        return;
    }
    var paths = new ProjectPaths(args[cacheArgument + 1]);
    var imported = await SourceImportService.ImportAsync(args[cacheArgument + 2]);
    var progress = new Progress<MediaCacheProgress>(update => Console.Error.WriteLine($"cache {update.Fraction:P0} {update.Stage}: {update.Detail}"));
    var bundle = await MediaCacheService.GenerateAsync(paths, imported.Source, progress);
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(bundle));
    return;
}

var transcribeArgument = Array.FindIndex(args, argument => string.Equals(argument, "--transcribe-source", StringComparison.OrdinalIgnoreCase));
if (transcribeArgument >= 0)
{
    if (args.Length <= transcribeArgument + 3)
    {
        Console.Error.WriteLine("Usage: HighlightForge.Worker --transcribe-source <project-directory> <media-path> <Fast|Balanced|Deep> [limit-seconds] [start-seconds]");
        Environment.ExitCode = 2;
        return;
    }
    var projectDirectory = args[transcribeArgument + 1];
    var mediaPath = args[transcribeArgument + 2];
    if (!Enum.TryParse<AnalysisMode>(args[transcribeArgument + 3], ignoreCase: true, out var mode))
    {
        Console.Error.WriteLine("Transcription mode must be Fast, Balanced, or Deep.");
        Environment.ExitCode = 2;
        return;
    }
    var paths = new ProjectPaths(projectDirectory);
    TimeSpan? limit = args.Length > transcribeArgument + 4 && double.TryParse(args[transcribeArgument + 4], System.Globalization.CultureInfo.InvariantCulture, out var limitSeconds)
        ? TimeSpan.FromSeconds(limitSeconds)
        : null;
    TimeSpan? start = args.Length > transcribeArgument + 5 && double.TryParse(args[transcribeArgument + 5], System.Globalization.CultureInfo.InvariantCulture, out var startSeconds)
        ? TimeSpan.FromSeconds(startSeconds)
        : null;
    var imported = await SourceImportService.ImportAsync(mediaPath);
    var pack = WhisperModelCatalog.ForMode(mode);
    using var client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
    var installer = new WhisperModelInstaller(client);
    var lastDownloadPercent = -1;
    var downloadProgress = new Progress<ModelDownloadProgress>(update =>
    {
        var percent = (int)(update.Fraction * 100);
        if (percent == lastDownloadPercent) return;
        lastDownloadPercent = percent;
        Console.Error.WriteLine($"model {update.Fraction:P0}");
    });
    var modelPath = await installer.GetActiveModelPathAsync(pack) ?? await installer.InstallAsync(pack, downloadProgress);
    var transcriptionProgress = new Progress<TranscriptionProgress>(update => Console.Error.WriteLine($"transcription {update.Fraction:P0} {update.Detail}"));
    var captions = await WhisperTranscriptionService.TranscribeAsync(paths, imported.Source, modelPath, transcriptionProgress, sourceStart: start, sourceLimit: limit);
    var state = CreatorWorkflowState.Empty(imported.Source.Id) with { Captions = captions, ModifiedUtc = DateTimeOffset.UtcNow };
    await new CreatorWorkflowStore(paths).SaveAsync(state);
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
    {
        source = imported.Source.AbsolutePath,
        originalLastWriteUtc = File.GetLastWriteTimeUtc(imported.Source.AbsolutePath),
        projectDirectory = paths.ProjectDirectory,
        modelPath,
        captions
    }));
    return;
}

var measureArgument = Array.FindIndex(args, argument => string.Equals(argument, "--measure-source", StringComparison.OrdinalIgnoreCase));
if (measureArgument >= 0)
{
    if (args.Length <= measureArgument + 2)
    {
        Console.Error.WriteLine("Usage: HighlightForge.Worker --measure-source <project-directory> <media-path>");
        Environment.ExitCode = 2;
        return;
    }
    var paths = new ProjectPaths(args[measureArgument + 1]);
    var imported = await SourceImportService.ImportAsync(args[measureArgument + 2]);
    var source = imported.Source;
    var usesDiscrete = source.AudioTracks.Any(track => track.Role == AudioTrackRole.Microphone) && source.AudioTracks.Any(track => track.Role == AudioTrackRole.Game);
    var plan = AudioMixPlanner.Create(source.AudioTracks, usesDiscrete);
    var measurements = new List<AudioLoudnessMeasurement>();
    foreach (var track in plan.InputTracks) measurements.Add(await AudioLoudnessAnalyzer.MeasureAsync(source.AbsolutePath, track));
    if (plan.UsesDiscreteTracks && measurements.All(measurement => measurement.IntegratedLufs <= -70))
    {
        var mixed = source.AudioTracks.FirstOrDefault(track => track.Role == AudioTrackRole.Mixed);
        if (mixed is not null)
        {
            var fallback = await AudioLoudnessAnalyzer.MeasureAsync(source.AbsolutePath, mixed);
            if (fallback.IntegratedLufs > -70) measurements = [fallback];
        }
    }
    var store = new CreatorWorkflowStore(paths);
    var state = await store.LoadAsync(source.Id);
    await store.SaveAsync(state with { LoudnessMeasurements = measurements, ModifiedUtc = DateTimeOffset.UtcNow });
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
    {
        source = source.AbsolutePath,
        originalLastWriteUtc = File.GetLastWriteTimeUtc(source.AbsolutePath),
        projectDirectory = paths.ProjectDirectory,
        plan.UsesDiscreteTracks,
        plan.Explanation,
        measurements
    }));
    return;
}

var renderArgument = Array.FindIndex(args, argument => string.Equals(argument, "--render-source", StringComparison.OrdinalIgnoreCase));
if (renderArgument >= 0)
{
    if (args.Length <= renderArgument + 4)
    {
        Console.Error.WriteLine("Usage: HighlightForge.Worker --render-source <project-directory> <media-path> <output.mp4> <LongForm|Vertical> [limit-seconds] [start-seconds] [adjusted]");
        Environment.ExitCode = 2;
        return;
    }
    var paths = new ProjectPaths(args[renderArgument + 1]);
    var imported = await SourceImportService.ImportAsync(args[renderArgument + 2]);
    var outputPath = args[renderArgument + 3];
    if (!Enum.TryParse<RenderKind>(args[renderArgument + 4], ignoreCase: true, out var kind))
    {
        Console.Error.WriteLine("Render kind must be LongForm or Vertical.");
        Environment.ExitCode = 2;
        return;
    }
    var limit = args.Length > renderArgument + 5 && double.TryParse(args[renderArgument + 5], System.Globalization.CultureInfo.InvariantCulture, out var limitSeconds)
        ? TimeSpan.FromSeconds(limitSeconds)
        : imported.Source.Duration;
    var start = args.Length > renderArgument + 6 && double.TryParse(args[renderArgument + 6], System.Globalization.CultureInfo.InvariantCulture, out var startSeconds)
        ? TimeSpan.FromSeconds(startSeconds)
        : TimeSpan.Zero;
    var source = imported.Source with { AudioRolesConfirmed = true };
    var end = start + limit > source.Duration ? source.Duration : start + limit;
    var clip = new TimelineClip(Guid.NewGuid(), source.Id, start, end, TimeSpan.Zero);
    if (args.Length > renderArgument + 7 && string.Equals(args[renderArgument + 7], "adjusted", StringComparison.OrdinalIgnoreCase))
    {
        clip = clip with
        {
            GainDb = -2,
            FadeIn = TimeSpan.FromSeconds(0.3),
            FadeOut = TimeSpan.FromSeconds(0.4),
            PunchZoom = true,
            ReframeX = 0.65,
            ReframeY = 0.45
        };
    }
    var project = ProjectDocument.Create("Render validation", DateTimeOffset.UtcNow) with
    {
        Sources = [source],
        Timeline = [clip]
    };
    var captionEnd = start + TimeSpan.FromSeconds(3) > end ? end : start + TimeSpan.FromSeconds(3);
    var creatorState = CreatorWorkflowState.Empty(source.Id) with
    {
        Captions = [new HighlightForge.Core.Captions.CaptionCue(start, captionEnd, "HighlightForge local caption")]
    };
    var progress = new Progress<ProjectRenderProgress>(update => Console.Error.WriteLine($"render {update.Fraction:P0} {update.Stage}: {update.Detail}"));
    var result = await ProjectRenderService.RenderAsync(
        new ProjectRenderRequest(paths, project, creatorState, outputPath, new ProjectRenderOptions(kind)),
        progress);
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result));
    return;
}

var benchmarkArgument = Array.FindIndex(args, argument => string.Equals(argument, "--benchmark", StringComparison.OrdinalIgnoreCase));
if (benchmarkArgument >= 0)
{
    if (args.Length <= benchmarkArgument + 1)
    {
        Console.Error.WriteLine("Usage: HighlightForge.Worker --benchmark <creator-benchmark.json>");
        Environment.ExitCode = 2;
        return;
    }
    var benchmarkPath = Path.GetFullPath(args[benchmarkArgument + 1]);
    var sessions = JsonSerializer.Deserialize<IReadOnlyList<CreatorBenchmarkSession>>(
        await File.ReadAllTextAsync(benchmarkPath),
        new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Creator benchmark JSON is invalid.");
    var report = CreatorBenchmarkGate.Evaluate(sessions);
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(report));
    Environment.ExitCode = report.Passed ? 0 : 1;
    return;
}

var clientAnalysisArgument = Array.FindIndex(args, argument => string.Equals(argument, "--worker-client-source", StringComparison.OrdinalIgnoreCase));
if (clientAnalysisArgument >= 0)
{
    if (args.Length <= clientAnalysisArgument + 3)
    {
        Console.Error.WriteLine("Usage: HighlightForge.Worker --worker-client-source <project-directory> <media-path> <Fast|Balanced|Deep> [pause-after-ms] [source-id]");
        Environment.ExitCode = 2;
        return;
    }
    var paths = new ProjectPaths(args[clientAnalysisArgument + 1]);
    var imported = await SourceImportService.ImportAsync(args[clientAnalysisArgument + 2]);
    var source = args.Length > clientAnalysisArgument + 5 && Guid.TryParse(args[clientAnalysisArgument + 5], out var sourceId)
        ? imported.Source with { Id = sourceId }
        : imported.Source;
    if (!Enum.TryParse<AnalysisMode>(args[clientAnalysisArgument + 3], ignoreCase: true, out var mode))
    {
        Console.Error.WriteLine("Analysis mode must be Fast, Balanced, or Deep.");
        Environment.ExitCode = 2;
        return;
    }
    using var cancellation = new CancellationTokenSource();
    if (args.Length > clientAnalysisArgument + 4 && int.TryParse(args[clientAnalysisArgument + 4], out var pauseAfterMilliseconds))
    {
        cancellation.CancelAfter(pauseAfterMilliseconds);
    }
    var progress = new SynchronousProgress<AnalysisWorkerMessage>(message =>
        Console.Error.WriteLine($"worker-client {message.Kind} {message.Progress:P0}: {message.Detail}"));
    try
    {
        var result = await AnalysisWorkerClient.AnalyzeAsync(paths, source, mode, progress, cancellation.Token);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result));
    }
    catch (OperationCanceledException)
    {
        var checkpoint = await new AnalysisJobStore(paths).LoadLatestForSourceAsync(source.Id, mode);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(checkpoint));
        Environment.ExitCode = 3;
    }
    return;
}

var multimodalArgument = Array.FindIndex(args, argument => string.Equals(argument, "--multimodal-source", StringComparison.OrdinalIgnoreCase));
if (multimodalArgument >= 0)
{
    if (args.Length <= multimodalArgument + 4)
    {
        Console.Error.WriteLine("Usage: HighlightForge.Worker --multimodal-source <project-directory> <media-path> <yamnet-directory> <ocr-directory> [limit-seconds] [florence-directory]");
        Environment.ExitCode = 2;
        return;
    }
    var paths = new ProjectPaths(args[multimodalArgument + 1]);
    var imported = await SourceImportService.ImportAsync(args[multimodalArgument + 2]);
    var track = imported.Source.AudioTracks.Count == 0 ? null : imported.Source.AudioTracks[0];
    var limit = args.Length > multimodalArgument + 5 && double.TryParse(args[multimodalArgument + 5], System.Globalization.CultureInfo.InvariantCulture, out var seconds)
        ? TimeSpan.FromSeconds(seconds)
        : TimeSpan.FromSeconds(Math.Min(30, imported.Source.Duration.TotalSeconds));
    IReadOnlyList<FeatureEvent> soundEvents = track is null
        ? []
        : await YamnetSoundEventAnalyzer.AnalyzeAsync(
            paths,
            imported.Source,
            track,
            [new FeatureEvent(FeatureKind.GameAudioPeak, TimeSpan.Zero, TimeSpan.FromSeconds(1), 0.9, "diagnostic window")],
            args[multimodalArgument + 3],
            AnalysisMode.Balanced);
    var ocrEvents = await OcrFeatureAnalyzer.AnalyzeAsync(
        paths,
        imported.Source,
        AnalysisMode.Balanced,
        args[multimodalArgument + 4],
        analysisLimit: limit);
    IReadOnlyList<FeatureEvent> visualContext = args.Length > multimodalArgument + 6
        ? await FlorenceVisualContextAnalyzer.AnalyzeAsync(
            imported.Source,
            AnalysisMode.Balanced,
            args[multimodalArgument + 6],
            analysisLimit: limit)
        : [];
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { soundEvents, ocrEvents, visualContext }));
    return;
}

var sourceArgument = Array.FindIndex(args, argument => string.Equals(argument, "--analyze-source", StringComparison.OrdinalIgnoreCase));
if (sourceArgument >= 0)
{
    if (args.Length <= sourceArgument + 3)
    {
        Console.Error.WriteLine("Usage: HighlightForge.Worker --analyze-source <project-directory> <media-path> <Fast|Balanced|Deep> [limit-seconds] [start-seconds]");
        Environment.ExitCode = 2;
        return;
    }
    var projectDirectory = args[sourceArgument + 1];
    var mediaPath = args[sourceArgument + 2];
    if (!Enum.TryParse<AnalysisMode>(args[sourceArgument + 3], ignoreCase: true, out var mode))
    {
        Console.Error.WriteLine("Analysis mode must be Fast, Balanced, or Deep.");
        Environment.ExitCode = 2;
        return;
    }
    TimeSpan? limit = args.Length > sourceArgument + 4 && double.TryParse(args[sourceArgument + 4], System.Globalization.CultureInfo.InvariantCulture, out var limitSeconds)
        ? TimeSpan.FromSeconds(limitSeconds)
        : null;
    TimeSpan? start = args.Length > sourceArgument + 5 && double.TryParse(args[sourceArgument + 5], System.Globalization.CultureInfo.InvariantCulture, out var startSeconds)
        ? TimeSpan.FromSeconds(startSeconds)
        : null;
    var imported = await SourceImportService.ImportAsync(mediaPath);
    var directPaths = new ProjectPaths(projectDirectory);
    var directState = await new CreatorWorkflowStore(directPaths).LoadAsync(imported.Source.Id);
    var result = await LocalFeatureAnalyzer.AnalyzeAsync(
        directPaths,
        imported.Source,
        mode,
        analysisStart: start,
        analysisLimit: limit,
        transcript: directState.Captions);
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result));
    return;
}

var inputPath = args.SkipWhile(argument => !string.Equals(argument, "--analyze", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
if (!string.IsNullOrWhiteSpace(inputPath))
{
    var input = JsonSerializer.Deserialize<AnalysisInput>(await File.ReadAllTextAsync(inputPath)) ?? throw new InvalidOperationException("Analysis input is invalid.");
    var candidates = HighlightScorer.CreateCandidates(input);
    var draft = HighlightScorer.BuildDraft(candidates, TimeSpan.FromMinutes(12));
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(draft));
    return;
}

var pipeName = args.SkipWhile(argument => !string.Equals(argument, "--pipe", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
if (string.IsNullOrWhiteSpace(pipeName))
{
    Console.Error.WriteLine("Usage: HighlightForge.Worker --health | --cache-source <project-directory> <media-path> | --analyze-source <project-directory> <media-path> <mode> [limit-seconds] [start-seconds] | --transcribe-source <project-directory> <media-path> <mode> | --measure-source <project-directory> <media-path> | --render-source <project-directory> <media-path> <output.mp4> <LongForm|Vertical> [limit-seconds] [start-seconds] [adjusted] | --benchmark <creator-benchmark.json> | --pipe <name>");
    Environment.ExitCode = 2;
    return;
}

await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
await pipe.WaitForConnectionAsync();
using var reader = new StreamReader(pipe, leaveOpen: true);
await using var writer = new StreamWriter(pipe) { AutoFlush = true };
var requestJson = await reader.ReadLineAsync();
if (string.Equals(requestJson, "health", StringComparison.OrdinalIgnoreCase))
{
    await writer.WriteLineAsync("ready");
    return;
}

var workerRequest = JsonSerializer.Deserialize<AnalysisWorkerRequest>(requestJson ?? string.Empty, WorkerProtocol.JsonOptions);
if (workerRequest is null)
{
    await writer.WriteLineAsync(JsonSerializer.Serialize(
        new AnalysisWorkerMessage("failed", Guid.Empty, 0, "The worker request was invalid.", Error: "Invalid request JSON."),
        WorkerProtocol.JsonOptions));
    Environment.ExitCode = 2;
    return;
}

using var analysisCancellation = new CancellationTokenSource();
using var controlCancellation = new CancellationTokenSource();
var sendGate = new object();
void Send(AnalysisWorkerMessage message)
{
    lock (sendGate)
    {
        writer.WriteLine(JsonSerializer.Serialize(message, WorkerProtocol.JsonOptions));
        writer.Flush();
    }
}

var controlTask = Task.Run(async () =>
{
    try
    {
        while (!controlCancellation.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(controlCancellation.Token);
            if (line is null) break;
            var command = JsonSerializer.Deserialize<AnalysisWorkerCommand>(line, WorkerProtocol.JsonOptions)?.Command;
            if (string.Equals(command, "pause", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "cancel", StringComparison.OrdinalIgnoreCase))
            {
                analysisCancellation.Cancel();
                break;
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
});

try
{
    IReadOnlyList<string> encoders;
    try
    {
        encoders = await FfmpegEncoderCapabilities.AvailableH264EncodersAsync(analysisCancellation.Token);
    }
    catch (Exception)
    {
        encoders = [];
    }
    var capabilities = new AnalysisWorkerCapabilities(
        Environment.ProcessorCount,
        encoders.Contains("h264_nvenc", StringComparer.Ordinal),
        encoders,
        Environment.OSVersion.VersionString);
    Send(new AnalysisWorkerMessage("capabilities", workerRequest.JobId, 0, "Local worker is ready.", Capabilities: capabilities));

    var paths = new ProjectPaths(workerRequest.ProjectDirectory);
    var creatorStore = new CreatorWorkflowStore(paths);
    var creatorState = await creatorStore.LoadAsync(workerRequest.Source.Id, analysisCancellation.Token);
    if (creatorState.Captions.Count == 0)
    {
        var whisperPack = WhisperModelCatalog.ForMode(workerRequest.Mode);
        using var modelClient = new HttpClient();
        var whisperPath = await new WhisperModelInstaller(modelClient).GetActiveModelPathAsync(whisperPack, analysisCancellation.Token);
        if (whisperPath is not null)
        {
            Send(new AnalysisWorkerMessage("progress", workerRequest.JobId, 0.01, "Creating local transcript context before feature analysis.", "transcript", AnalysisJobStatus.Running));
            var transcriptionProgress = new SynchronousProgress<TranscriptionProgress>(update => Send(new AnalysisWorkerMessage(
                "progress",
                workerRequest.JobId,
                Math.Min(0.09, update.Fraction * 0.09),
                update.Detail,
                "transcript",
                AnalysisJobStatus.Running)));
            var captions = await WhisperTranscriptionService.TranscribeAsync(
                paths,
                workerRequest.Source,
                whisperPath,
                transcriptionProgress,
                cancellationToken: analysisCancellation.Token);
            creatorState = creatorState with { Captions = captions, ModifiedUtc = DateTimeOffset.UtcNow };
            await creatorStore.SaveAsync(creatorState, analysisCancellation.Token);
        }
    }
    var checkpointStore = new AnalysisJobStore(paths);
    var checkpoint = workerRequest.Resume
        ? await checkpointStore.LoadLatestForSourceAsync(workerRequest.Source.Id, workerRequest.Mode, analysisCancellation.Token)
        : null;
    if (checkpoint?.Status == AnalysisJobStatus.Completed) checkpoint = null;
    var activeJobId = checkpoint?.JobId ?? workerRequest.JobId;
    var progress = new SynchronousProgress<AnalysisProgress>(update => Send(new AnalysisWorkerMessage(
        "progress",
        activeJobId,
        update.Progress,
        update.Detail,
        update.Stage,
        AnalysisJobStatus.Running)));
    Send(new AnalysisWorkerMessage(
        checkpoint is null ? "started" : "resumed",
        activeJobId,
        checkpoint?.Progress ?? 0,
        checkpoint is null ? "Started a new local analysis job." : $"Resuming after {checkpoint.Stage}.",
        checkpoint?.Stage,
        AnalysisJobStatus.Running,
        capabilities));
    var result = await LocalFeatureAnalyzer.AnalyzeAsync(
        paths,
        workerRequest.Source,
        workerRequest.Mode,
        progress,
        jobId: activeJobId,
        resumeFrom: checkpoint,
        transcript: creatorState.Captions,
        cancellationToken: analysisCancellation.Token);
    Send(new AnalysisWorkerMessage("completed", activeJobId, 1, "Local analysis completed.", "complete", AnalysisJobStatus.Completed, capabilities, result));
}
catch (OperationCanceledException)
{
    var checkpoint = await new AnalysisJobStore(new ProjectPaths(workerRequest.ProjectDirectory))
        .LoadLatestForSourceAsync(workerRequest.Source.Id, workerRequest.Mode, CancellationToken.None);
    Send(new AnalysisWorkerMessage(
        "paused",
        checkpoint?.JobId ?? workerRequest.JobId,
        checkpoint?.Progress ?? 0,
        checkpoint?.Detail ?? "Analysis paused safely.",
        checkpoint?.Stage,
        AnalysisJobStatus.Paused));
}
catch (Exception exception)
{
    Send(new AnalysisWorkerMessage(
        "failed",
        workerRequest.JobId,
        0,
        "Local analysis failed. The saved checkpoint can be retried.",
        Status: AnalysisJobStatus.Failed,
        Error: exception.Message));
    Environment.ExitCode = 1;
}
finally
{
    controlCancellation.Cancel();
    try
    {
        await controlTask;
    }
    catch (OperationCanceledException)
    {
    }
}

file static class WorkerProtocol
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
}

file sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
