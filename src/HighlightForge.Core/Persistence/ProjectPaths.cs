namespace HighlightForge.Core.Persistence;

public sealed class ProjectPaths
{
    public ProjectPaths(string projectDirectory)
    {
        ProjectDirectory = Path.GetFullPath(projectDirectory);
    }

    public string ProjectDirectory { get; }
    public string ManifestPath => Path.Combine(ProjectDirectory, "project.json");
    public string DatabasePath => Path.Combine(ProjectDirectory, "project.db");
    public string CacheDirectory => Path.Combine(ProjectDirectory, "cache");
    public string RenderCacheDirectory => Path.Combine(CacheDirectory, "render");
    public string TakesDirectory => Path.Combine(ProjectDirectory, "takes");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ProjectDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(RenderCacheDirectory);
        Directory.CreateDirectory(TakesDirectory);
    }
}
