using System.Security.Cryptography;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Models;

namespace HighlightForge.Media.Models;

public sealed class OcrModelInstaller
{
    private readonly HttpClient _httpClient;
    private readonly string _rootDirectory;
    private readonly ModelPackManager _manager;

    public OcrModelInstaller(HttpClient httpClient, string? rootDirectory = null)
    {
        _httpClient = httpClient;
        _rootDirectory = Path.GetFullPath(rootDirectory ?? WhisperModelCatalog.DefaultRootDirectory);
        _manager = new ModelPackManager(_rootDirectory);
    }

    public async Task<string?> GetInstalledDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var active = await _manager.GetActiveVersionAsync(OcrModelCatalog.English.Manifest.Id, cancellationToken);
        return active is not null && active.Status.IsInstalled
            ? OcrModelCatalog.InstalledDirectory(_rootDirectory)
            : null;
    }

    public async Task<string> InstallAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installed = await GetInstalledDirectoryAsync(cancellationToken);
        if (installed is not null) return installed;
        var pack = OcrModelCatalog.English;
        var stagingDirectory = Path.Combine(_rootDirectory, ".staging", Guid.NewGuid().ToString("N"));
        var modelPath = Path.Combine(stagingDirectory, pack.Manifest.Files[0].RelativePath);
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            using var response = await _httpClient.GetAsync(pack.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? pack.DownloadSize;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(modelPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
            {
                var buffer = new byte[1024 * 128];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    received += count;
                    progress?.Report(new ModelDownloadProgress(received, total));
                }
            }
            await using (var stream = File.OpenRead(modelPath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!string.Equals(actual, pack.Manifest.Files[0].Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The downloaded English OCR model failed SHA-256 verification.");
                }
            }
            await _manager.InstallFromDirectoryAsync(pack.Manifest, stagingDirectory, cancellationToken);
            await _manager.ActivateAsync(pack.Manifest, cancellationToken);
            var directory = OcrModelCatalog.InstalledDirectory(_rootDirectory);
            await HighlightForgeLog.InfoAsync($"Installed and verified local English OCR model at '{directory}'.", cancellationToken);
            return directory;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
        }
    }
}
