using System.Security.Cryptography;
using HighlightForge.Core.Models;
using HighlightForge.Core.Audio;
using HighlightForge.Media.Render;

namespace HighlightForge.Core.Tests;

public sealed class ReleaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HighlightForgeReleaseTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ModelPackVerificationRejectsChangedFiles()
    {
        var staged = Path.Combine(_directory, "staged");
        Directory.CreateDirectory(staged);
        var modelPath = Path.Combine(staged, "model.bin");
        await File.WriteAllTextAsync(modelPath, "local model");
        string hash;
        await using (var stream = File.OpenRead(modelPath))
        {
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        }
        var manifest = new ModelPackManifest("balanced", "1.0.0", "Local model pack", [new ModelFile("model.bin", hash, "MIT")]);
        var manager = new ModelPackManager(Path.Combine(_directory, "packs"));

        await manager.InstallFromDirectoryAsync(manifest, staged);
        var installed = await manager.ValidateAsync(manifest);
        await File.WriteAllTextAsync(Path.Combine(_directory, "packs", "balanced", "1.0.0", "model.bin"), "modified");
        var changed = await manager.ValidateAsync(manifest);

        Assert.True(installed.IsInstalled);
        Assert.False(changed.IsInstalled);
    }

    [Fact]
    public async Task ModelPackCannotEscapeItsVersionDirectory()
    {
        var staged = Path.Combine(_directory, "staged-escape");
        Directory.CreateDirectory(staged);
        var manifest = new ModelPackManifest("balanced", "1.0.0", "Local model pack", [new ModelFile("..\\outside.bin", new string('0', 64), "MIT")]);
        var manager = new ModelPackManager(Path.Combine(_directory, "packs"));

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallFromDirectoryAsync(manifest, staged));
    }

    [Fact]
    public void ShortPlanPreservesGameFrameOverBlurredVerticalBackground()
    {
        var arguments = RenderPlan.BuildArguments(new RenderRequest(RenderKind.Vertical, "source.mkv", "short.mp4", new AudioMixSettings()));

        Assert.Contains(arguments, argument => argument.Contains("boxblur=20", StringComparison.Ordinal));
        Assert.Contains("h264_mf", arguments);
        Assert.Contains(arguments, argument => argument.StartsWith("loudnorm=I=-14", StringComparison.Ordinal));
    }

    [Fact]
    public void ExportRejectsWritingOverTheOriginalRecording()
    {
        var source = Path.Combine(_directory, "original.mkv");
        var request = new RenderRequest(RenderKind.LongForm, source, source, new AudioMixSettings());

        var exception = Assert.Throws<InvalidOperationException>(() => RenderPlan.BuildArguments(request));

        Assert.Contains("cannot overwrite the original recording", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
