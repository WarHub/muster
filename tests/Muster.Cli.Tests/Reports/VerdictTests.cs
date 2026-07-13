using Muster.Cli.Converters;
using Muster.Cli.Reports;
using Muster.Cli.Reporting;
using Xunit;

namespace Muster.Cli.Tests.Reports;

public class VerdictTests
{
    private static ReplayRoster Roster(params string[] unmapped) => new(
        "r", "gs", [new("pts", "pt-1", 100m)], [],
        [new("fe-1", "cat-1", [], [])], [.. unmapped]);

    private static MultiRunReport Runs(params (string Engine, bool Passed, bool Inconclusive)[] engines) => new(
        Governing: engines.Length > 0 ? engines[0].Engine : null,
        Unavailable: [],
        Runs: [.. engines.Select(e => RunReport.Create(e.Engine, "data",
            [new FixtureResult("spec", "p", e.Passed, e.Passed ? [] : ["Step 3: cost pts: expected 100 but got 95"], 1, e.Inconclusive)]))]);

    [Fact]
    public void Conversion_error_is_needs_info()
    {
        var v = VerdictMapper.Map(null, "list not found", null);
        Assert.Equal(VerdictKind.NeedsInfo, v.Kind);
        Assert.Contains("needs-info", v.Labels);
    }

    [Fact]
    public void Unmapped_nodes_force_needs_info()
    {
        var v = VerdictMapper.Map(Roster("selection 'X' missing option_id"), null, Runs(("wham", true, false)));
        Assert.Equal(VerdictKind.NeedsInfo, v.Kind);
    }

    [Fact]
    public void Governing_pass_is_confirmed()
    {
        var v = VerdictMapper.Map(Roster(), null, Runs(("wham", true, false)));
        Assert.Equal(VerdictKind.Confirmed, v.Kind);
        Assert.Contains("confirmed", v.Labels);
        Assert.False(v.EngineGap);
    }

    [Fact]
    public void Governing_assertion_failure_is_not_reproducible()
    {
        var v = VerdictMapper.Map(Roster(), null, Runs(("wham", false, false)));
        Assert.Equal(VerdictKind.NotReproducible, v.Kind);
        Assert.Contains("expected 100 but got 95", v.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_crash_is_needs_info_not_notreproducible()
    {
        var v = VerdictMapper.Map(Roster(), null, Runs(("wham", false, true)));
        Assert.Equal(VerdictKind.NeedsInfo, v.Kind);
    }

    [Fact]
    public void Engine_disagreement_raises_engine_gap()
    {
        var v = VerdictMapper.Map(Roster(), null, Runs(("newrecruit", true, false), ("wham", false, false)));
        Assert.Equal(VerdictKind.Confirmed, v.Kind); // newrecruit governs
        Assert.True(v.EngineGap);
        Assert.Contains("engine-gap", v.Labels);
    }

    [Fact]
    public void No_engines_ran_is_inconclusive()
    {
        var runs = new MultiRunReport(null, ["newrecruit"], []);
        var v = VerdictMapper.Map(Roster(), null, runs);
        Assert.Equal(VerdictKind.Inconclusive, v.Kind);
    }

    [Fact]
    public void Inline_spec_with_no_roster_and_no_error_skips_roster_checks()
    {
        // inlineSpec=true: the pasted YAML has no ReplayRoster of its own, so a null roster
        // and no conversion error must NOT be treated as needs-info — the verdict comes
        // purely from the run results.
        var v = VerdictMapper.Map(null, null, Runs(("wham", true, false)), inlineSpec: true);
        Assert.Equal(VerdictKind.Confirmed, v.Kind);
    }

    [Fact]
    public void Inline_spec_conversion_error_is_still_needs_info()
    {
        var v = VerdictMapper.Map(null, "the inline spec is not valid: bad yaml", null, inlineSpec: true);
        Assert.Equal(VerdictKind.NeedsInfo, v.Kind);
    }
}
