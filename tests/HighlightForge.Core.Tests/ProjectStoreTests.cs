using HighlightForge.Core.Domain;
using HighlightForge.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace HighlightForge.Core.Tests;

public sealed class ProjectStoreTests : IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HighlightForgeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoadPreservesAProjectWithoutCopyingSources()
    {
        var store = new ProjectStore(new ProjectPaths(_directory));
        var project = ProjectDocument.Create("Stream highlights", new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)) with
        {
            Sources = [new MediaSource(Guid.NewGuid(), @"D:\OBS\session.mkv", TimeSpan.FromMinutes(90), 1920, 1080, 60, [], AudioRolesConfirmed: true)]
        };

        await store.SaveAsync(project);
        var restored = await store.LoadAsync();

        Assert.NotNull(restored);
        Assert.Equal(project.Id, restored.Id);
        Assert.Equal(@"D:\OBS\session.mkv", restored.Sources.Single().AbsolutePath);
        Assert.True(restored.Sources.Single().AudioRolesConfirmed);
        Assert.False(File.Exists(Path.Combine(_directory, "session.mkv")));
        Assert.True(File.Exists(new ProjectPaths(_directory).ManifestPath));
    }

    [Fact]
    public async Task VersionOneProjectMigratesAndRecordsTheCurrentSchema()
    {
        var paths = new ProjectPaths(Path.Combine(_directory, "migration.gheproj"));
        var project = ProjectDocument.Create("Old project", DateTimeOffset.UtcNow) with { SchemaVersion = 1 };
        var json = System.Text.Json.JsonSerializer.Serialize(project, JsonOptions);
        var store = new ProjectStore(paths);
        await store.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO project_document (id, schema_version, document_json, modified_utc) VALUES (1, 1, $json, $modified);";
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$modified", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var migrated = await store.LoadAsync();

        Assert.NotNull(migrated);
        Assert.Equal(ProjectSchema.CurrentVersion, migrated.SchemaVersion);
        Assert.True(File.Exists(paths.ManifestPath));
        await using var verifyConnection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False");
        await verifyConnection.OpenAsync();
        var verify = verifyConnection.CreateCommand();
        verify.CommandText = "SELECT schema_version FROM project_document WHERE id = 1;";
        Assert.Equal((long)ProjectSchema.CurrentVersion, await verify.ExecuteScalarAsync());
        verify.CommandText = "SELECT COUNT(*) FROM schema_migration WHERE version = $version;";
        verify.Parameters.AddWithValue("$version", ProjectSchema.CurrentVersion);
        Assert.Equal(1L, await verify.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PortableManifestRecoversProjectWhenDatabaseDocumentIsMissing()
    {
        var paths = new ProjectPaths(Path.Combine(_directory, "recovery.gheproj"));
        var store = new ProjectStore(paths);
        var project = ProjectDocument.Create("Recovery", DateTimeOffset.UtcNow);
        await store.SaveAsync(project);
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var clear = connection.CreateCommand();
            clear.CommandText = "DELETE FROM project_document;";
            await clear.ExecuteNonQueryAsync();
        }

        var recovered = await store.LoadAsync();

        Assert.Equal(project.Id, recovered?.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
