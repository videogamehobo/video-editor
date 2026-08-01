using System.Diagnostics;
using System.Globalization;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Persistence;
using HighlightForge.Media.Runtime;
using Tesseract;

namespace HighlightForge.Media.Analysis;

public static class OcrFeatureAnalyzer
{
    public static async Task<IReadOnlyList<FeatureEvent>> AnalyzeAsync(
        ProjectPaths paths,
        MediaSource source,
        AnalysisMode mode,
        string modelDirectory,
        TimeSpan? analysisStart = null,
        TimeSpan? analysisLimit = null,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path.Combine(modelDirectory, "eng.traineddata"))) return [];
        var start = analysisStart ?? TimeSpan.Zero;
        var duration = analysisLimit is { } limit && start + limit < source.Duration ? limit : source.Duration - start;
        if (duration <= TimeSpan.Zero) return [];
        var preferredInterval = mode switch { AnalysisMode.Fast => 30d, AnalysisMode.Deep => 8d, _ => 15d };
        var interval = Math.Max(preferredInterval, duration.TotalSeconds / 300);
        var frameDirectory = Path.Combine(paths.CacheDirectory, "analysis", source.Id.ToString("N"), $"ocr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(frameDirectory);
        var pattern = Path.Combine(frameDirectory, "frame-%05d.png");
        MediaPathSafety.RequireOutputWithinDirectory(paths.CacheDirectory, pattern, "OCR frame extraction");
        try
        {
            progress?.Report(new AnalysisProgress("ocr", 0.80, "Extracting sparse disposable frames for local on-screen text recognition."));
            await ExtractFramesAsync(source.AbsolutePath, pattern, interval, analysisStart, analysisLimit, cancellationToken);
            var files = Directory.EnumerateFiles(frameDirectory, "frame-*.png").OrderBy(path => path, StringComparer.Ordinal).ToArray();
            var features = new List<FeatureEvent>();
            using var engine = new TesseractEngine(modelDirectory, "eng", EngineMode.LstmOnly);
            string? previousText = null;
            for (var index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var image = Pix.LoadFromFile(files[index]);
                using var page = engine.Process(image, PageSegMode.SparseText);
                var text = NormalizeText(page.GetText());
                if (text.Length >= 3 && !string.Equals(text, previousText, StringComparison.OrdinalIgnoreCase))
                {
                    var position = start + TimeSpan.FromSeconds(index * interval);
                    features.Add(new FeatureEvent(
                        FeatureKind.OnScreenText,
                        position,
                        Min(position + TimeSpan.FromSeconds(interval), start + duration),
                        Math.Clamp(page.GetMeanConfidence(), 0.35f, 0.95f),
                        $"on-screen text: {TrimDetail(text)}"));
                    previousText = text;
                }
                progress?.Report(new AnalysisProgress("ocr", 0.80 + (0.05 * (index + 1d) / Math.Max(1, files.Length)), $"Reading local frame text {index + 1}/{files.Length}."));
            }
            return features;
        }
        finally
        {
            if (Directory.Exists(frameDirectory)) Directory.Delete(frameDirectory, recursive: true);
        }
    }

    public static string NormalizeText(string? text) => string.Join(' ',
        (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static async Task ExtractFramesAsync(
        string sourcePath,
        string outputPattern,
        double interval,
        TimeSpan? start,
        TimeSpan? limit,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegRuntime.ResolveFfmpegPath(),
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y" }) startInfo.ArgumentList.Add(argument);
        if (start is { } offset && offset > TimeSpan.Zero)
        {
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(offset.TotalSeconds.ToString(CultureInfo.InvariantCulture));
        }
        if (limit is { } duration)
        {
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(duration.TotalSeconds.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var argument in new[]
        {
            "-i", sourcePath, "-map", "0:v:0", "-vf", $"fps=1/{interval.ToString(CultureInfo.InvariantCulture)},scale=960:-2",
            "-an", "-vsync", "vfr", outputPattern
        }) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start FFmpeg for local OCR frame extraction.");
        try
        {
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException($"OCR frame extraction failed: {error.Trim()}");
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

    private static string TrimDetail(string text) => text.Length <= 140 ? text : $"{text[..137]}...";
    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
}
