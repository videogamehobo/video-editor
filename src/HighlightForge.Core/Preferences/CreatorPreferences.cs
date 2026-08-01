using System.Text.Json;

namespace HighlightForge.Core.Preferences;

public sealed record CreatorPreferences(
    double FunnyWeight = 1,
    double ActionWeight = 1,
    double StoryWeight = 1,
    HashSet<Guid>? AcceptedCandidateIds = null,
    HashSet<Guid>? RejectedCandidateIds = null);

public sealed class CreatorPreferencesStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public CreatorPreferencesStore(string projectDirectory) => _path = Path.Combine(projectDirectory, "preferences.json");

    public async Task SaveAsync(CreatorPreferences preferences, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(preferences, Options), cancellationToken);
        File.Move(temporaryPath, _path, overwrite: true);
    }

    public async Task<CreatorPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return new CreatorPreferences();
        return JsonSerializer.Deserialize<CreatorPreferences>(await File.ReadAllTextAsync(_path, cancellationToken), Options) ?? new CreatorPreferences();
    }
}
