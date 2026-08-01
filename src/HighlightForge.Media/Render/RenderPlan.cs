using System.Diagnostics;
using HighlightForge.Core.Audio;
using HighlightForge.Media.Runtime;

namespace HighlightForge.Media.Render;

public enum RenderKind
{
    LongForm,
    Vertical
}

public sealed record RenderRequest(
    RenderKind Kind,
    string SourcePath,
    string OutputPath,
    AudioMixSettings AudioSettings,
    int FramesPerSecond = 60);

public static class RenderPlan
{
    public static IReadOnlyList<string> BuildArguments(RenderRequest request)
    {
        MediaPathSafety.RequireSeparateOutput(request.SourcePath, request.OutputPath, "Export");
        var videoFilter = request.Kind == RenderKind.Vertical ? VerticalVideoFilter() : "null";
        return
        [
            "-y", "-i", request.SourcePath,
            "-vf", videoFilter,
            "-af", AudioMixPlanner.BuildFinalLoudnessFilter(request.AudioSettings),
            "-r", request.FramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:v", "h264_mf", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", request.OutputPath
        ];
    }

    public static string VerticalVideoFilter() =>
        "split[background][foreground];[background]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,boxblur=20:1[blurred];[foreground]scale=1080:1920:force_original_aspect_ratio=decrease[gameplay];[blurred][gameplay]overlay=(W-w)/2:(H-h)/2";
}

public sealed class RenderService
{
    public static async Task RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
    {
        MediaPathSafety.RequireSeparateOutput(request.SourcePath, request.OutputPath, "Export");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegRuntime.ResolveFfmpegPath(),
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in RenderPlan.BuildArguments(request)) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start FFmpeg.");
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Render failed: {(await errors).Trim()}");
    }
}
