using System.Globalization;
using System.Text;
using HighlightForge.Core.Analysis;
using HighlightForge.Core.Voiceover;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace HighlightForge.Media.Analysis;

public static class PhiNarrativeService
{
    private const int MaximumCandidates = 12;

    public static IReadOnlyList<HighlightCandidate> SelectCandidates(IReadOnlyList<HighlightCandidate> candidates) =>
        candidates
            .Where(candidate => !candidate.Reasons.Any(reason => reason.Kind == FeatureKind.Speech))
            .Select(HighlightScorer.EnsureIdentity)
            .Take(MaximumCandidates)
            .ToArray();

    public static string BuildPrompt(IReadOnlyList<HighlightCandidate> candidates)
    {
        var selected = SelectCandidates(candidates);
        var prompt = new StringBuilder()
            .AppendLine("<|system|>")
            .AppendLine("You help a gaming creator record concise human voice-over. Never invent game facts. Return exactly one short talking point per numbered moment as NUMBER|TEXT. Do not add an introduction.<|end|>")
            .AppendLine("<|user|>")
            .AppendLine("Write a natural setup or reaction prompt for each silent highlight using only the evidence given:");
        for (var index = 0; index < selected.Count; index++)
        {
            var candidate = selected[index];
            var reasons = string.Join("; ", candidate.Reasons.Take(4).Select(reason => reason.Detail));
            prompt.Append(index + 1)
                .Append(". time ")
                .Append(candidate.SourceIn.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture))
                .Append("s, evidence: ")
                .AppendLine(reasons);
        }
        return prompt.AppendLine("<|end|>").Append("<|assistant|>").ToString();
    }

    public static IReadOnlyList<NarrativeSuggestion> ParseResponse(
        string response,
        IReadOnlyList<HighlightCandidate> candidates)
    {
        var selected = SelectCandidates(candidates);
        var suggestions = new List<NarrativeSuggestion>();
        var seen = new HashSet<int>();
        foreach (var rawLine in response.Replace("<|end|>", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf('|');
            if (separator <= 0 || !int.TryParse(line[..separator].Trim().TrimEnd('.'), CultureInfo.InvariantCulture, out var number)) continue;
            var index = number - 1;
            var text = line[(separator + 1)..].Trim();
            if (index < 0 || index >= selected.Count || text.Length < 3 || !seen.Add(index)) continue;
            suggestions.Add(new NarrativeSuggestion(selected[index].Id, text.Length <= 240 ? text : text[..240].TrimEnd()));
        }
        return suggestions;
    }

    public static Task<IReadOnlyList<NarrativeSuggestion>> GenerateAsync(
        IReadOnlyList<HighlightCandidate> candidates,
        string modelDirectory,
        CancellationToken cancellationToken = default) => Task.Run(() => Generate(candidates, modelDirectory, cancellationToken), cancellationToken);

    private static IReadOnlyList<NarrativeSuggestion> Generate(
        IReadOnlyList<HighlightCandidate> candidates,
        string modelDirectory,
        CancellationToken cancellationToken)
    {
        var selected = SelectCandidates(candidates);
        if (selected.Count == 0) return [];
        using var model = new Model(Path.GetFullPath(modelDirectory));
        using var tokenizer = new Tokenizer(model);
        using var input = tokenizer.Encode(BuildPrompt(candidates));
        using var parameters = new GeneratorParams(model);
        parameters.SetSearchOption("max_length", Math.Min(2_048, input[0].Length + 256));
        parameters.SetSearchOption("do_sample", false);
        using var generator = new Generator(model, parameters);
        generator.AppendTokenSequences(input);
        while (!generator.IsDone())
        {
            cancellationToken.ThrowIfCancellationRequested();
            generator.GenerateNextToken();
        }
        var generated = generator.GetSequence(0);
        var output = tokenizer.Decode(generated[input[0].Length..]);
        return ParseResponse(output, candidates);
    }
}
