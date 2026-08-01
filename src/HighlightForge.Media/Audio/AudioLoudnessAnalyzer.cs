using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HighlightForge.Core.Audio;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Domain;
using HighlightForge.Media.Runtime;

namespace HighlightForge.Media.Audio;

public static partial class AudioLoudnessAnalyzer
{
    public static async Task<AudioLoudnessMeasurement> MeasureAsync(
        string sourcePath,
        AudioTrack track,
        TimeSpan? sourceStart = null,
        TimeSpan? sourceLimit = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegRuntime.ResolveFfmpegPath(),
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in BuildArguments(sourcePath, track.StreamIndex, sourceStart, sourceLimit)) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(FfmpegRuntime.MissingRuntimeMessage, exception);
        }

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg loudness measurement failed: {output.Trim()}");
        var measurement = Parse(track, output);
        await HighlightForgeLog.InfoAsync($"Measured stream {track.StreamIndex}: {measurement.IntegratedLufs:0.0} LUFS, {measurement.TruePeakDbtp:0.0} dBTP.", cancellationToken);
        return measurement;
    }

    public static IReadOnlyList<string> BuildArguments(string sourcePath, int streamIndex, TimeSpan? sourceStart = null, TimeSpan? sourceLimit = null)
    {
        var arguments = new List<string> { "-hide_banner", "-nostats" };
        if (sourceStart is not null) arguments.AddRange(["-ss", sourceStart.Value.TotalSeconds.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-i", sourcePath]);
        if (sourceLimit is not null) arguments.AddRange(["-t", sourceLimit.Value.TotalSeconds.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-map", $"0:{streamIndex}", "-af", "loudnorm=I=-14:LRA=11:TP=-1:print_format=json", "-f", "null", "NUL"]);
        return arguments;
    }

    public static AudioLoudnessMeasurement Parse(AudioTrack track, string ffmpegOutput)
    {
        var matches = LoudnessJson().Matches(ffmpegOutput);
        if (matches.Count == 0) throw new InvalidDataException("FFmpeg did not return a loudness measurement.");
        using var document = JsonDocument.Parse(matches[^1].Value);
        var root = document.RootElement;
        return new AudioLoudnessMeasurement(
            track.StreamIndex,
            track.DisplayName,
            ReadNumber(root, "input_i"),
            ReadNumber(root, "input_tp"),
            ReadNumber(root, "input_lra"),
            ReadNumber(root, "input_thresh"),
            ReadNumber(root, "target_offset"));
    }

    private static double ReadNumber(JsonElement root, string name)
    {
        var text = root.GetProperty(name).GetString();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return value;
        return text is { Length: > 0 } && text[0] == '-' ? -99 : 99;
    }

    [GeneratedRegex("\\{\\s*\"input_i\"[\\s\\S]*?\"target_offset\"[\\s\\S]*?\\}", RegexOptions.CultureInvariant)]
    private static partial Regex LoudnessJson();
}
