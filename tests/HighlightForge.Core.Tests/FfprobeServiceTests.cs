using HighlightForge.Media.Probe;
using HighlightForge.Media.Audio;
using HighlightForge.Media.Proxy;
using HighlightForge.Media.Runtime;

namespace HighlightForge.Core.Tests;

public sealed class FfprobeServiceTests
{
    [Fact]
    public void ParseReadsVideoAndSeparateAudioTracks()
    {
        const string json = """
        { "format": { "duration": "120.5" }, "streams": [
          { "index": 0, "codec_type": "video", "width": 1920, "height": 1080, "avg_frame_rate": "60000/1001" },
          { "index": 1, "codec_type": "audio", "channels": 2, "sample_rate": "48000", "tags": { "title": "Mixed" } },
          { "index": 2, "codec_type": "audio", "channels": 1, "sample_rate": "48000", "tags": { "title": "Microphone" } },
          { "index": 3, "codec_type": "audio", "channels": 2, "sample_rate": "48000", "tags": { "title": "Game" } }
        ] }
        """;

        var result = FfprobeService.Parse(@"C:\recordings\session.mkv", json);

        Assert.Equal(TimeSpan.FromSeconds(120.5), result.Duration);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.Equal(59.94, result.FramesPerSecond, 2);
        Assert.Collection(result.AudioTracks,
            track => Assert.Equal("Mixed", track.DisplayName),
            track => Assert.Equal("Microphone", track.DisplayName),
            track => Assert.Equal("Game", track.DisplayName));
    }
}

public sealed class FfmpegRuntimeTests
{
    [Fact]
    public void ResolverPrefersAnExplicitPathThenABundledPath()
    {
        Assert.Equal("C:\\tools\\ffprobe.exe", FfmpegRuntime.Resolve("ffprobe.exe", "C:\\tools\\ffprobe.exe"));
        var bundled = Path.GetTempFileName();
        try
        {
            Assert.Equal(bundled, FfmpegRuntime.Resolve("ffprobe.exe", null, [bundled]));
        }
        finally
        {
            File.Delete(bundled);
        }
    }

    [Fact]
    public void ResolverFindsAnExecutableRegisteredAsAnAdditionalPathCandidate()
    {
        var executable = Path.GetTempFileName();
        try
        {
            Assert.Equal(executable, FfmpegRuntime.Resolve("ffprobe.exe", null, [executable]));
        }
        finally
        {
            File.Delete(executable);
        }
    }
}

public sealed class EditingCoreTests
{
    [Fact]
    public void TrackMapperUsesDiscreteTracksAndExcludesTheMixedTrack()
    {
        var mapping = AudioTrackMapper.Suggest(
        [
            new(1, "Mixed", 2, 48000),
            new(2, "Microphone", 1, 48000),
            new(3, "Game", 2, 48000)
        ]);

        Assert.True(mapping.UsesDiscreteTracks);
        Assert.DoesNotContain(mapping.TracksForMix(), track => track.Role == HighlightForge.Core.Domain.AudioTrackRole.Mixed);
        Assert.Equal(2, mapping.TracksForMix().Count);
    }

    [Fact]
    public void ProxyCommandCreatesAudioFreeDisposableMp4()
    {
        var arguments = ProxyGenerationService.BuildArguments(new ProxyRequest(Guid.NewGuid(), "source.mkv", "cache/proxy.mp4"));

        Assert.Contains("-an", arguments);
        Assert.Contains("scale=-2:540", arguments);
        Assert.Equal("cache/proxy.mp4", arguments[^1]);
    }

    [Fact]
    public void ProxyGenerationRejectsWritingOverTheOriginalRecording()
    {
        var source = Path.Combine(Path.GetTempPath(), "recording.mkv");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProxyGenerationService.BuildArguments(new ProxyRequest(Guid.NewGuid(), source, Path.Combine(Path.GetTempPath(), ".", "recording.mkv"))));

        Assert.Contains("cannot overwrite the original recording", exception.Message, StringComparison.Ordinal);
    }
}
