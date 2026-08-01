namespace HighlightForge.Core.Persistence;

public sealed class ProjectPaths
{
    public ProjectPaths(string projectDirectory)
    {
        ProjectDirectory = Path.GetFullPath(projectDirectory);
    }

    public string ProjectDirectory { get; }
    public string DatabasePath => Path.Combine(ProjectDirectory, "project.db");
    public string CacheDirectory => Path.Combine(ProjectDirectory, "cache");
    public string TakesDirectory => Path.Combine(ProjectDirectory, "takes");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ProjectDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(TakesDirectory);
    }
}
