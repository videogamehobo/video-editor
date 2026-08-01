using System.Text.Json;
using HighlightForge.Core.Domain;
using Microsoft.Data.Sqlite;

namespace HighlightForge.Core.Persistence;

public sealed class ProjectStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ProjectPaths _paths;

    public ProjectStore(ProjectPaths paths)
    {
        _paths = paths;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS project_document (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                schema_version INTEGER NOT NULL,
                document_json TEXT NOT NULL,
                modified_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAsync(ProjectDocument project, CancellationToken cancellationToken = default)
    {
        if (project.SchemaVersion != ProjectSchema.CurrentVersion)
        {
            throw new InvalidOperationException($"Unsupported project schema {project.SchemaVersion}.");
        }

        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO project_document (id, schema_version, document_json, modified_utc)
            VALUES (1, $schemaVersion, $document, $modifiedUtc)
            ON CONFLICT(id) DO UPDATE SET
                schema_version = excluded.schema_version,
                document_json = excluded.document_json,
                modified_utc = excluded.modified_utc;
            """;
        command.Parameters.AddWithValue("$schemaVersion", project.SchemaVersion);
        command.Parameters.AddWithValue("$document", JsonSerializer.Serialize(project, SerializerOptions));
        command.Parameters.AddWithValue("$modifiedUtc", project.ModifiedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ProjectDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT document_json FROM project_document WHERE id = 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken) as string;
        return result is null ? null : JsonSerializer.Deserialize<ProjectDocument>(result, SerializerOptions);
    }

    private SqliteConnection OpenConnection() => new($"Data Source={_paths.DatabasePath};Pooling=False");
}
