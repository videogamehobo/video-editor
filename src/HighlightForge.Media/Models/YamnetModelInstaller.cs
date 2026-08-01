using System.IO.Compression;
using System.Security.Cryptography;
using HighlightForge.Core.Diagnostics;
using HighlightForge.Core.Models;

namespace HighlightForge.Media.Models;

public sealed class YamnetModelInstaller
{
    private readonly HttpClient _httpClient;
    private readonly string _rootDirectory;
    private readonly ModelPackManager _manager;

    public YamnetModelInstaller(HttpClient httpClient, string? rootDirectory = null)
    {
        _httpClient = httpClient;
        _rootDirectory = Path.GetFullPath(rootDirectory ?? WhisperModelCatalog.DefaultRootDirectory);
        _manager = new ModelPackManager(_rootDirectory);
    }

    public async Task<string?> GetInstalledDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var active = await _manager.GetActiveVersionAsync(YamnetModelCatalog.Pack.Manifest.Id, cancellationToken);
        return active is not null && active.Status.IsInstalled
            ? YamnetModelCatalog.InstalledDirectory(_rootDirectory)
            : null;
    }

    public async Task<string> InstallAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installed = await GetInstalledDirectoryAsync(cancellationToken);
        if (installed is not null) return installed;

        var pack = YamnetModelCatalog.Pack;
        var stagingRoot = Path.Combine(_rootDirectory, ".staging");
        var stagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(stagingDirectory, "yamnet.zip");
        var extractionDirectory = Path.Combine(stagingDirectory, "archive");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            await DownloadVerifiedAsync(pack.ArchiveUri, archivePath, pack.ArchiveSha256, pack.ArchiveSize, progress, cancellationToken);
            ZipFile.ExtractToDirectory(archivePath, extractionDirectory);
            var extractedRoot = Path.Combine(extractionDirectory, "yamnet-onnx-float");
            File.Copy(Path.Combine(extractedRoot, "yamnet.onnx"), Path.Combine(stagingDirectory, "yamnet.onnx"));
            File.Copy(Path.Combine(extractedRoot, "yamnet.data"), Path.Combine(stagingDirectory, "yamnet.data"));
            await DownloadVerifiedAsync(
                pack.ClassMapUri,
                Path.Combine(stagingDirectory, "yamnet_class_map.csv"),
                pack.Manifest.Files.Single(file => file.RelativePath == "yamnet_class_map.csv").Sha256,
                14_096,
                null,
                cancellationToken);
            await _manager.InstallFromDirectoryAsync(pack.Manifest, stagingDirectory, cancellationToken);
            await _manager.ActivateAsync(pack.Manifest, cancellationToken);
            var directory = YamnetModelCatalog.InstalledDirectory(_rootDirectory);
            await HighlightForgeLog.InfoAsync($"Installed and verified local YAMNet model pack at '{directory}'.", cancellationToken);
            return directory;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private async Task DownloadVerifiedAsync(
        Uri uri,
        string outputPath,
        string expectedSha256,
        long expectedSize,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expectedSize;
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
                progress?.Report(new ModelDownloadProgress(received, total));
            }
        }
        await using var stream = File.OpenRead(outputPath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The downloaded local model asset '{uri}' failed SHA-256 verification.");
        }
    }
}
