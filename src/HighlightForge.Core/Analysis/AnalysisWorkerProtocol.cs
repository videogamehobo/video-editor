using HighlightForge.Core.Domain;

namespace HighlightForge.Core.Analysis;

public enum AnalysisJobStatus
{
    Pending,
    Running,
    Paused,
    Completed,
    Failed
}

public sealed record AnalysisWorkerCapabilities(
    int LogicalProcessorCount,
    bool NvidiaAvailable,
    IReadOnlyList<string> H264Encoders,
    string OperatingSystem,
    bool LocalOnly = true);

public sealed record AnalysisWorkerRequest(
    Guid JobId,
    string ProjectDirectory,
    MediaSource Source,
    AnalysisMode Mode,
    bool Resume = true);

public sealed record AnalysisWorkerCommand(string Command);

public sealed record AnalysisWorkerMessage(
    string Kind,
    Guid JobId,
    double Progress,
    string Detail,
    string? Stage = null,
    AnalysisJobStatus? Status = null,
    AnalysisWorkerCapabilities? Capabilities = null,
    LocalAnalysisResult? Result = null,
    string? Error = null);
