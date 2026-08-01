namespace HighlightForge.Core.Models;

public sealed record PhiModelAsset(Uri DownloadUri, long DownloadSize, ModelFile File);
public sealed record PhiModelPack(IReadOnlyList<PhiModelAsset> Assets, ModelPackManifest Manifest)
{
    public long DownloadSize => Assets.Sum(asset => asset.DownloadSize);
}

public static class PhiModelCatalog
{
    public const string Revision = "fc04c8f93df696602fd9f300a30d1bf2e3081347";
    private const string Repository = "microsoft/Phi-4-mini-instruct-onnx";
    private const string ModelPath = "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4";

    public static PhiModelPack MiniInstructCpuInt4 { get; } = Create();

    public static string InstalledDirectory(string rootDirectory) => Path.Combine(
        Path.GetFullPath(rootDirectory),
        MiniInstructCpuInt4.Manifest.Id,
        MiniInstructCpuInt4.Manifest.Version);

    private static PhiModelPack Create()
    {
        var files = new (string Name, long Size, string Hash)[]
        {
            ("added_tokens.json", 249, "D4F2ACEB0F20B71DD1F4BCC7E052E4412946BF281840B8F83D39F259571AF486"),
            ("config.json", 2_504, "AC65D86061D3D0D704EE2511FD0EB8713EF19EB6EEDBA17C3080A4165D5B933B"),
            ("genai_config.json", 1_520, "0FCFA1E663F2BC867F8DC62FAE65DD0924F0A4D68B43D1234DF742DD19171470"),
            ("merges.txt", 2_418_348, "856CE61180BB689282EED6B3A6838BB1F438399BE23AEFE9D20EB379791FB4AD"),
            ("model.onnx", 52_118_230, "701AA5D185B6A782BC27104A990DD5B634FA507840B7C42F7EE6F1FB812D0B83"),
            ("model.onnx.data", 4_856_573_952, "CB0267FA60BEFA1A4ADE8C98B6D32A3D67F51ABBD307C7F793F132E8D9092131"),
            ("special_tokens_map.json", 587, "AFF38493227D813E29FCF8406E8E90062F1F031AA47D589325E9C31D89AC7CC3"),
            ("tokenizer.json", 15_524_095, "382CC235B56C725945E149CC25F191DA667C836655EFD0857B004320E90E91EA"),
            ("tokenizer_config.json", 2_960, "C565326A315FBE62CDA093A59D298828C8F3F823122661325F41F3BA577A7DEC"),
            ("vocab.json", 3_910_310, "6CB65A857824FA6615BB1782D95D882617A8BBCE1DA0317118586B36F39E98BD")
        };
        var assets = files.Select(file =>
        {
            var modelFile = new ModelFile(file.Name, file.Hash, "MIT");
            return new PhiModelAsset(
                new Uri($"https://huggingface.co/{Repository}/resolve/{Revision}/{ModelPath}/{file.Name}"),
                file.Size,
                modelFile);
        }).ToArray();
        return new PhiModelPack(
            assets,
            new ModelPackManifest(
                "phi-4-mini-instruct-cpu-int4",
                Revision,
                "Microsoft Phi-4-mini-instruct CPU INT4 ONNX model for local highlight narrative and voice-over prompts.",
                assets.Select(asset => asset.File).ToArray()));
    }
}
