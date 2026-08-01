using System.Security.Cryptography;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Models;

namespace HighlightForge.Media.Models;

public sealed record ModelDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesReceived / TotalBytes, 0, 1);
}

public sealed class WhisperModelInstaller
{
    private readonly HttpClient _httpClient;
    private readonly string _rootDirectory;
    private readonly ModelPackManager _manager;

    public WhisperModelInstaller(HttpClient httpClient, string? rootDirectory = null)
    {
        _httpClient = httpClient;
        _rootDirectory = Path.GetFullPath(rootDirectory ?? WhisperModelCatalog.DefaultRootDirectory);
        _manager = new ModelPackManager(_rootDirectory);
    }

    public async Task<string?> GetInstalledModelPathAsync(WhisperModelPack pack, CancellationToken cancellationToken = default)
    {
        var status = await _manager.ValidateAsync(pack.Manifest, cancellationToken);
        return status.IsInstalled ? WhisperModelCatalog.InstalledModelPath(_rootDirectory, pack) : null;
    }

    public async Task<string?> GetActiveModelPathAsync(WhisperModelPack pack, CancellationToken cancellationToken = default)
    {
        var active = await _manager.GetActiveVersionAsync(pack.Manifest.Id, cancellationToken);
        if (active is null) return null;
        var modelFile = active.Manifest.Files.SingleOrDefault(file =>
            string.Equals(file.RelativePath, pack.ModelFile.RelativePath, StringComparison.Ordinal));
        return modelFile is null
            ? null
            : Path.GetFullPath(Path.Combine(_rootDirectory, active.Manifest.Id, active.Manifest.Version, modelFile.RelativePath));
    }

    public async Task<string> InstallAsync(
        WhisperModelPack pack,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installed = await GetInstalledModelPathAsync(pack, cancellationToken);
        if (installed is not null)
        {
            await _manager.ActivateAsync(pack.Manifest, cancellationToken);
            return installed;
        }

        var stagingRoot = Path.Combine(_rootDirectory, ".staging");
        var stagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        var stagedModelPath = Path.Combine(stagingDirectory, pack.ModelFile.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedModelPath)!);
        try
        {
            await HighlightForgeLog.InfoAsync($"Downloading local model pack '{pack.Manifest.Id}' version '{pack.Manifest.Version}'.", cancellationToken);
            using var response = await _httpClient.GetAsync(pack.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? pack.DownloadSize;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(stagedModelPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
            {
                var buffer = new byte[1024 * 128];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    received += count;
                    progress?.Report(new ModelDownloadProgress(received, totalBytes));
                }
            }

            await using (var stream = File.OpenRead(stagedModelPath))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!string.Equals(actualHash, pack.ModelFile.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The downloaded {pack.DisplayName} model failed SHA-256 verification.");
                }
            }

            await _manager.InstallFromDirectoryAsync(pack.Manifest, stagingDirectory, cancellationToken);
            await _manager.ActivateAsync(pack.Manifest, cancellationToken);
            var modelPath = WhisperModelCatalog.InstalledModelPath(_rootDirectory, pack);
            await HighlightForgeLog.InfoAsync($"Installed verified local model pack at '{modelPath}'.", cancellationToken);
            return modelPath;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
        }
    }
}
