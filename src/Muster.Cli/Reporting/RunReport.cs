using System.Globalization;
using System.Text.Json;

namespace Muster.Cli.Reporting;

/// <summary>
/// Result of evaluating a single golden-roster fixture.
/// </summary>
/// <param name="Id">Fixture spec id.</param>
/// <param name="Path">Path to the fixture file on disk.</param>
/// <param name="Passed">Whether the fixture passed (only meaningful when <see cref="Inconclusive"/> is false).</param>
/// <param name="Failures">Assertion/parse/crash failure messages.</param>
/// <param name="DurationMs">Wall-clock time spent evaluating the fixture, in milliseconds.</param>
/// <param name="Inconclusive">
/// True when the fixture could not be conclusively evaluated (parse error, unpopulated
/// data source, or an engine crash) rather than a genuine assertion failure.
/// </param>
public sealed record FixtureResult(
    string Id, string Path, bool Passed, string[] Failures, long DurationMs, bool Inconclusive);

/// <summary>
/// Aggregate result of a <c>muster test</c> run.
/// </summary>
public sealed record RunReport(
    string Engine, string DataDir, DateTime TimestampUtc,
    int Total, int Passed, int Failed, int Inconclusive, IReadOnlyList<FixtureResult> Fixtures)
{
    public static RunReport Create(string engine, string dataDir, IReadOnlyList<FixtureResult> results) =>
        new(engine, dataDir, DateTime.UtcNow, results.Count,
            results.Count(r => r.Passed),
            results.Count(r => !r.Passed && !r.Inconclusive),
            results.Count(r => r.Inconclusive),
            results);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string ToJson(RunReport r) => JsonSerializer.Serialize(r, JsonOptions);

    public static void Write(RunReport r, string mode, TextWriter writer)
    {
        switch (mode)
        {
            case "json":
                writer.WriteLine(ToJson(r));
                break;
            case "github-actions":
                WriteGitHubActions(r, writer);
                break;
            default: // summary
                WriteSummary(r, writer);
                break;
        }
    }

    private static void WriteSummary(RunReport r, TextWriter writer)
    {
        foreach (var f in r.Fixtures)
        {
            var status = f.Inconclusive ? "????" : f.Passed ? "PASS" : "FAIL";
            writer.WriteLine($"[{status}] {f.Id}");
        }

        var seconds = r.Fixtures.Sum(f => f.DurationMs) / 1000.0;
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Results: {r.Passed} passed, {r.Failed} failed, {r.Inconclusive} inconclusive ({seconds:0.0}s)"));
    }

    private static void WriteGitHubActions(RunReport r, TextWriter writer)
    {
        writer.WriteLine("## Muster — golden roster results");
        writer.WriteLine();
        writer.WriteLine(
            $"**{r.Passed} passed, {r.Failed} failed, {r.Inconclusive} inconclusive** ({r.Total} fixtures, engine `{r.Engine}`)");

        var nonPasses = r.Fixtures.Where(f => !f.Passed).ToList();
        if (nonPasses.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("| Fixture | Failures |");
            writer.WriteLine("| --- | --- |");
            foreach (var f in nonPasses)
            {
                var marker = f.Inconclusive ? "⚠" : "❌";
                var failures = string.Join(
                    "<br>", f.Failures.Select(x => x.Replace("|", "\\|", StringComparison.Ordinal)));
                writer.WriteLine($"| {marker} `{f.Id}` | {failures} |");
            }
        }
    }
}
