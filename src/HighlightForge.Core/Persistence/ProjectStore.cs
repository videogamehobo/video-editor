using System.Text.Json;
using System.Text.Json.Nodes;
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
            CREATE TABLE IF NOT EXISTS schema_migration (
                version INTEGER PRIMARY KEY,
                applied_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        var migration = connection.CreateCommand();
        migration.CommandText = """
            INSERT INTO schema_migration (version, applied_utc)
            VALUES (1, $appliedUtc)
            ON CONFLICT(version) DO NOTHING;
            """;
        migration.Parameters.AddWithValue("$appliedUtc", DateTimeOffset.UtcNow.ToString("O"));
        await migration.ExecuteNonQueryAsync(cancellationToken);
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
        var migration = connection.CreateCommand();
        migration.CommandText = "INSERT INTO schema_migration (version, applied_utc) VALUES ($version, $appliedUtc) ON CONFLICT(version) DO NOTHING;";
        migration.Parameters.AddWithValue("$version", project.SchemaVersion);
        migration.Parameters.AddWithValue("$appliedUtc", DateTimeOffset.UtcNow.ToString("O"));
        await migration.ExecuteNonQueryAsync(cancellationToken);
        await WriteManifestAsync(project, cancellationToken);
    }

    public async Task<ProjectDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version, document_json FROM project_document WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        int? storedVersion = null;
        string? json = null;
        if (await reader.ReadAsync(cancellationToken))
        {
            storedVersion = reader.GetInt32(0);
            json = reader.GetString(1);
        }
        await reader.DisposeAsync();
        if (json is null && File.Exists(_paths.ManifestPath))
        {
            json = await File.ReadAllTextAsync(_paths.ManifestPath, cancellationToken);
            storedVersion = ReadSchemaVersion(json);
        }
        if (json is null || storedVersion is null) return null;
        var project = ProjectMigration.Migrate(json, storedVersion.Value);
        if (storedVersion != ProjectSchema.CurrentVersion) await SaveAsync(project, cancellationToken);
        return project;
    }

    private async Task WriteManifestAsync(ProjectDocument project, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{_paths.ManifestPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(project, SerializerOptions), cancellationToken);
        File.Move(temporaryPath, _paths.ManifestPath, overwrite: true);
    }

    private static int ReadSchemaVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var value))
        {
            throw new InvalidDataException("Project manifest has no valid schemaVersion.");
        }
        return value;
    }

    private SqliteConnection OpenConnection() => new($"Data Source={_paths.DatabasePath};Pooling=False");
}

public static class ProjectMigration
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static ProjectDocument Migrate(string json, int storedVersion)
    {
        if (storedVersion < 1) throw new InvalidDataException($"Project schema {storedVersion} is invalid.");
        if (storedVersion > ProjectSchema.CurrentVersion)
        {
            throw new InvalidOperationException($"Project schema {storedVersion} is newer than this version of HighlightForge supports.");
        }
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Project document JSON is invalid.");
        var version = storedVersion;
        while (version < ProjectSchema.CurrentVersion)
        {
            version = version switch
            {
                1 => MigrateVersion1To2(root),
                _ => throw new InvalidOperationException($"No migration exists for project schema {version}.")
            };
        }
        root["schemaVersion"] = version;
        return root.Deserialize<ProjectDocument>(SerializerOptions) ?? throw new InvalidDataException("Migrated project document is invalid.");
    }

    private static int MigrateVersion1To2(JsonObject root)
    {
        if (root["sources"] is JsonArray sources)
        {
            foreach (var source in sources.OfType<JsonObject>())
            {
                source.TryAdd("audioRolesConfirmed", false);
            }
        }
        return 2;
    }
}
