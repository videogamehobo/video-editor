using Microsoft.Data.Sqlite;

namespace HighlightForge.Core.Persistence;

public sealed record AnalysisJobCheckpoint(Guid JobId, string Stage, double Progress, DateTimeOffset UpdatedUtc, string? Detail = null);

public sealed class AnalysisJobStore
{
    private readonly ProjectPaths _paths;

    public AnalysisJobStore(ProjectPaths paths) => _paths = paths;

    public async Task SaveAsync(AnalysisJobCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var table = connection.CreateCommand();
        table.CommandText = """
            CREATE TABLE IF NOT EXISTS analysis_checkpoint (
                job_id TEXT PRIMARY KEY,
                stage TEXT NOT NULL,
                progress REAL NOT NULL,
                updated_utc TEXT NOT NULL,
                detail TEXT NULL
            );
            """;
        await table.ExecuteNonQueryAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO analysis_checkpoint (job_id, stage, progress, updated_utc, detail)
            VALUES ($jobId, $stage, $progress, $updatedUtc, $detail)
            ON CONFLICT(job_id) DO UPDATE SET stage = excluded.stage, progress = excluded.progress, updated_utc = excluded.updated_utc, detail = excluded.detail;
            """;
        command.Parameters.AddWithValue("$jobId", checkpoint.JobId.ToString("D"));
        command.Parameters.AddWithValue("$stage", checkpoint.Stage);
        command.Parameters.AddWithValue("$progress", checkpoint.Progress);
        command.Parameters.AddWithValue("$updatedUtc", checkpoint.UpdatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$detail", (object?)checkpoint.Detail ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AnalysisJobCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT stage, progress, updated_utc, detail FROM analysis_checkpoint WHERE job_id = $jobId;";
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(jobId, reader.GetString(0), reader.GetDouble(1), DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private SqliteConnection OpenConnection() => new($"Data Source={_paths.DatabasePath};Pooling=False");
}
