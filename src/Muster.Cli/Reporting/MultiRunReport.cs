using System.Text.Json;
using Muster.Cli.Commands;
using Muster.Cli.Engines;

namespace Muster.Cli.Reporting;

/// <summary>
/// Aggregate result of running golden-roster fixtures across one or more engines.
/// </summary>
/// <param name="Governing">
/// Name of the engine whose results are authoritative (first match, in <c>--governing</c>
/// precedence order, among the engines that actually ran) — or <c>null</c> if none ran.
/// </param>
/// <param name="Unavailable">Names of requested engines that could not be probed as available.</param>
/// <param name="Runs">Per-engine <see cref="RunReport"/>s, one per engine that ran.</param>
public sealed record MultiRunReport(string? Governing, IReadOnlyList<string> Unavailable, IReadOnlyList<RunReport> Runs)
{
    /// <summary>
    /// Parses <paramref name="engineSpecs"/>, probes each for availability, runs fixtures for
    /// every available engine, and resolves the governing engine from <paramref name="governing"/>
    /// precedence (falling back to <see cref="EngineRegistry.DefaultGoverning"/> when empty).
    /// </summary>
    public static MultiRunReport Run(string dataDir, string fixturesDir,
        IReadOnlyList<string> engineSpecs, IReadOnlyList<string> governing)
    {
        var specs = EngineRegistry.ParseAll(engineSpecs);
        var unavailable = new List<string>();
        var runs = new List<RunReport>();
        foreach (var spec in specs)
        {
            if (!EngineRegistry.IsAvailable(spec))
            {
                unavailable.Add(spec.Name);
                continue;
            }

            runs.Add(TestCommand.RunFixtures(dataDir, fixturesDir, spec));
        }

        var precedence = governing.Count > 0 ? governing : EngineRegistry.DefaultGoverning;
        var governor = EngineRegistry.ResolveGoverning(precedence, [.. runs.Select(r => r.Engine)]);
        return new(governor, unavailable, runs);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string ToJson(MultiRunReport report) => JsonSerializer.Serialize(report, JsonOptions);

    public static void Write(MultiRunReport report, string mode, TextWriter writer)
    {
        if (mode == "json")
        {
            writer.WriteLine(ToJson(report));
            return;
        }

        foreach (var run in report.Runs)
        {
            writer.WriteLine(mode == "github-actions"
                ? $"### Engine: {run.Engine}{(run.Engine == report.Governing ? " (governing)" : "")}"
                : $"-- engine: {run.Engine}{(run.Engine == report.Governing ? " (governing)" : "")} --");
            RunReport.Write(run, mode, writer);
            writer.WriteLine();
        }

        foreach (var name in report.Unavailable)
        {
            writer.WriteLine(mode == "github-actions"
                ? $"> ⚠ engine `{name}` was requested but is unavailable in this environment."
                : $"[????] engine {name}: unavailable");
        }
    }
}
