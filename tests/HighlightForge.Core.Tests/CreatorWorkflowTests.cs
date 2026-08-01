using HighlightForge.Core.Analysis;
using HighlightForge.Core.Audio;
using HighlightForge.Core.Captions;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Preferences;
using HighlightForge.Core.Voiceover;
using HighlightForge.Media.Audio;
using HighlightForge.Media.Render;

namespace HighlightForge.Core.Tests;

public sealed class CreatorWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HighlightForgeCreatorTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CaptionExportCreatesPlatformReadySrtAndVtt()
    {
        var cues = new[] { new CaptionCue(TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(3.4), "That was close!") };

        Assert.Contains("00:00:01,200 --> 00:00:03,400", CaptionDocument.ToSrt(cues));
        Assert.StartsWith("WEBVTT", CaptionDocument.ToWebVtt(cues));
    }

    [Fact]
    public void VoiceoverPlannerMarksUnexplainedAction()
    {
        var clips = new[] { new HighlightCandidate(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2.5), 1.2, [new SelectionReason(FeatureKind.GameAudioPeak, 0.8, "boss fight finish")]) };

        var suggestion = Assert.Single(VoiceoverPlanner.Suggest(clips));

        Assert.Contains("boss fight finish", suggestion.TalkingPoint);
    }

    [Fact]
    public void MixPlanNeverUsesMixedTrackWithDiscreteTracks()
    {
        var mapping = AudioTrackMapper.Suggest([new(1, "Mixed", 2, 48000), new(2, "Microphone", 1, 48000), new(3, "Game", 2, 48000)]);
        var plan = AudioMixPlanner.Create(mapping.Tracks, mapping.UsesDiscreteTracks);

        Assert.True(plan.UsesDiscreteTracks);
        Assert.DoesNotContain(plan.InputTracks, track => track.Role == AudioTrackRole.Mixed);
        Assert.Contains("loudnorm=I=-14", AudioMixPlanner.BuildFinalLoudnessFilter(plan.Settings));
    }

    [Fact]
    public async Task PreferencesRemainLocalToTheProject()
    {
        var store = new CreatorPreferencesStore(_directory);
        await store.SaveAsync(new CreatorPreferences(FunnyWeight: 1.3));

        var loaded = await store.LoadAsync();

        Assert.Equal(1.3, loaded.FunnyWeight);
        Assert.True(File.Exists(Path.Combine(_directory, "preferences.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
