using System.Text.Json;
using HighlightForge.Core.Analysis;
using Microsoft.Data.Sqlite;

namespace HighlightForge.Core.Persistence;

public sealed class AnalysisResultStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ProjectPaths _paths;

    public AnalysisResultStore(ProjectPaths paths) => _paths = paths;

    public async Task SaveAsync(LocalAnalysisResult result, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var table = connection.CreateCommand();
        table.CommandText = """
            CREATE TABLE IF NOT EXISTS analysis_result (
                source_id TEXT PRIMARY KEY,
                result_json TEXT NOT NULL,
                completed_utc TEXT NOT NULL
            );
            """;
        await table.ExecuteNonQueryAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO analysis_result (source_id, result_json, completed_utc)
            VALUES ($sourceId, $result, $completedUtc)
            ON CONFLICT(source_id) DO UPDATE SET
                result_json = excluded.result_json,
                completed_utc = excluded.completed_utc;
            """;
        command.Parameters.AddWithValue("$sourceId", result.SourceId.ToString("D"));
        command.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result, SerializerOptions));
        command.Parameters.AddWithValue("$completedUtc", result.CompletedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LocalAnalysisResult?> LoadAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var table = connection.CreateCommand();
        table.CommandText = """
            CREATE TABLE IF NOT EXISTS analysis_result (
                source_id TEXT PRIMARY KEY,
                result_json TEXT NOT NULL,
                completed_utc TEXT NOT NULL
            );
            """;
        await table.ExecuteNonQueryAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = "SELECT result_json FROM analysis_result WHERE source_id = $sourceId;";
        command.Parameters.AddWithValue("$sourceId", sourceId.ToString("D"));
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : JsonSerializer.Deserialize<LocalAnalysisResult>(json, SerializerOptions);
    }

    private SqliteConnection OpenConnection() => new($"Data Source={_paths.DatabasePath};Pooling=False");
}
