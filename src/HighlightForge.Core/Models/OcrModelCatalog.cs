namespace HighlightForge.Core.Models;

public sealed record OcrModelPack(Uri DownloadUri, long DownloadSize, ModelPackManifest Manifest);

public static class OcrModelCatalog
{
    public const string Version = "923915d4ced2a7235221788285785a29c4a42d4a";

    public static OcrModelPack English { get; } = new(
        new Uri($"https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/{Version}/eng.traineddata"),
        4_113_088,
        new ModelPackManifest(
            "tesseract-fast-en",
            Version,
            "Official integerized English Tesseract LSTM data for sparse on-screen text recognition.",
            [new ModelFile("eng.traineddata", "7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2", "Apache-2.0")]));

    public static string InstalledDirectory(string rootDirectory) => Path.Combine(
        Path.GetFullPath(rootDirectory),
        English.Manifest.Id,
        English.Manifest.Version);
}
