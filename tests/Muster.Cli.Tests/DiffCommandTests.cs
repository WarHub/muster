using System.Text.Json;
using Muster.Cli.Commands;
using Xunit;

namespace Muster.Cli.Tests;

[Collection("Console output tests")]
public class DiffCommandTests
{
    private static string TestAdapterDll => TestPaths.TestAdapterDll;

    private static int RunDiff(
        string baseDir, string headDir, string fixturesDir, string output,
        bool failOnBroke = false, string[]? engines = null, string[]? governing = null)
    {
        var originalOut = Console.Out;
        var stdout = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(stdout);
            exit = DiffCommand.Run(
                new DirectoryInfo(baseDir), new DirectoryInfo(headDir), new DirectoryInfo(fixturesDir),
                output, failOnBroke, engines ?? [], governing ?? []);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return exit;
    }

    private static string CaptureDiffOutput(
        string baseDir, string headDir, string fixturesDir, string output,
        bool failOnBroke = false, string[]? engines = null, string[]? governing = null)
    {
        var originalOut = Console.Out;
        var stdout = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            DiffCommand.Run(
                new DirectoryInfo(baseDir), new DirectoryInfo(headDir), new DirectoryInfo(fixturesDir),
                output, failOnBroke, engines ?? [], governing ?? []);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return stdout.ToString();
    }

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

        var diffs = root.GetProperty("diffs");
        Assert.Equal(1, diffs.GetArrayLength());
        var wham = diffs[0];
        Assert.Equal("wham", wham.GetProperty("engine").GetString());

        var rows = wham.GetProperty("rows");
        Assert.Equal(1, rows.GetArrayLength());
        var row = rows[0];
        Assert.Equal("unit-costs-20", row.GetProperty("fixtureId").GetString());
        Assert.Equal("broke", row.GetProperty("classification").GetString());
    }

    [Fact]
    public void FailOnBroke_exits_1_when_governing_engine_broke()
    {
        var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();
        var headData = TestRepoFactory.CopyWithReplacement(dataDir, "20", "25"); // breaks the pts=20 pin

        var exit = RunDiff(dataDir, headData, fixturesDir, "markdown",
            failOnBroke: true, engines: ["wham"], governing: ["wham"]);

        Assert.Equal(1, exit);
    }

    [Fact]
    public void FailOnBroke_ignores_non_governing_break()
    {
        var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();
        var headData = TestRepoFactory.CopyWithReplacement(dataDir, "20", "25");
        // fake adapter always computes pts=20 → fixture passes both sides for 'fake';
        // wham (non-governing) breaks; check must stay green but report the gap.
        var exit = RunDiff(dataDir, headData, fixturesDir, "markdown",
            failOnBroke: true,
            engines: [$"fake=dotnet:{TestAdapterDll}", "wham"],
            governing: ["fake", "wham"]);

        Assert.Equal(0, exit);
    }

    [Fact]
    public void Head_status_disagreement_reports_engine_gap()
    {
        var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();
        var headData = TestRepoFactory.CopyWithReplacement(dataDir, "20", "25");

        var output = CaptureDiffOutput(dataDir, headData, fixturesDir, "markdown",
            engines: [$"fake=dotnet:{TestAdapterDll}", "wham"], governing: ["fake"]);

        Assert.Contains("engine-gap", output, StringComparison.Ordinal);
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
