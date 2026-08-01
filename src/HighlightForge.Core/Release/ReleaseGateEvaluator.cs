using HighlightForge.Core.Analysis;

namespace HighlightForge.Core.Release;

public sealed record BenchmarkMoment(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}

public sealed record CreatorBenchmarkSession(
    string Id,
    IReadOnlyList<BenchmarkMoment> MustKeepMoments,
    IReadOnlyList<BenchmarkMoment> CreatorAcceptedMoments,
    IReadOnlyList<HighlightCandidate> ReviewQueue,
    IReadOnlyList<HighlightCandidate> DraftClips);

public sealed record CreatorBenchmarkReport(
    int SessionCount,
    int MustKeepCount,
    int RecalledMustKeepCount,
    int DraftClipCount,
    int AcceptedDraftClipCount,
    double MustKeepRecall,
    double DraftAcceptance,
    bool Passed,
    IReadOnlyList<string> Problems);

public static class CreatorBenchmarkGate
{
    public const int MinimumSessionCount = 10;
    public const double MinimumMustKeepRecall = 0.80;
    public const double MinimumDraftAcceptance = 0.60;

    public static CreatorBenchmarkReport Evaluate(IReadOnlyList<CreatorBenchmarkSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var problems = new List<string>();
        var duplicateIds = sessions.GroupBy(session => session.Id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicateIds.Length > 0) problems.Add($"Duplicate benchmark session IDs: {string.Join(", ", duplicateIds)}.");
        if (sessions.Count < MinimumSessionCount) problems.Add($"At least {MinimumSessionCount} creator-annotated sessions are required; found {sessions.Count}.");

        foreach (var session in sessions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(session.Id);
            ValidateMoments(session.MustKeepMoments, session.Id, "must-keep");
            ValidateMoments(session.CreatorAcceptedMoments, session.Id, "accepted");
        }

        var mustKeepCount = sessions.Sum(session => session.MustKeepMoments.Count);
        var recalled = sessions.Sum(session => session.MustKeepMoments.Count(moment => session.ReviewQueue.Any(candidate => Overlaps(moment, candidate))));
        var draftCount = sessions.Sum(session => session.DraftClips.Count);
        var accepted = sessions.Sum(session => session.DraftClips.Count(candidate => session.CreatorAcceptedMoments.Any(moment => Overlaps(moment, candidate))));
        var recall = mustKeepCount == 0 ? 0 : (double)recalled / mustKeepCount;
        var acceptance = draftCount == 0 ? 0 : (double)accepted / draftCount;
        if (mustKeepCount == 0) problems.Add("Benchmark sessions contain no creator-designated must-keep moments.");
        else if (recall < MinimumMustKeepRecall) problems.Add($"Must-keep recall is {recall:P1}; at least {MinimumMustKeepRecall:P0} is required.");
        if (draftCount == 0) problems.Add("Benchmark sessions contain no ranked draft clips.");
        else if (acceptance < MinimumDraftAcceptance) problems.Add($"Top-draft acceptance is {acceptance:P1}; at least {MinimumDraftAcceptance:P0} is required.");

        return new CreatorBenchmarkReport(sessions.Count, mustKeepCount, recalled, draftCount, accepted, recall, acceptance, problems.Count == 0, problems);
    }

    private static bool Overlaps(BenchmarkMoment moment, HighlightCandidate candidate) =>
        moment.End > candidate.SourceIn && moment.Start < candidate.SourceOut;

    private static void ValidateMoments(IReadOnlyList<BenchmarkMoment> moments, string sessionId, string kind)
    {
        if (moments.Any(moment => moment.Start < TimeSpan.Zero || moment.End <= moment.Start))
        {
            throw new InvalidDataException($"Benchmark session '{sessionId}' has an invalid {kind} interval.");
        }
    }
}

public sealed record AnalysisPerformanceReport(
    TimeSpan RecordingDuration,
    TimeSpan AnalysisDuration,
    bool CpuOnly,
    bool Completed,
    bool PauseResumeRecovered,
    bool Passed,
    IReadOnlyList<string> Problems);

public static class AnalysisPerformanceGate
{
    public static AnalysisPerformanceReport Evaluate(
        TimeSpan recordingDuration,
        TimeSpan analysisDuration,
        bool cpuOnly,
        bool completed,
        bool pauseResumeRecovered)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(recordingDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(analysisDuration, TimeSpan.Zero);
        var problems = new List<string>();
        if (!completed) problems.Add("Analysis did not complete.");
        if (!pauseResumeRecovered) problems.Add("Pause/resume or crash recovery did not complete successfully.");
        if (!cpuOnly && analysisDuration > TimeSpan.FromTicks(recordingDuration.Ticks / 2))
        {
            problems.Add("Balanced GPU analysis exceeded half the recording duration.");
        }
        return new AnalysisPerformanceReport(recordingDuration, analysisDuration, cpuOnly, completed, pauseResumeRecovered, problems.Count == 0, problems);
    }
}
