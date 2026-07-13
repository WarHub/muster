using Muster.Cli.Converters;
using Muster.Cli.Reporting;

namespace Muster.Cli.Reports;

public enum VerdictKind { Confirmed, NotReproducible, NeedsInfo, Inconclusive }

public sealed record Verdict(VerdictKind Kind, bool EngineGap, IReadOnlyList<string> Labels, string? Detail);

/// <summary>
/// Maps roster conversion + per-engine fixture evaluation results to a <see cref="Verdict"/>.
/// </summary>
public static class VerdictMapper
{
    /// <param name="roster">
    /// The converted roster, or <see langword="null"/> when conversion failed or (in
    /// <paramref name="inlineSpec"/> mode) never produced one — an inline spec pasted by the
    /// reporter has no <see cref="ReplayRoster"/> of its own.
    /// </param>
    /// <param name="conversionError">Non-null when the roster/spec could not be converted or validated.</param>
    /// <param name="runs">Per-engine fixture evaluation results for the one generated/pasted spec.</param>
    /// <param name="inlineSpec">
    /// <see langword="true"/> when the report's roster source was an inline
    /// <c>```yaml steps block</c> — the YAML *is* the spec, so there is no
    /// <see cref="ReplayRoster"/> to check for a null value or unmapped nodes; the verdict
    /// is derived purely from <paramref name="runs"/>.
    /// </param>
    public static Verdict Map(ReplayRoster? roster, string? conversionError, MultiRunReport? runs, bool inlineSpec = false)
    {
        if (conversionError is not null)
            return Make(VerdictKind.NeedsInfo, false, conversionError);
        if (!inlineSpec)
        {
            if (roster is null)
                return Make(VerdictKind.NeedsInfo, false, "no roster found in the report");
            if (roster.Unmapped.Count > 0)
                return Make(VerdictKind.NeedsInfo, false, string.Join("\n", roster.Unmapped));
        }
        if (runs is null || runs.Runs.Count == 0 || runs.Governing is null)
            return Make(VerdictKind.Inconclusive, false, "no engine was available to evaluate the roster");

        var governing = runs.Runs.First(r => r.Engine == runs.Governing);
        var fixture = governing.Fixtures[0];

        var gap = runs.Runs.Count > 1 && runs.Runs
            .Select(r => (r.Fixtures[0].Passed, r.Fixtures[0].Inconclusive))
            .Distinct().Count() > 1;

        if (fixture.Inconclusive)
            return Make(VerdictKind.NeedsInfo, gap,
                "the roster could not be replayed against current data: " + fixture.Failures.FirstOrDefault());
        return fixture.Passed
            ? Make(VerdictKind.Confirmed, gap, null)
            : Make(VerdictKind.NotReproducible, gap, string.Join("\n", fixture.Failures));
    }

    private static Verdict Make(VerdictKind kind, bool gap, string? detail)
    {
        var labels = new List<string>
        {
            kind switch
            {
                VerdictKind.Confirmed => "confirmed",
                VerdictKind.NotReproducible => "not-reproducible",
                VerdictKind.NeedsInfo => "needs-info",
                _ => "inconclusive",
            },
        };
        if (gap) labels.Add("engine-gap");
        return new(kind, gap, labels, detail);
    }
}
