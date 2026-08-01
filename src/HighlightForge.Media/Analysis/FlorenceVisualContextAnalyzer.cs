using System.Diagnostics;
using System.Globalization;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Domain;
using HighlightForge.Media.Runtime;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HighlightForge.Media.Analysis;

public static class FlorenceVisualContextAnalyzer
{
    public const int ImageSize = 768;
    private const int Channels = 3;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] StandardDeviation = [0.229f, 0.224f, 0.225f];

    public static async Task<IReadOnlyList<FeatureEvent>> AnalyzeAsync(
        MediaSource source,
        AnalysisMode mode,
        string modelDirectory,
        TimeSpan? analysisStart = null,
        TimeSpan? analysisLimit = null,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var modelPath = Path.Combine(modelDirectory, "vision_encoder_q4f16.onnx");
        if (!File.Exists(modelPath)) return [];
        var start = analysisStart ?? TimeSpan.Zero;
        var duration = analysisLimit is { } limit && start + limit < source.Duration ? limit : source.Duration - start;
        if (duration <= TimeSpan.Zero) return [];
        var maximumFrames = mode == AnalysisMode.Deep ? 240 : 120;
        var preferredInterval = mode == AnalysisMode.Deep ? 15d : 30d;
        var interval = Math.Max(preferredInterval, duration.TotalSeconds / maximumFrames);
        using var session = new InferenceSession(modelPath);
        var startInfo = BuildExtractionStartInfo(source.AbsolutePath, interval, analysisStart, analysisLimit);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start FFmpeg for Florence-2 frame sampling.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var frameBytes = new byte[ImageSize * ImageSize * Channels];
        var features = new List<FeatureEvent>();
        float[]? previous = null;
        var frameIndex = 0;
        try
        {
            while (frameIndex < maximumFrames)
            {
                var read = await ReadFrameAsync(process.StandardOutput.BaseStream, frameBytes, cancellationToken);
                if (read == 0) break;
                if (read != frameBytes.Length) throw new InvalidDataException("FFmpeg returned a partial Florence-2 frame.");
                var tensor = CreateImageTensor(frameBytes);
                using var outputs = session.Run([NamedOnnxValue.CreateFromTensor("pixel_values", tensor)]);
                var values = outputs.Single(output => output.Name == "image_features").AsEnumerable<float>().ToArray();
                var embedding = MeanPool(values, 768);
                if (previous is not null)
                {
                    var distance = 1 - CosineSimilarity(previous, embedding);
                    if (distance >= 0.08)
                    {
                        var position = start + TimeSpan.FromSeconds(frameIndex * interval);
                        features.Add(new FeatureEvent(
                            FeatureKind.VisualNovelty,
                            position,
                            Min(position + TimeSpan.FromSeconds(interval), start + duration),
                            Math.Clamp(0.45 + distance, 0.45, 0.95),
                            $"Florence-2 visual context shift ({distance:P0})"));
                    }
                }
                previous = embedding;
                frameIndex++;
                progress?.Report(new AnalysisProgress(
                    "visual-context",
                    0.76 + (0.04 * frameIndex / Math.Max(1, maximumFrames)),
                    $"Reviewing sparse Florence-2 visual context frame {frameIndex}."));
            }
            await process.WaitForExitAsync(cancellationToken);
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException($"Florence-2 frame extraction failed: {error.Trim()}");
            return features;
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

    public static DenseTensor<float> CreateImageTensor(ReadOnlySpan<byte> rgb)
    {
        if (rgb.Length != ImageSize * ImageSize * Channels) throw new ArgumentException("A 768x768 RGB frame is required.", nameof(rgb));
        var values = new float[rgb.Length];
        var pixels = ImageSize * ImageSize;
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            for (var channel = 0; channel < Channels; channel++)
            {
                values[(channel * pixels) + pixel] = ((rgb[(pixel * Channels) + channel] / 255f) - Mean[channel]) / StandardDeviation[channel];
            }
        }
        return new DenseTensor<float>(values, [1, Channels, ImageSize, ImageSize]);
    }

    public static float[] MeanPool(IReadOnlyList<float> values, int embeddingSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(embeddingSize, 1);
        if (values.Count == 0 || values.Count % embeddingSize != 0) throw new ArgumentException("Visual embeddings have an unexpected shape.", nameof(values));
        var tokens = values.Count / embeddingSize;
        var result = new float[embeddingSize];
        for (var token = 0; token < tokens; token++)
        {
            for (var component = 0; component < embeddingSize; component++) result[component] += values[(token * embeddingSize) + component];
        }
        for (var component = 0; component < embeddingSize; component++) result[component] /= tokens;
        return result;
    }

    public static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count != right.Count || left.Count == 0) throw new ArgumentException("Visual embeddings must have the same non-zero size.");
        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }
        if (leftMagnitude == 0 || rightMagnitude == 0) return 0;
        return dot / Math.Sqrt(leftMagnitude * rightMagnitude);
    }

    private static ProcessStartInfo BuildExtractionStartInfo(string sourcePath, double interval, TimeSpan? start, TimeSpan? limit)
    {
        var info = new ProcessStartInfo
        {
            FileName = FfmpegRuntime.ResolveFfmpegPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error" }) info.ArgumentList.Add(argument);
        if (start is { } offset && offset > TimeSpan.Zero)
        {
            info.ArgumentList.Add("-ss");
            info.ArgumentList.Add(offset.TotalSeconds.ToString(CultureInfo.InvariantCulture));
        }
        if (limit is { } duration)
        {
            info.ArgumentList.Add("-t");
            info.ArgumentList.Add(duration.TotalSeconds.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var argument in new[]
        {
            "-i", sourcePath, "-map", "0:v:0",
            "-vf", $"fps=1/{interval.ToString(CultureInfo.InvariantCulture)},scale={ImageSize}:{ImageSize}:force_original_aspect_ratio=decrease,pad={ImageSize}:{ImageSize}:(ow-iw)/2:(oh-ih)/2:black",
            "-an", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"
        }) info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task<int> ReadFrameAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (count == 0) break;
            total += count;
        }
        return total;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
}
