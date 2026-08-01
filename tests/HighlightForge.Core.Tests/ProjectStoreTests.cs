using HighlightForge.Core.Domain;
using HighlightForge.Core.Persistence;

namespace HighlightForge.Core.Tests;

public sealed class ProjectStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HighlightForgeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoadPreservesAProjectWithoutCopyingSources()
    {
        var store = new ProjectStore(new ProjectPaths(_directory));
        var project = ProjectDocument.Create("Stream highlights", new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)) with
        {
            Sources = [new MediaSource(Guid.NewGuid(), @"D:\OBS\session.mkv", TimeSpan.FromMinutes(90), 1920, 1080, 60, [])]
        };

        await store.SaveAsync(project);
        var restored = await store.LoadAsync();

        Assert.NotNull(restored);
        Assert.Equal(project.Id, restored.Id);
        Assert.Equal(@"D:\OBS\session.mkv", restored.Sources.Single().AbsolutePath);
        Assert.False(File.Exists(Path.Combine(_directory, "session.mkv")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
