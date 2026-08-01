using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Persistence;
using HighlightForge.Media.Runtime;

namespace HighlightForge.Media.Proxy;

public sealed record MediaCacheBundle(
    Guid SourceId,
    string Fingerprint,
    string ProxyPath,
    string? WaveformPath,
    IReadOnlyList<string> ThumbnailPaths,
    DateTimeOffset CompletedUtc,
    ProxyTimeMap? TimeMap = null);

public sealed record ProxyTimeMap(TimeSpan SourceStart, TimeSpan ProxyStart, TimeSpan Duration)
{
    public TimeSpan SourceToProxy(TimeSpan sourceTime) =>
        ProxyStart + Clamp(sourceTime - SourceStart, TimeSpan.Zero, Duration);

    public TimeSpan ProxyToSource(TimeSpan proxyTime) =>
        SourceStart + Clamp(proxyTime - ProxyStart, TimeSpan.Zero, Duration);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;
}

public sealed record MediaCacheProgress(double Fraction, string Stage, string Detail);

public static class MediaCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<MediaCacheBundle> GenerateAsync(
        ProjectPaths paths,
        MediaSource source,
        IProgress<MediaCacheProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(source.AbsolutePath)) throw new FileNotFoundException("The source recording is no longer available.", source.AbsolutePath);
        paths.EnsureDirectories();
        var fingerprint = CreateFingerprint(source.AbsolutePath);
        var sourceCacheRoot = Path.Combine(paths.CacheDirectory, "media", source.Id.ToString("N"));
        var outputDirectory = Path.Combine(sourceCacheRoot, fingerprint);
        MediaPathSafety.RequireOutputWithinDirectory(paths.CacheDirectory, outputDirectory, "Media cache");
        var manifestPath = Path.Combine(outputDirectory, "cache.json");
        var existing = await TryLoadCompletedAsync(manifestPath, source, cancellationToken);
        if (existing is not null)
        {
            progress?.Report(new MediaCacheProgress(1, "Complete", "Using the existing source-matched preview cache."));
            return existing;
        }

        var workingDirectory = Path.Combine(sourceCacheRoot, $".{fingerprint}-{Guid.NewGuid():N}.partial");
        MediaPathSafety.RequireOutputWithinDirectory(paths.CacheDirectory, workingDirectory, "Media cache work directory");
        Directory.CreateDirectory(workingDirectory);
        var proxyPath = Path.Combine(workingDirectory, "proxy.mp4");
        var thumbnailsDirectory = Path.Combine(workingDirectory, "thumbnails");
        Directory.CreateDirectory(thumbnailsDirectory);
        var waveformPath = source.AudioTracks.Count == 0 ? null : Path.Combine(workingDirectory, "waveform.png");
        try
        {
            progress?.Report(new MediaCacheProgress(0.02, "Proxy", "Creating a low-resolution seekable preview."));
            await RunFfmpegAsync(BuildProxyArguments(source, proxyPath), cancellationToken);
            progress?.Report(new MediaCacheProgress(0.72, "Thumbnails", "Sampling timeline thumbnails."));
            await RunFfmpegAsync(BuildThumbnailArguments(source, Path.Combine(thumbnailsDirectory, "%06d.jpg")), cancellationToken);
            if (waveformPath is not null)
            {
                progress?.Report(new MediaCacheProgress(0.88, "Waveform", "Drawing the selected game/mixed audio waveform."));
                await RunFfmpegAsync(BuildWaveformArguments(source, waveformPath), cancellationToken);
            }

            var thumbnails = Directory.EnumerateFiles(thumbnailsDirectory, "*.jpg").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            var bundle = new MediaCacheBundle(
                source.Id,
                fingerprint,
                Path.Combine(outputDirectory, "proxy.mp4"),
                waveformPath is null ? null : Path.Combine(outputDirectory, "waveform.png"),
                thumbnails.Select(path => Path.Combine(outputDirectory, "thumbnails", Path.GetFileName(path))).ToArray(),
                DateTimeOffset.UtcNow,
                new ProxyTimeMap(TimeSpan.Zero, TimeSpan.Zero, source.Duration));
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "cache.json"), JsonSerializer.Serialize(bundle, JsonOptions), cancellationToken);
            Directory.CreateDirectory(sourceCacheRoot);
            if (Directory.Exists(outputDirectory)) throw new IOException("A matching media cache was completed by another operation.");
            Directory.Move(workingDirectory, outputDirectory);
            progress?.Report(new MediaCacheProgress(1, "Complete", $"Created proxy, {thumbnails.Length} thumbnails, and waveform cache."));
            return bundle;
        }
        finally
        {
            if (Directory.Exists(workingDirectory)) Directory.Delete(workingDirectory, recursive: true);
        }
    }

    public static async Task<MediaCacheBundle?> TryLoadAsync(ProjectPaths paths, MediaSource source, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(source.AbsolutePath)) return null;
        var fingerprint = CreateFingerprint(source.AbsolutePath);
        var manifestPath = Path.Combine(paths.CacheDirectory, "media", source.Id.ToString("N"), fingerprint, "cache.json");
        return await TryLoadCompletedAsync(manifestPath, source, cancellationToken);
    }

    public static IReadOnlyList<string> BuildProxyArguments(MediaSource source, string outputPath)
    {
        ValidateOutput(source, outputPath);
        var audio = SelectPreviewAudio(source);
        var arguments = new List<string>
        {
            "-hide_banner", "-y", "-i", source.AbsolutePath,
            "-map", "0:v:0", "-vf", "scale=-2:540",
            "-c:v", "mpeg4", "-q:v", "6"
        };
        if (audio is null) arguments.Add("-an");
        else arguments.AddRange(["-map", $"0:{audio.StreamIndex}", "-c:a", "aac", "-b:a", "128k", "-ar", "48000"]);
        arguments.AddRange(["-movflags", "+faststart", outputPath]);
        return arguments;
    }

    public static IReadOnlyList<string> BuildThumbnailArguments(MediaSource source, string outputPattern)
    {
        ValidateOutput(source, outputPattern);
        return [
            "-hide_banner", "-y", "-i", source.AbsolutePath,
            "-map", "0:v:0",
            "-vf", "select='isnan(prev_selected_t)+gte(t-prev_selected_t,30)',scale=320:-2:out_range=pc,format=yuvj420p",
            "-fps_mode", "vfr", "-c:v", "mjpeg", "-q:v", "4", outputPattern
        ];
    }

    public static IReadOnlyList<string> BuildWaveformArguments(MediaSource source, string outputPath)
    {
        ValidateOutput(source, outputPath);
        var audio = SelectPreviewAudio(source) ?? throw new InvalidOperationException("This recording has no audio track for a waveform.");
        return [
            "-hide_banner", "-y", "-i", source.AbsolutePath,
            "-filter_complex", $"[0:{audio.StreamIndex}]aformat=channel_layouts=stereo,showwavespic=s=1600x220:colors=0x5DADE2[wave]",
            "-map", "[wave]", "-an", "-frames:v", "1", "-c:v", "png", outputPath
        ];
    }

    private static AudioTrack? SelectPreviewAudio(MediaSource source)
    {
        foreach (var role in new[] { AudioTrackRole.Mixed, AudioTrackRole.Game })
        {
            for (var index = 0; index < source.AudioTracks.Count; index++)
            {
                if (source.AudioTracks[index].Role == role) return source.AudioTracks[index];
            }
        }
        return source.AudioTracks.Count == 0 ? null : source.AudioTracks[0];
    }

    private static async Task<MediaCacheBundle?> TryLoadCompletedAsync(
        string manifestPath,
        MediaSource source,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath)) return null;
        var bundle = JsonSerializer.Deserialize<MediaCacheBundle>(await File.ReadAllTextAsync(manifestPath, cancellationToken), JsonOptions);
        if (bundle is null || bundle.SourceId != source.Id || !File.Exists(bundle.ProxyPath)) return null;
        if (bundle.WaveformPath is not null && !File.Exists(bundle.WaveformPath)) return null;
        if (bundle.ThumbnailPaths.Any(path => !File.Exists(path))) return null;
        return bundle;
    }

    private static string CreateFingerprint(string sourcePath)
    {
        var file = new FileInfo(sourcePath);
        var input = Encoding.UTF8.GetBytes($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        return Convert.ToHexString(SHA256.HashData(input))[..16].ToLowerInvariant();
    }

    private static void ValidateOutput(MediaSource source, string outputPath) =>
        MediaPathSafety.RequireSeparateOutput(source.AbsolutePath, outputPath, "Media cache");

    private static async Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegRuntime.ResolveFfmpegPath(),
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start for media-cache generation.");
        try
        {
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg media-cache generation failed: {error.Trim()}");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
