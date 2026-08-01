using System.Text.Json;
using HighlightForge.Core.Analysis;
using Microsoft.Data.Sqlite;

namespace HighlightForge.Core.Persistence;

public sealed record AnalysisJobCheckpoint(
    Guid JobId,
    string Stage,
    double Progress,
    DateTimeOffset UpdatedUtc,
    string? Detail = null,
    Guid SourceId = default,
    AnalysisMode Mode = AnalysisMode.Balanced,
    AnalysisJobStatus Status = AnalysisJobStatus.Running,
    IReadOnlyList<FeatureEvent>? Features = null);

public sealed class AnalysisJobStore
{
    private readonly ProjectPaths _paths;

    public AnalysisJobStore(ProjectPaths paths) => _paths = paths;

    public async Task SaveAsync(AnalysisJobCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO analysis_checkpoint (job_id, source_id, mode, status, stage, progress, updated_utc, detail, features_json)
            VALUES ($jobId, $sourceId, $mode, $status, $stage, $progress, $updatedUtc, $detail, $features)
            ON CONFLICT(job_id) DO UPDATE SET
                source_id = excluded.source_id,
                mode = excluded.mode,
                status = excluded.status,
                stage = excluded.stage,
                progress = excluded.progress,
                updated_utc = excluded.updated_utc,
                detail = excluded.detail,
                features_json = excluded.features_json;
            """;
        command.Parameters.AddWithValue("$jobId", checkpoint.JobId.ToString("D"));
        command.Parameters.AddWithValue("$sourceId", checkpoint.SourceId.ToString("D"));
        command.Parameters.AddWithValue("$mode", checkpoint.Mode.ToString());
        command.Parameters.AddWithValue("$status", checkpoint.Status.ToString());
        command.Parameters.AddWithValue("$stage", checkpoint.Stage);
        command.Parameters.AddWithValue("$progress", checkpoint.Progress);
        command.Parameters.AddWithValue("$updatedUtc", checkpoint.UpdatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$detail", (object?)checkpoint.Detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$features", JsonSerializer.Serialize(checkpoint.Features, SerializerOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AnalysisJobCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_id, mode, status, stage, progress, updated_utc, detail, features_json
            FROM analysis_checkpoint WHERE job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCheckpoint(jobId, reader) : null;
    }

    public async Task<AnalysisJobCheckpoint?> LoadLatestForSourceAsync(
        Guid sourceId,
        AnalysisMode mode,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, source_id, mode, status, stage, progress, updated_utc, detail, features_json
            FROM analysis_checkpoint
            WHERE source_id = $sourceId AND mode = $mode
            ORDER BY updated_utc DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId.ToString("D"));
        command.Parameters.AddWithValue("$mode", mode.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return ReadCheckpoint(Guid.Parse(reader.GetString(0)), reader, 1);
    }

    private static async Task EnsureTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var table = connection.CreateCommand();
        table.CommandText = """
            CREATE TABLE IF NOT EXISTS analysis_checkpoint (
                job_id TEXT PRIMARY KEY,
                source_id TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                mode TEXT NOT NULL DEFAULT 'Balanced',
                status TEXT NOT NULL DEFAULT 'Running',
                stage TEXT NOT NULL,
                progress REAL NOT NULL,
                updated_utc TEXT NOT NULL,
                detail TEXT NULL,
                features_json TEXT NOT NULL DEFAULT '[]'
            );
            """;
        await table.ExecuteNonQueryAsync(cancellationToken);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(analysis_checkpoint);";
        await using (var reader = await pragma.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        }
        var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source_id"] = "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'",
            ["mode"] = "TEXT NOT NULL DEFAULT 'Balanced'",
            ["status"] = "TEXT NOT NULL DEFAULT 'Running'",
            ["features_json"] = "TEXT NOT NULL DEFAULT '[]'"
        };
        foreach (var addition in additions.Where(pair => !columns.Contains(pair.Key)))
        {
            var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE analysis_checkpoint ADD COLUMN {addition.Key} {addition.Value};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static AnalysisJobCheckpoint ReadCheckpoint(Guid jobId, SqliteDataReader reader, int offset = 0)
    {
        var sourceId = Guid.TryParse(reader.GetString(offset), out var parsedSourceId) ? parsedSourceId : Guid.Empty;
        var mode = Enum.TryParse<AnalysisMode>(reader.GetString(offset + 1), out var parsedMode) ? parsedMode : AnalysisMode.Balanced;
        var status = Enum.TryParse<AnalysisJobStatus>(reader.GetString(offset + 2), out var parsedStatus) ? parsedStatus : AnalysisJobStatus.Running;
        var featuresJson = reader.IsDBNull(offset + 7) ? "[]" : reader.GetString(offset + 7);
        var features = JsonSerializer.Deserialize<IReadOnlyList<FeatureEvent>?>(featuresJson, SerializerOptions);
        return new AnalysisJobCheckpoint(
            jobId,
            reader.GetString(offset + 3),
            reader.GetDouble(offset + 4),
            DateTimeOffset.Parse(reader.GetString(offset + 5), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6),
            sourceId,
            mode,
            status,
            features);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private SqliteConnection OpenConnection() => new($"Data Source={_paths.DatabasePath};Pooling=False");
}
