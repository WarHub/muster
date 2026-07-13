using System.Globalization;
using System.Text.Json;

namespace Muster.Cli.Reporting;

/// <summary>
/// Per-fixture classification of how its result differs between a base and head
/// <see cref="RunReport"/> — the "blast radius" of a data-tree change.
/// </summary>
/// <param name="FixtureId">Fixture spec id.</param>
/// <param name="BaseStatus"><c>pass</c>, <c>fail</c>, <c>inconclusive</c>, or <c>missing</c> (fixture absent from that run).</param>
/// <param name="HeadStatus">Same vocabulary as <paramref name="BaseStatus"/>, for the head run.</param>
/// <param name="Classification">
/// One of <c>unchanged</c>, <c>broke</c>, <c>fixed</c>, <c>still-failing</c>,
/// <c>verdict-changed</c>, or <c>inconclusive</c>.
/// </param>
/// <param name="BaseFailures">Failure messages from the base run (empty when passed).</param>
/// <param name="HeadFailures">Failure messages from the head run (empty when passed).</param>
public sealed record BlastRow(
    string FixtureId,
    string BaseStatus,
    string HeadStatus,
    string Classification,
    IReadOnlyList<string> BaseFailures,
    IReadOnlyList<string> HeadFailures);

/// <summary>
/// Combined payload for <c>muster diff --output json</c>: both runs plus the classified rows.
/// </summary>
public sealed record DiffReport(RunReport Base, RunReport Head, IReadOnlyList<BlastRow> Rows);

/// <summary>
/// Per-engine blast radius rows for one engine in a <see cref="MultiDiffReport"/>.
/// </summary>
/// <param name="Engine">Engine name.</param>
/// <param name="Rows">This engine's classified fixture rows (base run vs head run).</param>
public sealed record EngineDiff(string Engine, IReadOnlyList<BlastRow> Rows);

/// <summary>
/// Combined payload for <c>muster diff</c> across every engine that ran on both base and head.
/// </summary>
/// <param name="Governing">Name of the governing engine (see <see cref="MultiRunReport.Governing"/>), or <c>null</c>.</param>
/// <param name="Unavailable">Names of engines that did not run on both sides (unavailable on either, or only present on one side).</param>
/// <param name="Diffs">Per-engine classified rows, one <see cref="EngineDiff"/> per engine that ran on both sides.</param>
/// <param name="EngineGaps">
/// Fixture ids where engines that ran on both sides disagree on head status — a signal of
/// engine divergence that the governing engine's verdict does not resolve.
/// </param>
public sealed record MultiDiffReport(
    string? Governing,
    IReadOnlyList<string> Unavailable,
    IReadOnlyList<EngineDiff> Diffs,
    IReadOnlyList<string> EngineGaps);

/// <summary>
/// Compares two <see cref="RunReport"/>s (same fixtures, different data trees) and classifies
/// each fixture's change in outcome — the blast radius of the base-to-head diff.
/// </summary>
public static class BlastRadius
{
    /// <summary>
    /// Classifies every fixture present in either <paramref name="baseRun"/> or
    /// <paramref name="headRun"/>. Pure and side-effect free.
    /// </summary>
    /// <remarks>
    /// Classification is three-valued, not boolean: pass→pass is <c>unchanged</c>,
    /// pass→fail is <c>broke</c>, fail→pass is <c>fixed</c>, fail→fail with identical
    /// failure text is <c>still-failing</c>, fail→fail with different failure text is
    /// <c>verdict-changed</c>, and any side being inconclusive (or the fixture missing
    /// from one side) always yields <c>inconclusive</c> regardless of the other side's status.
    /// </remarks>
    public static IReadOnlyList<BlastRow> Classify(RunReport baseRun, RunReport headRun)
    {
        ArgumentNullException.ThrowIfNull(baseRun);
        ArgumentNullException.ThrowIfNull(headRun);

        var headById = headRun.Fixtures.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var baseIds = new HashSet<string>(baseRun.Fixtures.Select(f => f.Id), StringComparer.Ordinal);

        var rows = new List<BlastRow>();
        foreach (var baseFixture in baseRun.Fixtures)
        {
            rows.Add(headById.TryGetValue(baseFixture.Id, out var headFixture)
                ? ClassifyPair(baseFixture, headFixture)
                : new BlastRow(baseFixture.Id, Status(baseFixture), "missing", "inconclusive", baseFixture.Failures, []));
        }

        foreach (var headFixture in headRun.Fixtures.Where(f => !baseIds.Contains(f.Id)))
        {
            rows.Add(new BlastRow(headFixture.Id, "missing", Status(headFixture), "inconclusive", [], headFixture.Failures));
        }

        return rows;
    }

