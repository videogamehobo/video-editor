using System.Diagnostics;
using HighlightForge.Core.Captions;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Persistence;
using HighlightForge.Media.Audio;
using HighlightForge.Media.Runtime;
using Whisper.net;

namespace HighlightForge.Media.Captions;

public sealed record TranscriptionProgress(double Fraction, string Detail);

public static class WhisperTranscriptionService
{
    public static async Task<IReadOnlyList<CaptionCue>> TranscribeAsync(
        ProjectPaths projectPaths,
        MediaSource source,
        string modelPath,
        IProgress<TranscriptionProgress>? progress = null,
        TimeSpan? sourceStart = null,
        TimeSpan? sourceLimit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("The selected local Whisper model is not installed.", modelPath);

        var preferredTracks = source.AudioTracks
            .OrderBy(track => track.Role switch
            {
                AudioTrackRole.Microphone => 0,
                AudioTrackRole.Mixed => 1,
                AudioTrackRole.Game => 2,
                _ => 3
            })
            .ToArray();
        if (preferredTracks.Length == 0) return [];

        projectPaths.EnsureDirectories();
        var transcriptionDirectory = Path.Combine(projectPaths.CacheDirectory, "transcription", source.Id.ToString("N"));
        Directory.CreateDirectory(transcriptionDirectory);
        using var factory = WhisperFactory.FromPath(modelPath);

        foreach (var track in preferredTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new TranscriptionProgress(0.02, $"Checking {track.DisplayName} for usable speech audio"));
            var loudness = await AudioLoudnessAnalyzer.MeasureAsync(source.AbsolutePath, track, sourceStart, sourceLimit, cancellationToken);
            if (loudness.IntegratedLufs <= -70 || loudness.TruePeakDbtp <= -70)
            {
                await HighlightForgeLog.InfoAsync($"Skipping effectively silent audio stream {track.StreamIndex} before transcription.", cancellationToken);
                continue;
            }
            var wavPath = Path.Combine(transcriptionDirectory, $"stream-{track.StreamIndex}.wav");
            MediaPathSafety.RequireSeparateOutput(source.AbsolutePath, wavPath, "Transcription audio extraction");
            MediaPathSafety.RequireOutputWithinDirectory(projectPaths.CacheDirectory, wavPath, "Transcription audio extraction");
            progress?.Report(new TranscriptionProgress(0.05, $"Extracting {track.DisplayName} audio into the disposable project cache"));
            await ExtractAudioAsync(source.AbsolutePath, track.StreamIndex, wavPath, sourceStart, sourceLimit, cancellationToken);

            var words = new List<CaptionWord>();
            using var processor = factory.CreateBuilder()
                .WithLanguage("en")
                .WithTokenTimestamps()
                .SplitOnWord()
                .Build();
            await using var audio = File.OpenRead(wavPath);
            await foreach (var segment in processor.ProcessAsync(audio, cancellationToken))
            {
                var text = segment.Text.Trim();
                var offset = sourceStart ?? TimeSpan.Zero;
                if (text.Length > 0 && segment.End > segment.Start)
                {
                    words.Add(new CaptionWord(segment.Start + offset, segment.End + offset, text));
                }
                var transcriptionDuration = sourceLimit ?? source.Duration;
                var fraction = transcriptionDuration <= TimeSpan.Zero ? 0.5 : Math.Clamp(segment.End.TotalSeconds / transcriptionDuration.TotalSeconds, 0, 1);
                progress?.Report(new TranscriptionProgress(0.1 + (fraction * 0.9), $"Transcribing locally: {segment.End + offset:hh\\:mm\\:ss}"));
            }

            if (words.Count > 0)
            {
                var cues = CaptionDocument.GroupWords(words);
                await HighlightForgeLog.InfoAsync($"Local transcription produced {words.Count} timed words in {cues.Count} caption cues from audio stream {track.StreamIndex}.", cancellationToken);
                return cues;
            }

            await HighlightForgeLog.InfoAsync($"Audio stream {track.StreamIndex} produced no speech; trying the next read-only source track.", cancellationToken);
        }

        return [];
    }

    public static IReadOnlyList<string> BuildExtractionArguments(
        string sourcePath,
        int streamIndex,
        string outputPath,
        TimeSpan? sourceStart = null,
        TimeSpan? sourceLimit = null)
    {
        MediaPathSafety.RequireSeparateOutput(sourcePath, outputPath, "Transcription audio extraction");
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (sourceStart is not null) arguments.AddRange(["-ss", sourceStart.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-i", sourcePath]);
        if (sourceLimit is not null) arguments.AddRange(["-t", sourceLimit.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-map", $"0:{streamIndex}", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", outputPath]);
        return arguments;
    }

    private static async Task ExtractAudioAsync(
        string sourcePath,
        int streamIndex,
        string outputPath,
        TimeSpan? sourceStart,
        TimeSpan? sourceLimit,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegRuntime.ResolveFfmpegPath(),
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in BuildExtractionArguments(sourcePath, streamIndex, outputPath, sourceStart, sourceLimit)) startInfo.ArgumentList.Add(argument);
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
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg could not extract the commentary track: {error.Trim()}");
    }
}
