using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Media.Runtime;

namespace HighlightForge.Media.Probe;

public sealed class FfprobeService
{
    public static async Task<MediaProbeResult> ProbeAsync(string mediaPath, CancellationToken cancellationToken = default)
    {
        var absolutePath = Path.GetFullPath(mediaPath);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("The selected media file does not exist.", absolutePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFfprobePath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add(absolutePath);
        await HighlightForgeLog.InfoAsync($"Starting FFprobe for '{absolutePath}' using '{startInfo.FileName}'.", cancellationToken);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start FFprobe.");
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            await HighlightForgeLog.ErrorAsync("FFprobe executable could not be started.", exception, cancellationToken);
            throw new InvalidOperationException(FfmpegRuntime.MissingRuntimeMessage, exception);
        }
        using (process)
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                await HighlightForgeLog.InfoAsync($"FFprobe failed with exit code {process.ExitCode}: {error.Trim()}", cancellationToken);
                throw new InvalidOperationException($"FFprobe could not inspect this recording: {error.Trim()}");
            }
            await HighlightForgeLog.InfoAsync($"FFprobe completed for '{absolutePath}'.", cancellationToken);
            return Parse(absolutePath, output);
        }
    }

    public static MediaProbeResult Parse(string absolutePath, string ffprobeJson)
    {
        using var document = JsonDocument.Parse(ffprobeJson);
        var root = document.RootElement;
        var format = root.GetProperty("format");
        var duration = ParseDuration(format.TryGetProperty("duration", out var durationValue) ? durationValue.GetString() : null);
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(stream => stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video");
        var width = video.ValueKind == JsonValueKind.Undefined ? 0 : video.GetProperty("width").GetInt32();
        var height = video.ValueKind == JsonValueKind.Undefined ? 0 : video.GetProperty("height").GetInt32();
        var fps = video.ValueKind == JsonValueKind.Undefined ? 0 : ParseFrameRate(video.TryGetProperty("avg_frame_rate", out var rate) ? rate.GetString() : null);

        var audioTracks = streams
            .Where(stream => stream.TryGetProperty("codec_type", out var type) && type.GetString() == "audio")
            .Select(stream => new AudioTrack(
                stream.GetProperty("index").GetInt32(),
                GetTrackName(stream),
                stream.TryGetProperty("channels", out var channels) ? channels.GetInt32() : 0,
                stream.TryGetProperty("sample_rate", out var sampleRate) && int.TryParse(sampleRate.GetString(), CultureInfo.InvariantCulture, out var parsedSampleRate) ? parsedSampleRate : 0))
            .ToArray();

        return new MediaProbeResult(Path.GetFullPath(absolutePath), duration, width, height, fps, audioTracks);
    }

    private static string ResolveFfprobePath() => FfmpegRuntime.ResolveFfprobePath();

    private static TimeSpan ParseDuration(string? duration) =>
        double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;

    private static double ParseFrameRate(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate)) return 0;
        var parts = rate.Split('/');
        if (parts.Length != 2 || !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator) || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) || denominator == 0) return 0;
        return numerator / denominator;
    }

    private static string GetTrackName(JsonElement stream)
    {
        if (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } name)
        {
            return name;
        }
        return $"Audio track {stream.GetProperty("index").GetInt32()}";
    }
}
