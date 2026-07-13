using System.Text.Json;
using Xunit;

namespace Muster.Cli.Tests;

[Collection("Console output tests")]
public class DiffCommandTests
{
    [Fact]
    public async Task Diff_reports_broken_fixture_between_trees()
    {
        var (baseData, fixtures) = TestRepoFactory.CreateTestRepo();
        var headData = TestRepoFactory.CopyWithReplacement(baseData, "value=\"20.0\"", "value=\"25.0\"");

        var originalOut = Console.Out;
        var stdout = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(stdout);
            exit = await Program.Main(["diff", "--base", baseData, "--head", headData, "--fixtures", fixtures]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(0, exit);
        var text = stdout.ToString();
        Assert.Contains("unit-costs-20", text, StringComparison.Ordinal);
        Assert.Contains("broke", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_reports_unchanged_when_base_and_head_are_identical()
    {
        var (baseData, fixtures) = TestRepoFactory.CreateTestRepo();
        var headData = TestRepoFactory.CopyWithReplacement(baseData, "does-not-exist", "unused");

        var originalOut = Console.Out;
        var stdout = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(stdout);
            exit = await Program.Main(["diff", "--base", baseData, "--head", headData, "--fixtures", fixtures]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(0, exit);
        var text = stdout.ToString();
        Assert.Contains("unit-costs-20", text, StringComparison.Ordinal);
        Assert.Contains("unchanged", text, StringComparison.Ordinal);
        Assert.DoesNotContain("broke", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_json_output_contains_both_run_reports_and_classified_rows()
    {
        var (baseData, fixtures) = TestRepoFactory.CreateTestRepo();
        var headData = TestRepoFactory.CopyWithReplacement(baseData, "value=\"20.0\"", "value=\"25.0\"");

        var originalOut = Console.Out;
        var stdout = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(stdout);
            exit = await Program.Main(["diff", "--base", baseData, "--head", headData, "--fixtures", fixtures, "--output", "json"]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("base", out var baseReport));
        Assert.True(root.TryGetProperty("head", out var headReport));
        Assert.Equal(1, baseReport.GetProperty("passed").GetInt32());
        Assert.Equal(1, headReport.GetProperty("failed").GetInt32());

        var rows = root.GetProperty("rows");
        Assert.Equal(1, rows.GetArrayLength());
        var row = rows[0];
        Assert.Equal("unit-costs-20", row.GetProperty("fixtureId").GetString());
        Assert.Equal("broke", row.GetProperty("classification").GetString());
    }

    [Fact]
    public async Task Diff_exits_2_when_base_dir_is_missing()
    {
        var (_, fixtures) = TestRepoFactory.CreateTestRepo();
        var (headData, _) = TestRepoFactory.CreateTestRepo();
        var exit = await Program.Main(["diff", "--base", "Z:\\nope", "--head", headData, "--fixtures", fixtures]);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Diff_exits_2_when_fixtures_dir_has_no_fixtures()
    {
        var (baseData, _) = TestRepoFactory.CreateTestRepo();
        var (headData, _) = TestRepoFactory.CreateTestRepo();
        var emptyFixtures = Directory.CreateTempSubdirectory("muster-e2e-empty-fixtures").FullName;
        var exit = await Program.Main(["diff", "--base", baseData, "--head", headData, "--fixtures", emptyFixtures]);
        Assert.Equal(2, exit);
    }
}
