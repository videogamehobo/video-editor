using System.IO.Pipes;
using System.Text.Json;
using HighlightForge.Core.Analysis;

if (args.Contains("--health", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(JsonSerializer.Serialize(new { service = "HighlightForge.Worker", status = "ready", localOnly = true }));
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
    Console.Error.WriteLine("Usage: HighlightForge.Worker --health | --pipe <name>");
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
