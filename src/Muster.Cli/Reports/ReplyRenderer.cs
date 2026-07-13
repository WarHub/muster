using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Muster.Cli.Converters;
using Muster.Cli.Reporting;

namespace Muster.Cli.Reports;

/// <summary>
/// Renders a <see cref="Verdict"/> plus its evidence (roster, per-engine runs, the generated
/// spec) as a markdown GitHub issue-comment reply.
/// </summary>
/// <remarks>
/// The first line and the snapshot markers are consumed downstream by Task 11's
/// SnapshotExtractor and Task 12's find-comment step — their exact text is load-bearing and
/// must not be reformatted.
/// </remarks>
public static partial class ReplyRenderer
{
    private const int MaxQuoteLength = 500;

    // Matches RosterRunner.AssertEqual's roster-level cost failure wording:
    // "Step {n}: cost[{typeId-or-name}].value: expected {x} but got {y}".
    [GeneratedRegex(@"cost\[(?<key>[^\]]+)\]\.value:\s*expected\s*(?<exp>-?[\d.]+)\s*but got\s*(?<act>-?[\d.]+)")]
    private static partial Regex CostFailurePattern();

    public static string Render(
        Verdict verdict, ReplayRoster? roster, MultiRunReport? runs, string specYaml, string? problem, string? expected,
        bool carriedForwardSnapshot = false)
    {
        var sb = new StringBuilder();
        sb.Append("<!-- muster:report -->\n");
        sb.Append('\n');
        sb.Append($"## Verdict: {Heading(verdict.Kind)}\n");
        sb.Append('\n');
        sb.Append(Explanation(verdict, runs));
        sb.Append('\n');
        sb.Append('\n');

        if (verdict.Kind == VerdictKind.NeedsInfo)
        {
            AppendNeedsInfo(sb, verdict);
        }

        if (roster is not null && roster.ObservedTotals.Count > 0 && runs is { Runs.Count: > 0 })
        {
            AppendMatrix(sb, roster, runs);
        }

        AppendQuoted(sb, "Problem", problem);
        AppendQuoted(sb, "Expected", expected);

        if (roster is { BooksRevisions.Count: > 0 })
        {
            sb.Append($"reported against: {string.Join(", ", roster.BooksRevisions)} — evaluated against current data\n");
            sb.Append('\n');
        }

        // Never emit an empty ```yaml block: when there is no spec at all (neither freshly
        // derived nor carried forward from a previous evaluation), omit the whole snapshot
        // section rather than posting an empty, misleading <details> block.
        if (specYaml.Length > 0)
        {
            if (carriedForwardSnapshot)
            {
                sb.Append("> ⚠ The roster source is no longer reachable — snapshot preserved from a previous evaluation.\n");
                sb.Append('\n');
            }

            sb.Append("<details><summary>Executable spec (snapshot)</summary>\n");
            sb.Append("<!-- muster:snapshot -->\n");
            sb.Append('\n');
            sb.Append("```yaml\n");
            sb.Append(specYaml);
            if (!specYaml.EndsWith('\n'))
            {
                sb.Append('\n');
            }
            sb.Append("```\n");
            sb.Append("</details>\n");
        }

        return sb.ToString();
    }

    private static string Heading(VerdictKind kind) => kind switch
    {
        VerdictKind.Confirmed => "Confirmed",
        VerdictKind.NotReproducible => "Not reproducible",
        VerdictKind.NeedsInfo => "Needs info",
        _ => "Inconclusive",
    };

    private static string Explanation(Verdict verdict, MultiRunReport? runs)
    {
        var ran = runs?.Runs.Select(r => r.Engine).ToList() ?? [];
        var unavailable = runs?.Unavailable ?? [];

        var parts = new List<string>
        {
            verdict.Kind switch
            {
                VerdictKind.Confirmed => "The reported values reproduce against current data.",
                VerdictKind.NotReproducible => "The reported values do not reproduce against current data.",
                VerdictKind.NeedsInfo => "More information is needed to evaluate this report.",
                _ => "No engine was available to evaluate this report.",
            },
        };
        if (runs?.Governing is { } governing)
            parts.Add($"Governing engine: `{governing}`.");
        if (ran.Count > 0)
            parts.Add($"Engines run: {string.Join(", ", ran.Select(e => $"`{e}`"))}.");
        if (unavailable.Count > 0)
            parts.Add($"Unavailable: {string.Join(", ", unavailable.Select(e => $"`{e}`"))}.");

        return string.Join(" ", parts);
    }

    private static void AppendNeedsInfo(StringBuilder sb, Verdict verdict)
    {
        sb.Append("I couldn't fully evaluate this report");
        if (!string.IsNullOrEmpty(verdict.Detail))
        {
            sb.Append(": ");
            sb.Append('\n');
            sb.Append('\n');
            sb.Append($"> {Truncate(verdict.Detail).Replace("\n", "\n> ", StringComparison.Ordinal)}\n");
        }
        else
        {
            sb.Append(".\n");
        }
        sb.Append('\n');
        sb.Append("Please provide the roster in one of these formats:\n");
        sb.Append('\n');
        sb.Append("- a New Recruit share link (`https://www.newrecruit.eu/app/list/...`)\n");
        sb.Append("- a `.ros`/`.rosz` roster file attachment\n");
        sb.Append("- an inline fenced `yaml` code block containing a `steps:` list\n");
        sb.Append('\n');
    }

    private static void AppendMatrix(StringBuilder sb, ReplayRoster roster, MultiRunReport runs)
    {
        var ranEngines = runs.Runs.Select(r => r.Engine).ToList();

        sb.Append($"| Value | reported | {string.Join(" | ", ranEngines)} |\n");
        sb.Append($"| --- | --- | {string.Join(" | ", ranEngines.Select(_ => "---"))} |\n");
        foreach (var cost in roster.ObservedTotals)
        {
            var cells = runs.Runs.Select(r => EngineCell(r, cost));
            sb.Append($"| {Escape(cost.Name)} | {cost.Value.ToString(CultureInfo.InvariantCulture)} | {string.Join(" | ", cells)} |\n");
        }
        sb.Append('\n');
    }

    private static string EngineCell(RunReport run, ReplayCost cost)
    {
        if (run.Fixtures.Count == 0)
            return "differs ✖";

        var fixture = run.Fixtures[0];
        if (fixture.Passed)
            return "reproduced ✔";

        foreach (var failure in fixture.Failures)
        {
            var m = CostFailurePattern().Match(failure);
            if (m.Success && string.Equals(m.Groups["key"].Value, cost.TypeId, StringComparison.Ordinal))
                return Escape(m.Groups["act"].Value);
        }

        return "differs ✖";
    }

    private static void AppendQuoted(StringBuilder sb, string label, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        sb.Append($"**{label}:**\n");
        sb.Append('\n');
        var truncated = Truncate(text);
        foreach (var line in truncated.Split('\n'))
        {
            sb.Append($"> {line.TrimEnd('\r')}\n");
        }
        sb.Append('\n');
    }

    private static string Truncate(string s) => s.Length > MaxQuoteLength ? s[..MaxQuoteLength] : s;

    private static string Escape(string s) => s.Replace("|", "\\|", StringComparison.Ordinal);
}
