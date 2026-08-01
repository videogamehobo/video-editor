using System.IO.Pipes;
using System.Text.Json;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Audio;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Models;
using HighlightForge.Core.Persistence;
using HighlightForge.Media.Analysis;
using HighlightForge.Media.Audio;
using HighlightForge.Media.Captions;
using HighlightForge.Media.Import;
using HighlightForge.Media.Models;

if (args.Contains("--health", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(JsonSerializer.Serialize(new { service = "HighlightForge.Worker", status = "ready", localOnly = true }));
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
    var modelPath = await installer.InstallAsync(pack, downloadProgress);
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
    var result = await LocalFeatureAnalyzer.AnalyzeAsync(new ProjectPaths(projectDirectory), imported.Source, mode, analysisStart: start, analysisLimit: limit);
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
    Console.Error.WriteLine("Usage: HighlightForge.Worker --health | --analyze-source <project-directory> <media-path> <mode> [limit-seconds] [start-seconds] | --transcribe-source <project-directory> <media-path> <mode> | --measure-source <project-directory> <media-path> | --pipe <name>");
    Environment.ExitCode = 2;
    return;
}

await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
await pipe.WaitForConnectionAsync();
using var reader = new StreamReader(pipe, leaveOpen: true);
await using var writer = new StreamWriter(pipe) { AutoFlush = true };
var request = await reader.ReadLineAsync();
if (string.Equals(request, "health", StringComparison.OrdinalIgnoreCase))
{
    await writer.WriteLineAsync("ready");
}