    /// <summary>
    /// Pairs engines that ran on both <paramref name="baseRuns"/> and <paramref name="headRuns"/>
    /// by name and classifies each pair's fixtures via <see cref="Classify"/>. Engines present on
    /// only one side (unavailable, or ran on only base/head) are reported in
    /// <see cref="MultiDiffReport.Unavailable"/> and excluded from rows and gating.
    /// </summary>
    public static MultiDiffReport ClassifyMulti(MultiRunReport baseRuns, MultiRunReport headRuns)
    {
        ArgumentNullException.ThrowIfNull(baseRuns);
        ArgumentNullException.ThrowIfNull(headRuns);

        var baseByEngine = baseRuns.Runs.ToDictionary(r => r.Engine, StringComparer.Ordinal);
        var headByEngine = headRuns.Runs.ToDictionary(r => r.Engine, StringComparer.Ordinal);

        var unavailable = new List<string>(baseRuns.Unavailable.Union(headRuns.Unavailable, StringComparer.Ordinal));
        var diffs = new List<EngineDiff>();
        foreach (var (engine, baseRun) in baseByEngine)
        {
            if (!headByEngine.TryGetValue(engine, out var headRun)) { unavailable.Add(engine); continue; }
            diffs.Add(new(engine, Classify(baseRun, headRun)));
        }

        foreach (var engine in headByEngine.Keys.Except(baseByEngine.Keys, StringComparer.Ordinal))
            unavailable.Add(engine);

        // engine-gap: fixtures whose HEAD status differs across engines that ran both sides
        var gaps = new List<string>();
        if (diffs.Count > 1)
        {
            var byFixture = diffs
                .SelectMany(d => d.Rows.Select(r => (r.FixtureId, r.HeadStatus)))
                .GroupBy(x => x.FixtureId, StringComparer.Ordinal);
            foreach (var g in byFixture)
                if (g.Select(x => x.HeadStatus).Distinct(StringComparer.Ordinal).Count() > 1)
                    gaps.Add(g.Key);
        }

        return new(headRuns.Governing, unavailable, diffs, gaps);
    }

    private static BlastRow ClassifyPair(FixtureResult baseFixture, FixtureResult headFixture)
    {
        var baseStatus = Status(baseFixture);
        var headStatus = Status(headFixture);

        if (baseFixture.Inconclusive || headFixture.Inconclusive)
        {
            return new BlastRow(baseFixture.Id, baseStatus, headStatus, "inconclusive", baseFixture.Failures, headFixture.Failures);
        }

        var classification = (baseFixture.Passed, headFixture.Passed) switch
        {
            (true, true) => "unchanged",
            (true, false) => "broke",
            (false, true) => "fixed",
            (false, false) => baseFixture.Failures.SequenceEqual(headFixture.Failures, StringComparer.Ordinal)
                ? "still-failing"
                : "verdict-changed",
        };

        return new BlastRow(baseFixture.Id, baseStatus, headStatus, classification, baseFixture.Failures, headFixture.Failures);
    }

    private static string Status(FixtureResult f) => f.Inconclusive ? "inconclusive" : f.Passed ? "pass" : "fail";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string ToJson(RunReport baseRun, RunReport headRun, IReadOnlyList<BlastRow> rows) =>
        JsonSerializer.Serialize(new DiffReport(baseRun, headRun, rows), JsonOptions);

