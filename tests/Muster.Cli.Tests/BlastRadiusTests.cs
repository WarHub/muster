using Muster.Cli.Reporting;
using Xunit;

namespace Muster.Cli.Tests;

public class BlastRadiusTests
{
    private static FixtureResult Result(string id, bool passed, string[]? failures = null, bool inconclusive = false) =>
        new(id, $"/fixtures/{id}.yaml", passed, failures ?? [], DurationMs: 1, inconclusive);

    private static RunReport Report(params FixtureResult[] fixtures) =>
        RunReport.Create("wham", "/data", fixtures);

    [Fact]
    public void Pass_to_pass_is_unchanged()
    {
        var baseRun = Report(Result("f1", passed: true));
        var headRun = Report(Result("f1", passed: true));

        var rows = BlastRadius.Classify(baseRun, headRun);

        Assert.Equal("unchanged", Assert.Single(rows).Classification);
    }

    [Fact]
    public void Pass_to_fail_is_broke()
    {
        var baseRun = Report(Result("f1", passed: true));
        var headRun = Report(Result("f1", passed: false, ["expected 20 but got 25"]));

        var rows = BlastRadius.Classify(baseRun, headRun);

        var row = Assert.Single(rows);
        Assert.Equal("broke", row.Classification);
        Assert.Equal("pass", row.BaseStatus);
        Assert.Equal("fail", row.HeadStatus);
    }

    [Fact]
    public void Fail_to_pass_is_fixed()
    {
        var baseRun = Report(Result("f1", passed: false, ["expected 20 but got 25"]));
        var headRun = Report(Result("f1", passed: true));

        var rows = BlastRadius.Classify(baseRun, headRun);

        Assert.Equal("fixed", Assert.Single(rows).Classification);
    }

    [Fact]
    public void Fail_to_fail_with_same_failure_text_is_still_failing()
    {
        var baseRun = Report(Result("f1", passed: false, ["expected 20 but got 25"]));
        var headRun = Report(Result("f1", passed: false, ["expected 20 but got 25"]));

        var rows = BlastRadius.Classify(baseRun, headRun);

        Assert.Equal("still-failing", Assert.Single(rows).Classification);
    }

    [Fact]
    public void Fail_to_fail_with_different_failure_text_is_verdict_changed()
    {
        var baseRun = Report(Result("f1", passed: false, ["expected 20 but got 25"]));
        var headRun = Report(Result("f1", passed: false, ["expected 20 but got 30"]));

        var rows = BlastRadius.Classify(baseRun, headRun);

        Assert.Equal("verdict-changed", Assert.Single(rows).Classification);
    }

    [Fact]
    public void Base_side_inconclusive_yields_inconclusive_regardless_of_head_status()
    {
        var baseRun = Report(Result("f1", passed: false, ["harness error"], inconclusive: true));
        var headRun = Report(Result("f1", passed: true));

        var rows = BlastRadius.Classify(baseRun, headRun);

        var row = Assert.Single(rows);
        Assert.Equal("inconclusive", row.Classification);
        Assert.Equal("inconclusive", row.BaseStatus);
        Assert.Equal("pass", row.HeadStatus);
    }

    [Fact]
    public void Head_side_inconclusive_yields_inconclusive_regardless_of_base_status()
    {
        var baseRun = Report(Result("f1", passed: true));
        var headRun = Report(Result("f1", passed: false, ["harness error"], inconclusive: true));

        var rows = BlastRadius.Classify(baseRun, headRun);

        Assert.Equal("inconclusive", Assert.Single(rows).Classification);
    }

    [Fact]
    public void Fixture_missing_from_head_yields_inconclusive_with_missing_head_status()
    {
        var baseRun = Report(Result("f1", passed: true));
        var headRun = Report();

        var rows = BlastRadius.Classify(baseRun, headRun);

        var row = Assert.Single(rows);
        Assert.Equal("inconclusive", row.Classification);
        Assert.Equal("pass", row.BaseStatus);
        Assert.Equal("missing", row.HeadStatus);
    }

    [Fact]
    public void Fixture_missing_from_base_yields_inconclusive_with_missing_base_status()
    {
        var baseRun = Report();
        var headRun = Report(Result("f1", passed: true));

        var rows = BlastRadius.Classify(baseRun, headRun);

        var row = Assert.Single(rows);
        Assert.Equal("inconclusive", row.Classification);
        Assert.Equal("missing", row.BaseStatus);
        Assert.Equal("pass", row.HeadStatus);
    }

    [Fact]
    public void Classify_preserves_base_fixture_order_and_appends_head_only_fixtures()
    {
        var baseRun = Report(Result("f2", passed: true), Result("f1", passed: true));
        var headRun = Report(Result("f1", passed: true), Result("f3", passed: true));

        var rows = BlastRadius.Classify(baseRun, headRun);

        Assert.Equal(["f2", "f1", "f3"], rows.Select(r => r.FixtureId));
    }
}
