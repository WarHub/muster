using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BattleScribeSpec;
using BattleScribeSpec.Roster;
using Muster.Cli.Engines;
using Muster.Cli.Fixtures;

namespace Muster.Cli.Reports;

/// <summary>
/// Re-pins a promoted bug-report snapshot spec to the roster engine's current values: drops any
/// stale <c>expectedState</c>-only assertion steps, replays the remaining action steps against
/// the local data repo, and re-emits the spec with a fresh <c>expectedState</c> block pinned to
/// the observed final <see cref="RosterState"/>.
/// </summary>
public static partial class SpecRePinner
{
    // The comment SpecEmitter (Task 9/10) writes immediately above its observed-value pin step —
    // load-bearing for RewriteWithPins' text-surgery: everything from this line to EOF in a
    // muster-emitted snapshot is exactly the trailing expectedState-only step, so dropping from
    // here is a deterministic way to strip stale pins without a full YAML re-emit.
    //
    // Line-anchored (^\s*...$ with Multiline) so that a quoted scalar field (e.g. an
    // attacker-controlled roster customName) containing this text mid-line can never match: a
    // quoted step field sits inline within a `key: "..."` line, never as a standalone comment
    // line, and SpecEmitter.Quote escapes embedded newlines so it can't fake one either.
    [GeneratedRegex(@"^\s*# observed values from the report.*$", RegexOptions.Multiline)]
    private static partial Regex ObservedValuesMarkerPattern();

    [GeneratedRegex(@"^id:.*$", RegexOptions.Multiline)]
    private static partial Regex IdLinePattern();

    /// <summary>
    /// Loads <paramref name="specYaml"/>, replays its action steps (assertion-only steps are
    /// dropped — they may pin stale/buggy values) against <paramref name="dataDir"/> using
    /// <paramref name="engineSpec"/>, and returns the spec's YAML text re-pinned to the engine's
    /// current final <see cref="RosterState"/> under <paramref name="newSpecId"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The data source is not populated locally, or the replay itself fails (a fixture that
    /// can't run can't be promoted) — never returns a spec built from a partial/failed replay.
    /// </exception>
    public static string RePin(string specYaml, string dataDir, EngineSpec engineSpec, string newSpecId)
    {
        var spec = SpecLoader.LoadFromYaml(specYaml, defaultId: newSpecId);
        if (spec.Setup.DataSource is { Length: > 0 } ds && !RepoDataSourceResolver.IsPopulatedFor(dataDir, ds))
        {
            throw new InvalidOperationException($"data source not populated locally: {ds}");
        }

        // Strip assertion-only steps — they may pin stale/buggy values from the original report.
        var replaySteps = spec.Steps.Where(s => s.Action is not null).ToList();
        var replaySpec = new SpecFile
        {
            Id = spec.Id,
            Category = spec.Category,
            Description = spec.Description,
            Tags = spec.Tags,
            Engines = spec.Engines,
            Setup = spec.Setup,
            Steps = replaySteps,
        };

        RosterState? finalState = null;
        using var engine = EngineRegistry.CreateEngine(engineSpec);
        var runner = new RosterRunner(engine, RepoDataSourceResolver.Create(dataDir), engineName: engineSpec.Name)
        {
            OnStepCompleted = (_, _, state, _) => finalState = state,
        };
        var result = runner.Run(replaySpec);
        if (result.HarnessError is not null || !result.Passed || finalState is null)
        {
            throw new InvalidOperationException(
                "cannot promote: replay failed against current data: "
                + (result.HarnessError ?? result.Failures.FirstOrDefault() ?? "no state captured"));
        }

        return RewriteWithPins(specYaml, newSpecId, finalState.Costs, engineSpec.Name);
    }

    /// <summary>
    /// Text-level rewrite of <paramref name="specYaml"/>: replaces the top-level <c>id:</c> line
    /// with <paramref name="newSpecId"/>, drops any existing observed-value pin block (recognized
    /// by <see cref="ObservedValuesMarkerPattern"/>; falls back to append-only when absent), and appends
    /// a fresh <c>expectedState.costs</c> block pinned to <paramref name="costs"/>. The result is
    /// validated with <see cref="SpecLoader.LoadFromYaml(string, string?)"/> before being
    /// returned — a rewrite that fails to parse is a harness error, never a silently written
    /// broken fixture.
    /// </summary>
    internal static string RewriteWithPins(
        string specYaml, string newSpecId, IReadOnlyList<CostState> costs, string engineName)
    {
        var text = IdLinePattern().Replace(specYaml, $"id: {Quote(newSpecId)}", count: 1);

        var markerMatch = ObservedValuesMarkerPattern().Match(text);
        if (markerMatch.Success)
        {
            text = text[..markerMatch.Index];
        }

        text = text.TrimEnd('\n', '\r') + "\n";

        var sb = new StringBuilder(text);
        sb.AppendLine();
        sb.AppendLine($"  # expected values pinned at promotion (engine: {engineName})");
        sb.AppendLine("  - expectedState:");
        sb.AppendLine("      costs:");
        foreach (var cost in costs)
        {
            sb.AppendLine($"        - typeId: {Quote(cost.TypeId)}");
            sb.AppendLine($"          value: {cost.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        var rewritten = sb.ToString();

        // Never write a broken fixture: validate the rewrite round-trips before returning it.
        SpecLoader.LoadFromYaml(rewritten);

        return rewritten;
    }

    private static string Quote(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
