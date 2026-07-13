using System.Text.Json;
using Muster.Cli.Reporting;
using Xunit;

namespace Muster.Cli.Tests;

public class RunReportTests
{
    private static RunReport CreateSampleReport()
    {
        var fixtures = new List<FixtureResult>
        {
            new("fixture-pass", "/fixtures/fixture-pass.yaml", Passed: true, Failures: [], DurationMs: 12, Inconclusive: false),
            new("fixture-fail", "/fixtures/fixture-fail.yaml", Passed: false,
                Failures: ["Step 0: costCount: expected 1 but got 2"], DurationMs: 34, Inconclusive: false),
            new("fixture-inconclusive", "/fixtures/fixture-inconclusive.yaml", Passed: false,
                Failures: ["InvalidOperationException: malformed | data with a pipe"], DurationMs: 56, Inconclusive: true),
        };

        return RunReport.Create("wham", "/data", fixtures);
    }

    [Fact]
    public void Write_summary_mode_lists_each_fixture_status_and_totals()
    {
        var report = CreateSampleReport();
        var writer = new StringWriter();

        RunReport.Write(report, "summary", writer);

        var text = writer.ToString();
        Assert.Contains("[PASS] fixture-pass", text, StringComparison.Ordinal);
        Assert.Contains("[FAIL] fixture-fail", text, StringComparison.Ordinal);
        Assert.Contains("[????] fixture-inconclusive", text, StringComparison.Ordinal);
        Assert.Contains("Results: 1 passed, 1 failed, 1 inconclusive", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_json_mode_produces_parseable_camelCase_document_with_correct_counts()
    {
        var report = CreateSampleReport();
        var writer = new StringWriter();

        RunReport.Write(report, "json", writer);

        using var doc = JsonDocument.Parse(writer.ToString());
        var root = doc.RootElement;

        Assert.Equal("wham", root.GetProperty("engine").GetString());
        Assert.Equal(3, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("passed").GetInt32());
        Assert.Equal(1, root.GetProperty("failed").GetInt32());
        Assert.Equal(1, root.GetProperty("inconclusive").GetInt32());

        var fixturesArray = root.GetProperty("fixtures");
        Assert.Equal(3, fixturesArray.GetArrayLength());
        // camelCase property names, not PascalCase.
        Assert.True(fixturesArray[0].TryGetProperty("id", out _));
        Assert.True(fixturesArray[0].TryGetProperty("durationMs", out _));
        Assert.False(fixturesArray[0].TryGetProperty("Id", out _));
    }

    [Fact]
    public void ToJson_matches_Write_json_mode_output()
    {
        var report = CreateSampleReport();
        var writer = new StringWriter();
        RunReport.Write(report, "json", writer);

        // Write("json", ...) writes ToJson(r) plus a trailing newline.
        Assert.Equal(RunReport.ToJson(report) + writer.NewLine, writer.ToString());
    }

    [Fact]
    public void Write_github_actions_mode_has_heading_bold_counts_and_escaped_table_row()
    {
        var report = CreateSampleReport();
        var writer = new StringWriter();

        RunReport.Write(report, "github-actions", writer);

        var text = writer.ToString();
        Assert.Contains("## Muster — golden roster results", text, StringComparison.Ordinal);
        Assert.Contains("**1 passed, 1 failed, 1 inconclusive**", text, StringComparison.Ordinal);
        Assert.Contains("(3 fixtures, engine `wham`)", text, StringComparison.Ordinal);

        // Table only includes non-passing fixtures.
        Assert.Contains("| Fixture | Failures |", text, StringComparison.Ordinal);
        Assert.Contains("❌ `fixture-fail`", text, StringComparison.Ordinal);
        Assert.Contains("⚠ `fixture-inconclusive`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-pass`", text, StringComparison.Ordinal);

        // Pipe characters inside failure messages must be escaped so they don't break the table.
        Assert.Contains("malformed \\| data with a pipe", text, StringComparison.Ordinal);
    }
}
