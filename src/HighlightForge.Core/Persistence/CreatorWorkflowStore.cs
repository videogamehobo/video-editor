using System.Text.Json;
using HighlightForge.Core.Audio;
using HighlightForge.Core.Captions;
using HighlightForge.Core.Voiceover;
using Microsoft.Data.Sqlite;

namespace HighlightForge.Core.Persistence;

public sealed record CreatorWorkflowState(
    Guid SourceId,
    IReadOnlyList<CaptionCue> Captions,
    IReadOnlyList<VoiceoverTake> VoiceoverTakes,
    IReadOnlyList<AudioLoudnessMeasurement> LoudnessMeasurements,
    DateTimeOffset ModifiedUtc,
    CaptionStyleSettings? CaptionStyle = null,
    AudioMixSettings? AudioSettings = null)
{
    public static CreatorWorkflowState Empty(Guid sourceId) => new(
        sourceId,
        [],
        [],
        [],
        DateTimeOffset.UtcNow,
        new CaptionStyleSettings(),
        new AudioMixSettings());
}

public sealed class CreatorWorkflowStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ProjectPaths _paths;

    public CreatorWorkflowStore(ProjectPaths paths) => _paths = paths;

    public async Task SaveAsync(CreatorWorkflowState state, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO creator_workflow (source_id, state_json, modified_utc)
            VALUES ($sourceId, $state, $modifiedUtc)
            ON CONFLICT(source_id) DO UPDATE SET
                state_json = excluded.state_json,
                modified_utc = excluded.modified_utc;
            """;
        command.Parameters.AddWithValue("$sourceId", state.SourceId.ToString("D"));
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state, SerializerOptions));
        command.Parameters.AddWithValue("$modifiedUtc", state.ModifiedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CreatorWorkflowState> LoadAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json FROM creator_workflow WHERE source_id = $sourceId;";
        command.Parameters.AddWithValue("$sourceId", sourceId.ToString("D"));
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null
            ? CreatorWorkflowState.Empty(sourceId)
            : JsonSerializer.Deserialize<CreatorWorkflowState>(json, SerializerOptions) ?? CreatorWorkflowState.Empty(sourceId);
    }

    private static async Task EnsureTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var table = connection.CreateCommand();
        table.CommandText = """
            CREATE TABLE IF NOT EXISTS creator_workflow (
                source_id TEXT PRIMARY KEY,
                state_json TEXT NOT NULL,
                modified_utc TEXT NOT NULL
            );
            """;
        await table.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection OpenConnection() => new($"Data Source={_paths.DatabasePath};Pooling=False");
}
