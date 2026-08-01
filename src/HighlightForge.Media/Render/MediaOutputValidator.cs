using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HighlightForge.Core.Audio;
using HighlightForge.Media.Runtime;

namespace HighlightForge.Media.Render;

public sealed record OutputStreamInfo(string CodecType, string CodecName, int? Width, int? Height, double? StartSeconds, double? DurationSeconds);
public sealed record OutputValidationReport(bool IsValid, IReadOnlyList<string> Problems, IReadOnlyList<OutputStreamInfo> Streams, double DurationSeconds);

public static class MediaOutputValidator
{
    public static async Task<OutputValidationReport> ValidateAsync(
        string outputPath,
        RenderKind kind,
        TimeSpan expectedDuration,
        AudioLoudnessMeasurement loudness,
        AudioMixSettings settings,
        int? expectedWidth = null,
        int? expectedHeight = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegRuntime.ResolveFfprobePath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-show_entries", "format=duration:stream=index,codec_name,codec_type,width,height,duration,start_time", "-of", "json", outputPath
        }) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start FFprobe output verification.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"FFprobe output verification failed: {error.Trim()}");
        return Parse(output, kind, expectedDuration, loudness, settings, expectedWidth, expectedHeight);
    }

    public static OutputValidationReport Parse(
        string ffprobeJson,
        RenderKind kind,
        TimeSpan expectedDuration,
        AudioLoudnessMeasurement loudness,
        AudioMixSettings settings,
        int? expectedWidth = null,
        int? expectedHeight = null)
    {
        using var document = JsonDocument.Parse(ffprobeJson);
        var root = document.RootElement;
        var duration = ReadDouble(root.GetProperty("format"), "duration") ?? 0;
        var streams = root.GetProperty("streams").EnumerateArray().Select(stream => new OutputStreamInfo(
            stream.GetProperty("codec_type").GetString() ?? string.Empty,
            stream.GetProperty("codec_name").GetString() ?? string.Empty,
            ReadInt(stream, "width"),
            ReadInt(stream, "height"),
            ReadDouble(stream, "start_time"),
            ReadDouble(stream, "duration"))).ToArray();
        var problems = new List<string>();
        var videoStreams = streams.Where(stream => stream.CodecType == "video").ToArray();
        var audioStreams = streams.Where(stream => stream.CodecType == "audio").ToArray();
        var video = videoStreams.FirstOrDefault();
        var audio = audioStreams.FirstOrDefault();
        if (videoStreams.Length > 1) problems.Add($"Expected one video stream, found {videoStreams.Length}.");
        if (audioStreams.Length > 1) problems.Add($"Expected one audio stream, found {audioStreams.Length}.");
        if (video is null) problems.Add("Missing video stream.");
        else
        {
            if (!string.Equals(video.CodecName, "h264", StringComparison.OrdinalIgnoreCase)) problems.Add($"Expected H.264 video, found {video.CodecName}.");
            if (kind == RenderKind.Vertical && (video.Width != 1080 || video.Height != 1920)) problems.Add($"Vertical output must be 1080x1920, found {video.Width}x{video.Height}.");
            if (kind == RenderKind.LongForm && expectedWidth is not null && expectedHeight is not null &&
                (video.Width != expectedWidth || video.Height != expectedHeight))
            {
                problems.Add($"Long-form output must preserve {expectedWidth}x{expectedHeight}, found {video.Width}x{video.Height}.");
            }
        }
        if (audio is null) problems.Add("Missing audio stream.");
        else if (!string.Equals(audio.CodecName, "aac", StringComparison.OrdinalIgnoreCase)) problems.Add($"Expected AAC audio, found {audio.CodecName}.");
        if (Math.Abs(duration - expectedDuration.TotalSeconds) > 0.25) problems.Add($"Output duration differs from the edited timeline by {Math.Abs(duration - expectedDuration.TotalSeconds):0.###} seconds.");
        if (video?.DurationSeconds is not null && audio?.DurationSeconds is not null &&
            Math.Abs(video.DurationSeconds.Value - audio.DurationSeconds.Value) > 0.1)
        {
            problems.Add("Audio/video stream durations differ by more than 100 ms.");
        }
        if (video?.StartSeconds is not null && audio?.StartSeconds is not null &&
            Math.Abs(video.StartSeconds.Value - audio.StartSeconds.Value) > 0.1)
        {
            problems.Add("Audio/video stream start times differ by more than 100 ms.");
        }
        if (loudness.IntegratedLufs > -70 && Math.Abs(loudness.IntegratedLufs - settings.TargetIntegratedLufs) > 1)
        {
            problems.Add($"Integrated loudness is {loudness.IntegratedLufs:0.0} LUFS.");
        }
        if (loudness.IntegratedLufs > -70 && loudness.TruePeakDbtp > settings.TruePeakDbtp)
        {
            problems.Add($"True peak is {loudness.TruePeakDbtp:0.0} dBTP.");
        }
        return new OutputValidationReport(problems.Count == 0, problems, streams, duration);
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)) return null;
        var text = property.ValueKind == JsonValueKind.String ? property.GetString() : property.GetRawText();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static int? ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value) ? value : null;
}
