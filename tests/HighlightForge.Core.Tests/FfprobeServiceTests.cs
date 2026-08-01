using HighlightForge.Media.Probe;

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
