using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Persistence;
using HighlightForge.Media.Runtime;

namespace HighlightForge.Media.Analysis;

public static class AnalysisWorkerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<LocalAnalysisResult> AnalyzeAsync(
        ProjectPaths paths,
        MediaSource source,
        AnalysisMode mode,
        IProgress<AnalysisWorkerMessage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var latest = await new AnalysisJobStore(paths).LoadLatestForSourceAsync(source.Id, mode, cancellationToken);
        var jobId = latest is { Status: AnalysisJobStatus.Paused or AnalysisJobStatus.Failed or AnalysisJobStatus.Running }
            ? latest.JobId
            : Guid.NewGuid();
        var pipeName = $"HighlightForge.Analysis.{jobId:N}.{Guid.NewGuid():N}";
        var startInfo = CreateStartInfo(pipeName);
        startInfo.Environment["HIGHLIGHTFORGE_FFMPEG_PATH"] = FfmpegRuntime.ResolveFfmpegPath();
        startInfo.Environment["HIGHLIGHTFORGE_FFPROBE_PATH"] = FfmpegRuntime.ResolveFfprobePath();
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("The local analysis worker did not start.");
        var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var combinedConnect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectTimeout.Token);
            try
            {
                await pipe.ConnectAsync(combinedConnect.Token);
            }
            catch (OperationCanceledException) when (connectTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The local analysis worker did not open its control pipe within 20 seconds.");
            }

            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var request = new AnalysisWorkerRequest(jobId, paths.ProjectDirectory, source, mode, Resume: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
            var writerGate = new object();
            var pauseSent = 0;
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                if (Interlocked.Exchange(ref pauseSent, 1) != 0) return;
                try
                {
                    lock (writerGate)
                    {
                        writer.WriteLine(JsonSerializer.Serialize(new AnalysisWorkerCommand("pause"), JsonOptions));
                        writer.Flush();
                    }
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                }
            });

            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                var message = JsonSerializer.Deserialize<AnalysisWorkerMessage>(line, JsonOptions)
                    ?? throw new InvalidDataException("The local analysis worker returned an invalid response.");
                progress?.Report(message);
                switch (message.Kind)
                {
                    case "completed" when message.Result is not null:
                        await process.WaitForExitAsync(CancellationToken.None);
                        return message.Result;
                    case "paused":
                        throw new OperationCanceledException(message.Detail, cancellationToken);
                    case "failed":
                        throw new InvalidOperationException(message.Error ?? message.Detail);
                }
            }

            await process.WaitForExitAsync(CancellationToken.None);
            var error = await errorTask;
            throw new InvalidOperationException($"The local analysis worker exited before completing the job. {error.Trim()}".Trim());
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            var error = await errorTask;
            if (!string.IsNullOrWhiteSpace(error)) await HighlightForgeLog.InfoAsync($"Analysis worker diagnostic: {error.Trim()}", CancellationToken.None);
            throw;
        }
        finally
        {
            _ = await outputTask;
        }
    }

    public static ProcessStartInfo CreateStartInfo(string pipeName, string? baseDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var root = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var executableCandidates = new[]
        {
            Path.Combine(root, "worker", "HighlightForge.Worker.exe"),
            Path.Combine(root, "HighlightForge.Worker.exe"),
            Path.GetFullPath(Path.Combine(root, "..", "..", "..", "..", "HighlightForge.Worker", "bin", "Debug", "net10.0", "HighlightForge.Worker.exe")),
            Path.GetFullPath(Path.Combine(root, "..", "..", "..", "..", "HighlightForge.Worker", "bin", "Release", "net10.0", "HighlightForge.Worker.exe"))
        };
        var executable = executableCandidates.FirstOrDefault(File.Exists);
        ProcessStartInfo startInfo;
        if (executable is not null)
        {
            startInfo = BaseStartInfo(executable);
        }
        else
        {
            var dllCandidates = executableCandidates.Select(path => Path.ChangeExtension(path, ".dll")).ToArray();
            var dll = dllCandidates.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("The separate HighlightForge analysis worker is missing. Repair or reinstall HighlightForge.");
            startInfo = BaseStartInfo("dotnet");
            startInfo.ArgumentList.Add(dll);
        }
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        return startInfo;
    }

    private static ProcessStartInfo BaseStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    };
}
