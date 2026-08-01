using HighlightForge.Core.Domain;
using HighlightForge.Media.Audio;
using HighlightForge.Media.Probe;

namespace HighlightForge.Media.Import;

public sealed record ImportedSource(MediaSource Source, AudioTrackMapping SuggestedTrackMapping);

public sealed class SourceImportService
{
    public static async Task<ImportedSource> ImportAsync(string mediaPath, CancellationToken cancellationToken = default)
    {
        var probe = await FfprobeService.ProbeAsync(mediaPath, cancellationToken);
        var mapping = AudioTrackMapper.Suggest(probe.AudioTracks);
        var source = probe.ToSource() with { AudioTracks = mapping.Tracks };
        return new ImportedSource(source, mapping);
    }
}
