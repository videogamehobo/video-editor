using System.Security.Cryptography;
using System.Text.Json;

namespace HighlightForge.Core.Models;

public sealed record ModelFile(string RelativePath, string Sha256, string License);
public sealed record ModelPackManifest(string Id, string Version, string Description, IReadOnlyList<ModelFile> Files);
public sealed record ModelPackStatus(string Id, string Version, bool IsInstalled, IReadOnlyList<string> Problems);
public sealed record InstalledModelPackVersion(ModelPackManifest Manifest, bool IsActive, ModelPackStatus Status);

public sealed class ModelPackManager
{
    private readonly string _rootDirectory;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ModelPackManager(string rootDirectory) => _rootDirectory = Path.GetFullPath(rootDirectory);

    public async Task<ModelPackStatus> ValidateAsync(ModelPackManifest manifest, CancellationToken cancellationToken = default)
    {
        var packDirectory = ResolvePackVersionDirectory(manifest.Id, manifest.Version);
        var problems = new List<string>();
        foreach (var file in manifest.Files)
        {
            var path = ResolveWithin(packDirectory, file.RelativePath);
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
        var destination = ResolvePackVersionDirectory(manifest.Id, manifest.Version);
        Directory.CreateDirectory(destination);
        foreach (var file in manifest.Files)
        {
            var sourceFile = ResolveWithin(source, file.RelativePath);
            if (!File.Exists(sourceFile)) throw new FileNotFoundException("Staged model file is missing.", sourceFile);
            var destinationFile = ResolveWithin(destination, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: true);
        }
        await File.WriteAllTextAsync(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(manifest, Options), cancellationToken);
        var status = await ValidateAsync(manifest, cancellationToken);
        if (!status.IsInstalled) throw new InvalidOperationException(string.Join("; ", status.Problems));
    }

    public async Task<IReadOnlyList<InstalledModelPackVersion>> ListInstalledVersionsAsync(
        string packId,
        CancellationToken cancellationToken = default)
    {
        var packDirectory = ResolvePackDirectory(packId);
        if (!Directory.Exists(packDirectory)) return [];
        var activeVersion = await ReadMarkerAsync(packDirectory, "active-version.txt", cancellationToken);
        var versions = new List<InstalledModelPackVersion>();
        foreach (var versionDirectory in Directory.EnumerateDirectories(packDirectory))
        {
            var manifestPath = Path.Combine(versionDirectory, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            var manifest = JsonSerializer.Deserialize<ModelPackManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), Options);
            if (manifest is null || !string.Equals(manifest.Id, packId, StringComparison.Ordinal)) continue;
            var status = await ValidateAsync(manifest, cancellationToken);
            versions.Add(new InstalledModelPackVersion(manifest, string.Equals(manifest.Version, activeVersion, StringComparison.Ordinal), status));
        }
        return versions.OrderByDescending(version => version.IsActive).ThenByDescending(version => version.Manifest.Version, StringComparer.Ordinal).ToArray();
    }

    public async Task<InstalledModelPackVersion?> GetActiveVersionAsync(
        string packId,
        CancellationToken cancellationToken = default) =>
        (await ListInstalledVersionsAsync(packId, cancellationToken))
            .SingleOrDefault(version => version.IsActive && version.Status.IsInstalled);

    public async Task ActivateAsync(ModelPackManifest manifest, CancellationToken cancellationToken = default)
    {
        var status = await ValidateAsync(manifest, cancellationToken);
        if (!status.IsInstalled) throw new InvalidOperationException(string.Join("; ", status.Problems));
        var packDirectory = ResolvePackDirectory(manifest.Id);
        Directory.CreateDirectory(packDirectory);
        var current = await ReadMarkerAsync(packDirectory, "active-version.txt", cancellationToken);
        if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, manifest.Version, StringComparison.Ordinal))
        {
            await WriteMarkerAsync(packDirectory, "previous-version.txt", current, cancellationToken);
        }
        await WriteMarkerAsync(packDirectory, "active-version.txt", manifest.Version, cancellationToken);
    }

    public async Task<ModelPackManifest> RollbackAsync(string packId, CancellationToken cancellationToken = default)
    {
        var packDirectory = ResolvePackDirectory(packId);
        var previous = await ReadMarkerAsync(packDirectory, "previous-version.txt", cancellationToken);
        if (string.IsNullOrWhiteSpace(previous)) throw new InvalidOperationException($"Model pack '{packId}' has no previous installed version to restore.");
        var manifestPath = Path.Combine(ResolvePackVersionDirectory(packId, previous), "manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidOperationException($"The previous model version '{previous}' is no longer installed.");
        var manifest = JsonSerializer.Deserialize<ModelPackManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), Options)
            ?? throw new InvalidDataException("The previous model manifest is invalid.");
        var current = await ReadMarkerAsync(packDirectory, "active-version.txt", cancellationToken);
        var status = await ValidateAsync(manifest, cancellationToken);
        if (!status.IsInstalled) throw new InvalidOperationException(string.Join("; ", status.Problems));
        await WriteMarkerAsync(packDirectory, "active-version.txt", previous, cancellationToken);
        if (!string.IsNullOrWhiteSpace(current)) await WriteMarkerAsync(packDirectory, "previous-version.txt", current, cancellationToken);
        return manifest;
    }

    private string ResolvePackDirectory(string packId)
    {
        ValidateSegment(packId, nameof(packId));
        return ResolveWithin(_rootDirectory, packId);
    }

    private string ResolvePackVersionDirectory(string packId, string version)
    {
        ValidateSegment(packId, nameof(packId));
        ValidateSegment(version, nameof(version));
        return ResolveWithin(ResolvePackDirectory(packId), version);
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 || value is "." or "..")
        {
            throw new InvalidDataException($"Model pack path segment '{value}' is invalid.");
        }
    }

    private static async Task<string?> ReadMarkerAsync(string packDirectory, string fileName, CancellationToken cancellationToken)
    {
        var path = ResolveWithin(packDirectory, fileName);
        return File.Exists(path) ? (await File.ReadAllTextAsync(path, cancellationToken)).Trim() : null;
    }

    private static async Task WriteMarkerAsync(string packDirectory, string fileName, string value, CancellationToken cancellationToken)
    {
        var path = ResolveWithin(packDirectory, fileName);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporaryPath, value, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string ResolveWithin(string rootDirectory, string relativePath)
    {
        var root = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Model file path '{relativePath}' escapes its model-pack directory.");
        }
        return path;
    }
}
