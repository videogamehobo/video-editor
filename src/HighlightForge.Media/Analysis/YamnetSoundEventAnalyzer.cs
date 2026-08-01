using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Persistence;
using HighlightForge.Media.Runtime;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HighlightForge.Media.Analysis;

public static class YamnetSoundEventAnalyzer
{
    public const int SampleRate = 16_000;
    public const int PatchFrames = 96;
    public const int MelBands = 64;
    public const int RequiredSamples = 15_600;
    private const int WindowSamples = 400;
    private const int HopSamples = 160;
    private const int FftSize = 512;
    private static readonly double[] HannWindow = CreateHannWindow();
    private static readonly double[][] MelWeights = CreateMelWeights();

    public static async Task<IReadOnlyList<FeatureEvent>> AnalyzeAsync(
        ProjectPaths paths,
        MediaSource source,
        AudioTrack track,
        IReadOnlyList<FeatureEvent> seedWindows,
        string modelDirectory,
        AnalysisMode mode,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var modelPath = Path.Combine(modelDirectory, "yamnet.onnx");
        var classMapPath = Path.Combine(modelDirectory, "yamnet_class_map.csv");
        if (!File.Exists(modelPath) || !File.Exists(Path.Combine(modelDirectory, "yamnet.data")) || !File.Exists(classMapPath)) return [];
        var windows = SelectWindows(seedWindows, mode);
        if (windows.Length == 0) return [];

        var analysisDirectory = Path.Combine(paths.CacheDirectory, "analysis", source.Id.ToString("N"));
        Directory.CreateDirectory(analysisDirectory);
        var pcmPath = Path.Combine(analysisDirectory, $"yamnet-{track.StreamIndex}-{Guid.NewGuid():N}.s16le");
        MediaPathSafety.RequireSeparateOutput(source.AbsolutePath, pcmPath, "YAMNet audio extraction");
        MediaPathSafety.RequireOutputWithinDirectory(paths.CacheDirectory, pcmPath, "YAMNet audio extraction");
        try
        {
            progress?.Report(new AnalysisProgress("sound-events", 0.60, "Extracting disposable mono audio for local YAMNet sound-event recognition."));
            await ExtractPcmAsync(source.AbsolutePath, track.StreamIndex, pcmPath, cancellationToken);
            var labels = ParseClassMap(await File.ReadAllLinesAsync(classMapPath, cancellationToken));
            using var session = new InferenceSession(modelPath);
            await using var pcm = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
            var result = new List<FeatureEvent>();
            for (var index = 0; index < windows.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var window = windows[index];
                var samples = await ReadSamplesAsync(pcm, window.Start, cancellationToken);
                var patch = ComputeLogMelPatch(samples);
                var tensor = new DenseTensor<float>(patch, [1, 1, PatchFrames, MelBands]);
                using var outputs = session.Run([NamedOnnxValue.CreateFromTensor("audio", tensor)]);
                var scores = outputs.Single(output => output.Name == "class_scores").AsEnumerable<float>().ToArray();
                var detected = SelectInterestingEvent(scores, labels, window);
                if (detected is not null) result.Add(detected);
                progress?.Report(new AnalysisProgress(
                    "sound-events",
                    0.60 + (0.08 * (index + 1d) / windows.Length),
                    $"Recognizing local sound events {index + 1}/{windows.Length}."));
            }
            return result;
        }
        finally
        {
            if (File.Exists(pcmPath)) File.Delete(pcmPath);
        }
    }

    public static float[] ComputeLogMelPatch(ReadOnlySpan<float> samples)
    {
        var patch = new float[PatchFrames * MelBands];
        var fft = new Complex[FftSize];
        var power = new double[(FftSize / 2) + 1];
        for (var frame = 0; frame < PatchFrames; frame++)
        {
            Array.Clear(fft);
            var offset = frame * HopSamples;
            for (var sample = 0; sample < WindowSamples; sample++)
            {
                var input = offset + sample < samples.Length ? samples[offset + sample] : 0;
                fft[sample] = new Complex(input * HannWindow[sample], 0);
            }
            ForwardFft(fft);
            for (var bin = 0; bin < power.Length; bin++) power[bin] = fft[bin].Magnitude * fft[bin].Magnitude;
            for (var mel = 0; mel < MelBands; mel++)
            {
                double energy = 0;
                for (var bin = 0; bin < power.Length; bin++) energy += power[bin] * MelWeights[mel][bin];
                patch[(frame * MelBands) + mel] = (float)Math.Log(energy + 0.001);
            }
        }
        return patch;
    }

    public static IReadOnlyDictionary<int, string> ParseClassMap(IEnumerable<string> lines)
    {
        var labels = new Dictionary<int, string>();
        foreach (var line in lines.Skip(1))
        {
            var first = line.IndexOf(',');
            if (first <= 0 || !int.TryParse(line.AsSpan(0, first), CultureInfo.InvariantCulture, out var index)) continue;
            var second = line.IndexOf(',', first + 1);
            if (second < 0) continue;
            var label = line[(second + 1)..].Trim().Trim('"').Replace("\"\"", "\"", StringComparison.Ordinal);
            if (label.Length > 0) labels[index] = label;
        }
        return labels;
    }

    public static FeatureEvent? SelectInterestingEvent(
        IReadOnlyList<float> scores,
        IReadOnlyDictionary<int, string> labels,
        FeatureEvent window,
        double minimumConfidence = 0.15)
    {
        var interesting = scores
            .Select((score, index) => (Score: (double)score, Label: labels.GetValueOrDefault(index, string.Empty)))
            .Where(item => item.Score >= minimumConfidence)
            .Select(item => (item.Score, item.Label, Kind: MapKind(item.Label)))
            .Where(item => item.Kind is not null)
            .OrderByDescending(item => item.Score)
            .FirstOrDefault();
        if (interesting.Kind is null) return null;
        return new FeatureEvent(
            interesting.Kind.Value,
            window.Start,
            window.End,
            Math.Clamp(interesting.Score, 0.35, 0.99),
            $"sound event: {interesting.Label} ({interesting.Score:P0})");
    }