    public static string ToJson(MultiDiffReport report) => JsonSerializer.Serialize(report, JsonOptions);

    public static void Write(RunReport baseRun, RunReport headRun, IReadOnlyList<BlastRow> rows, string mode, TextWriter writer)
    {
        switch (mode)
        {
            case "json":
                writer.WriteLine(ToJson(baseRun, headRun, rows));
                break;
            default: // markdown
                WriteMarkdown(baseRun, headRun, rows, writer);
                break;
        }
    }

    /// <summary>
    /// Writes a <see cref="MultiDiffReport"/>: <c>json</c> serializes the whole record; markdown
    /// renders one <c>### Engine: {name}</c> section per engine (reusing the single-engine
    /// markdown row/detail rendering), followed by unavailable-engine notices and, when
    /// <see cref="MultiDiffReport.EngineGaps"/> is non-empty, an <c>engine-gap</c> section.
    /// </summary>
    public static void WriteMulti(MultiDiffReport report, string mode, TextWriter writer)
    {
        if (mode == "json")
        {
            writer.WriteLine(ToJson(report));
            return;
        }

        foreach (var diff in report.Diffs)
        {
            writer.WriteLine($"### Engine: {diff.Engine}{(diff.Engine == report.Governing ? " (governing)" : "")}");
            writer.WriteLine();
            WriteMarkdownBody(diff.Rows, writer);
            writer.WriteLine();
        }

        foreach (var name in report.Unavailable)
        {
            writer.WriteLine($"> ⚠ engine `{name}` was requested but is unavailable, or only ran on one side.");
        }

        if (report.EngineGaps.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("### ⚠ engine-gap");
            writer.WriteLine(
                $"Engines disagree on the head state of: {string.Join(", ", report.EngineGaps.Select(g => $"`{g}`"))}. "
                + "The governing engine's verdict stands; divergence should be triaged as an engine defect.");
        }
    }

    private static void WriteMarkdown(RunReport baseRun, RunReport headRun, IReadOnlyList<BlastRow> rows, TextWriter writer)
    {
        writer.WriteLine("## Muster diff — blast radius");
        writer.WriteLine();
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Base: `{baseRun.DataDir}` — {baseRun.Passed} passed, {baseRun.Failed} failed, {baseRun.Inconclusive} inconclusive"));
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Head: `{headRun.DataDir}` — {headRun.Passed} passed, {headRun.Failed} failed, {headRun.Inconclusive} inconclusive"));
        writer.WriteLine();
        WriteMarkdownBody(rows, writer);
    }

    private static void WriteMarkdownBody(IReadOnlyList<BlastRow> rows, TextWriter writer)
    {
        writer.WriteLine("| Fixture | Base | Head | Change |");
        writer.WriteLine("| --- | --- | --- | --- |");
        foreach (var row in rows)
        {
            writer.WriteLine($"| `{row.FixtureId}` | {row.BaseStatus} | {row.HeadStatus} | {DisplayClassification(row.Classification)} |");
        }

        var detailRows = rows.Where(r => r.Classification is "broke" or "verdict-changed").ToList();
        if (detailRows.Count == 0)
        {
            return;
        }

        writer.WriteLine();
        writer.WriteLine("### Failure detail");
        foreach (var row in detailRows)
        {
            writer.WriteLine();
            writer.WriteLine($"**{row.FixtureId}** ({DisplayClassification(row.Classification)})");
            if (row.BaseFailures.Count > 0)
            {
                writer.WriteLine("- base: " + string.Join("; ", row.BaseFailures));
            }

            if (row.HeadFailures.Count > 0)
            {
                writer.WriteLine("- head: " + string.Join("; ", row.HeadFailures));
            }
        }
    }

    private static string DisplayClassification(string classification) => classification switch
    {
        "broke" => "broke ❌",
        "fixed" => "fixed ✅",
        "inconclusive" => "inconclusive ⚠",
        _ => classification,
    };
}
