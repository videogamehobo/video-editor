using System.Security.Cryptography;
using System.Text.Json;

namespace HighlightForge.Core.Models;

public sealed record ModelFile(string RelativePath, string Sha256, string License);
public sealed record ModelPackManifest(string Id, string Version, string Description, IReadOnlyList<ModelFile> Files);
public sealed record ModelPackStatus(string Id, string Version, bool IsInstalled, IReadOnlyList<string> Problems);

public sealed class ModelPackManager
{
    private readonly string _rootDirectory;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ModelPackManager(string rootDirectory) => _rootDirectory = Path.GetFullPath(rootDirectory);

    public async Task<ModelPackStatus> ValidateAsync(ModelPackManifest manifest, CancellationToken cancellationToken = default)
    {
        var packDirectory = Path.Combine(_rootDirectory, manifest.Id, manifest.Version);
        var problems = new List<string>();
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(packDirectory, file.RelativePath);
            if (!File.Exists(path))
            {
                problems.Add($"Missing {file.RelativePath}");
                continue;
            }
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase)) problems.Add($"Checksum mismatch for {file.RelativePath}");
        }
        return new(manifest.Id, manifest.Version, problems.Count == 0, problems);
    }

    public async Task InstallFromDirectoryAsync(ModelPackManifest manifest, string stagedDirectory, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(stagedDirectory);
        var destination = Path.Combine(_rootDirectory, manifest.Id, manifest.Version);
        Directory.CreateDirectory(destination);
        foreach (var file in manifest.Files)
        {
            var sourceFile = Path.Combine(source, file.RelativePath);
            if (!File.Exists(sourceFile)) throw new FileNotFoundException("Staged model file is missing.", sourceFile);
            var destinationFile = Path.Combine(destination, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: true);
        }
        await File.WriteAllTextAsync(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(manifest, Options), cancellationToken);
        var status = await ValidateAsync(manifest, cancellationToken);
        if (!status.IsInstalled) throw new InvalidOperationException(string.Join("; ", status.Problems));
    }
}
