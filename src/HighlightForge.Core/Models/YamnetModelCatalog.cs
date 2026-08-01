namespace HighlightForge.Core.Models;

public sealed record YamnetModelPack(
    Uri ArchiveUri,
    string ArchiveSha256,
    long ArchiveSize,
    Uri ClassMapUri,
    ModelPackManifest Manifest);

public static class YamnetModelCatalog
{
    public const string Version = "v0.58.0-qairt-2.45.0";

    public static YamnetModelPack Pack { get; } = new(
        new Uri("https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/yamnet/releases/v0.58.0/yamnet-onnx-float.zip"),
        "6D1B0B8C5CE4FE4529A797AE22C256E4312541C67117CF0632E5063080A75013",
        13_883_322,
        new Uri("https://raw.githubusercontent.com/tensorflow/models/dfffd623b6be8d1d9744b8e261fbac370d17c46d/research/audioset/yamnet/yamnet_class_map.csv"),
        new ModelPackManifest(
            "yamnet-sound-events",
            Version,
            "Qualcomm AI Hub YAMNet ONNX float model with the official TensorFlow AudioSet class map.",
            [
                new ModelFile("yamnet.onnx", "CDBE3856099AEC4CB7B73D4C0571D40E5BD5C7EE6E534EC419A46554EFF4DEC2", "Apache-2.0"),
                new ModelFile("yamnet.data", "D4DC721C9F1161233AA19D14285CCE1F5539593378A7E75B19C308EC13BA8AEB", "Apache-2.0"),
                new ModelFile("yamnet_class_map.csv", "CDF24D193E196D9E95912A2667051AE203E92A2BA09449218CCB40EF787C6DF2", "Apache-2.0")
            ]));

    public static string InstalledDirectory(string rootDirectory) => Path.Combine(
        Path.GetFullPath(rootDirectory),
        Pack.Manifest.Id,
        Pack.Manifest.Version);
}
