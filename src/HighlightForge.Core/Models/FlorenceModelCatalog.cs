namespace HighlightForge.Core.Models;

public sealed record FlorenceModelAsset(Uri DownloadUri, long DownloadSize, ModelFile File);
public sealed record FlorenceModelPack(IReadOnlyList<FlorenceModelAsset> Assets, ModelPackManifest Manifest);

public static class FlorenceModelCatalog
{
    public const string Revision = "e88a44eaf3791a35eae0c5a47b3dbcd36e67eb6f";

    public static FlorenceModelPack BaseFtVisualEncoder { get; } = Create();

    public static string InstalledDirectory(string rootDirectory) => Path.Combine(
        Path.GetFullPath(rootDirectory),
        BaseFtVisualEncoder.Manifest.Id,
        BaseFtVisualEncoder.Manifest.Version);

    private static FlorenceModelPack Create()
    {
        var model = new ModelFile(
            "vision_encoder_q4f16.onnx",
            "1E993FB7081302294B5C286B2CC6C2A63283959F399317DC2BE49ECA94F2DD18",
            "MIT");
        var preprocessing = new ModelFile(
            "preprocessor_config.json",
            "C892857E34A7082284983A7717717D39C9BF7E574F1F41D80D4C918C97502EFA",
            "MIT");
        return new FlorenceModelPack(
            [
                new FlorenceModelAsset(
                    new Uri($"https://huggingface.co/onnx-community/Florence-2-base-ft/resolve/{Revision}/onnx/vision_encoder_q4f16.onnx"),
                    62_416_644,
                    model),
                new FlorenceModelAsset(
                    new Uri($"https://huggingface.co/onnx-community/Florence-2-base-ft/resolve/{Revision}/preprocessor_config.json"),
                    2_673,
                    preprocessing)
            ],
            new ModelPackManifest(
                "florence-2-base-ft-visual-q4f16",
                Revision,
                "Florence-2-base-ft quantized ONNX visual encoder for sparse local context embeddings.",
                [model, preprocessing]));
    }
}
