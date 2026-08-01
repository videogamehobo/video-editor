using System.Diagnostics;
using HighlightForge.Core.Persistence;

namespace HighlightForge.Media.Proxy;

public sealed record ProxyRequest(Guid SourceId, string SourcePath, string OutputPath, int Height = 540);

public sealed class ProxyGenerationService
{
    public static ProxyRequest CreateRequest(ProjectPaths paths, Guid sourceId, string sourcePath, int height = 540)
    {
        var proxyDirectory = Path.Combine(paths.CacheDirectory, "proxies");
        Directory.CreateDirectory(proxyDirectory);
        return new ProxyRequest(sourceId, Path.GetFullPath(sourcePath), Path.Combine(proxyDirectory, $"{sourceId:N}.mp4"), height);
    }

    public static async Task GenerateAsync(ProxyRequest request, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(),
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in BuildArguments(request)) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start FFmpeg.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg could not generate a proxy: {(await errorTask).Trim()}");
    }

    public static IReadOnlyList<string> BuildArguments(ProxyRequest request) =>
    [
        "-y", "-i", request.SourcePath,
        "-map", "0:v:0", "-vf", $"scale=-2:{request.Height}",
        "-c:v", "mpeg4", "-q:v", "6", "-an", "-movflags", "+faststart", request.OutputPath
    ];

    private static string ResolveFfmpegPath() =>
        Environment.GetEnvironmentVariable("HIGHLIGHTFORGE_FFMPEG_PATH") is { Length: > 0 } explicitPath ? explicitPath : "ffmpeg";
}
