using HighlightForge.Core.Analysis;

namespace HighlightForge.Core.Models;

public sealed record WhisperModelPack(
    AnalysisMode Mode,
    string DisplayName,
    Uri DownloadUri,
    long DownloadSize,
    ModelPackManifest Manifest)
{
    public ModelFile ModelFile => Manifest.Files.Single();
}

public static class WhisperModelCatalog
{
    public const string Revision = "5359861c739e955e79d9a303bcbc70fb988958b1";
    private const string License = "MIT";

    public static IReadOnlyList<WhisperModelPack> All { get; } =
    [
        Create(AnalysisMode.Fast, "Fast (Whisper base.en)", "ggml-base.en.bin", 147_964_211, "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002"),
        Create(AnalysisMode.Balanced, "Balanced (Whisper small.en)", "ggml-small.en.bin", 487_614_201, "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d"),
        Create(AnalysisMode.Deep, "Deep (Whisper medium.en)", "ggml-medium.en.bin", 1_533_774_781, "cc37e93478338ec7700281a7ac30a10128929eb8f427dda2e865faa8f6da4356")
    ];

    public static WhisperModelPack ForMode(AnalysisMode mode) => All.Single(pack => pack.Mode == mode);

    public static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HighlightForge", "models");

    public static string InstalledModelPath(string rootDirectory, WhisperModelPack pack) => Path.Combine(
        Path.GetFullPath(rootDirectory), pack.Manifest.Id, pack.Manifest.Version, pack.ModelFile.RelativePath);

    private static WhisperModelPack Create(AnalysisMode mode, string displayName, string fileName, long size, string sha256)
    {
        var id = $"whisper-{mode.ToString().ToLowerInvariant()}-en";
        var manifest = new ModelPackManifest(id, Revision, displayName, [new ModelFile(fileName, sha256, License)]);
        var url = new Uri($"https://huggingface.co/ggerganov/whisper.cpp/resolve/{Revision}/{fileName}");
        return new WhisperModelPack(mode, displayName, url, size, manifest);
    }
}
