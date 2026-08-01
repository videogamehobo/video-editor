using System.Security.Cryptography;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Models;

namespace HighlightForge.Media.Models;

public sealed class FlorenceModelInstaller
{
    private readonly HttpClient _httpClient;
    private readonly string _rootDirectory;
    private readonly ModelPackManager _manager;

    public FlorenceModelInstaller(HttpClient httpClient, string? rootDirectory = null)
    {
        _httpClient = httpClient;
        _rootDirectory = Path.GetFullPath(rootDirectory ?? WhisperModelCatalog.DefaultRootDirectory);
        _manager = new ModelPackManager(_rootDirectory);
    }

    public async Task<string?> GetInstalledDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var active = await _manager.GetActiveVersionAsync(FlorenceModelCatalog.BaseFtVisualEncoder.Manifest.Id, cancellationToken);
        return active is not null && active.Status.IsInstalled
            ? FlorenceModelCatalog.InstalledDirectory(_rootDirectory)
            : null;
    }

    public async Task<string> InstallAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installed = await GetInstalledDirectoryAsync(cancellationToken);
        if (installed is not null) return installed;
        var pack = FlorenceModelCatalog.BaseFtVisualEncoder;
        var stagingDirectory = Path.Combine(_rootDirectory, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var totalSize = pack.Assets.Sum(asset => asset.DownloadSize);
            long completedBytes = 0;
            foreach (var asset in pack.Assets)
            {
                var outputPath = Path.Combine(stagingDirectory, asset.File.RelativePath);
                using var response = await _httpClient.GetAsync(asset.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
                {
                    var buffer = new byte[1024 * 128];
                    long received = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        received += count;
                        progress?.Report(new ModelDownloadProgress(completedBytes + received, totalSize));
                    }
                    completedBytes += received;
                }
                await using var stream = File.OpenRead(outputPath);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!string.Equals(actual, asset.File.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The downloaded Florence-2 asset '{asset.File.RelativePath}' failed SHA-256 verification.");
                }
            }
            await _manager.InstallFromDirectoryAsync(pack.Manifest, stagingDirectory, cancellationToken);
            await _manager.ActivateAsync(pack.Manifest, cancellationToken);
            var directory = FlorenceModelCatalog.InstalledDirectory(_rootDirectory);
            await HighlightForgeLog.InfoAsync($"Installed and verified local Florence-2 visual model at '{directory}'.", cancellationToken);
            return directory;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
        }
    }
}