    private static FeatureKind? MapKind(string label)
    {
        if (label.Contains("laughter", StringComparison.OrdinalIgnoreCase) || label.Contains("giggle", StringComparison.OrdinalIgnoreCase)) return FeatureKind.Laughter;
        if (label.Contains("scream", StringComparison.OrdinalIgnoreCase) || label.Contains("cheering", StringComparison.OrdinalIgnoreCase) || label.Contains("shout", StringComparison.OrdinalIgnoreCase)) return FeatureKind.VocalExcitement;
        if (label.Contains("explosion", StringComparison.OrdinalIgnoreCase) || label.Contains("gunshot", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("crash", StringComparison.OrdinalIgnoreCase) || label.Contains("bang", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("glass", StringComparison.OrdinalIgnoreCase) || label.Contains("exciting music", StringComparison.OrdinalIgnoreCase)) return FeatureKind.GameAudioPeak;
        return null;
    }

    private static FeatureEvent[] SelectWindows(IReadOnlyList<FeatureEvent> seedWindows, AnalysisMode mode)
    {
        var maximum = mode switch { AnalysisMode.Fast => 120, AnalysisMode.Deep => 600, _ => 300 };
        var selected = new List<FeatureEvent>();
        foreach (var window in seedWindows
            .Where(window => window.Kind is FeatureKind.GameAudioPeak or FeatureKind.VocalExcitement)
            .OrderByDescending(window => window.Confidence))
        {
            if (selected.Any(existing => Math.Abs((existing.Start - window.Start).TotalSeconds) < 1)) continue;
            selected.Add(window);
            if (selected.Count == maximum) break;
        }
        return selected.OrderBy(window => window.Start).ToArray();
    }

    private static async Task<float[]> ReadSamplesAsync(FileStream pcm, TimeSpan start, CancellationToken cancellationToken)
    {
        var startSample = Math.Max(0, (long)Math.Floor((start - TimeSpan.FromSeconds(0.15)).TotalSeconds * SampleRate));
        pcm.Position = Math.Min(pcm.Length, startSample * sizeof(short));
        var bytes = new byte[RequiredSamples * sizeof(short)];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await pcm.ReadAsync(bytes.AsMemory(read), cancellationToken);
            if (count == 0) break;
            read += count;
        }
        var samples = new float[RequiredSamples];
        for (var index = 0; index < read / sizeof(short); index++)
        {
            samples[index] = BitConverter.ToInt16(bytes, index * sizeof(short)) / 32768f;
        }
        return samples;
    }

    private static async Task ExtractPcmAsync(string sourcePath, int streamIndex, string outputPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegRuntime.ResolveFfmpegPath(),
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", sourcePath,
            "-map", $"0:{streamIndex}", "-vn", "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "s16le", outputPath
        }) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start FFmpeg for YAMNet audio extraction.");
        try
        {
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException($"YAMNet audio extraction failed: {error.Trim()}");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
    }

    private static double[] CreateHannWindow() => Enumerable.Range(0, WindowSamples)
        .Select(index => 0.5 - (0.5 * Math.Cos(2 * Math.PI * index / WindowSamples)))
        .ToArray();

    private static double[][] CreateMelWeights()
    {
        static double HzToMel(double frequency) => 2595 * Math.Log10(1 + (frequency / 700));
        static double MelToHz(double mel) => 700 * (Math.Pow(10, mel / 2595) - 1);
        var minimumMel = HzToMel(125);
        var maximumMel = HzToMel(7_500);
        var points = Enumerable.Range(0, MelBands + 2)
            .Select(index => MelToHz(minimumMel + ((maximumMel - minimumMel) * index / (MelBands + 1))))
            .Select(frequency => (int)Math.Floor((FftSize + 1) * frequency / SampleRate))
            .Select(bin => Math.Clamp(bin, 0, FftSize / 2))
            .ToArray();
        var weights = new double[MelBands][];
        for (var mel = 0; mel < MelBands; mel++)
        {
            weights[mel] = new double[(FftSize / 2) + 1];
            var left = points[mel];
            var center = Math.Max(left + 1, points[mel + 1]);
            var right = Math.Max(center + 1, points[mel + 2]);
            for (var bin = left; bin < center && bin < weights[mel].Length; bin++) weights[mel][bin] = (double)(bin - left) / (center - left);
            for (var bin = center; bin <= right && bin < weights[mel].Length; bin++) weights[mel][bin] = (double)(right - bin) / (right - center);
        }
        return weights;
    }

    private static void ForwardFft(Complex[] values)
    {
        var reversed = 0;
        for (var index = 1; index < values.Length; index++)
        {
            var bit = values.Length >> 1;
            while ((reversed & bit) != 0)
            {
                reversed ^= bit;
                bit >>= 1;
            }
            reversed ^= bit;
            if (index < reversed) (values[index], values[reversed]) = (values[reversed], values[index]);
        }
        for (var length = 2; length <= values.Length; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var root = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var offset = 0; offset < values.Length; offset += length)
            {
                var factor = Complex.One;
                for (var index = 0; index < length / 2; index++)
                {
                    var even = values[offset + index];
                    var odd = values[offset + index + (length / 2)] * factor;
                    values[offset + index] = even + odd;
                    values[offset + index + (length / 2)] = even - odd;
                    factor *= root;
                }
            }
        }
    }
}
