using System.IO.Pipes;
using System.Text.Json;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Persistence;
using HighlightForge.Media.Analysis;
using HighlightForge.Media.Import;

if (args.Contains("--health", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(JsonSerializer.Serialize(new { service = "HighlightForge.Worker", status = "ready", localOnly = true }));
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
    Console.Error.WriteLine("Usage: HighlightForge.Worker --health | --analyze-source <project-directory> <media-path> <mode> [limit-seconds] [start-seconds] | --pipe <name>");
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
