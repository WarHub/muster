using Muster.Cli.Commands;
using Xunit;

namespace Muster.Cli.Tests;

[Collection("Console output tests")]
public class ReportCommandTests
{
    private static string GreenFixtureYaml(string fixturesDir) =>
        File.ReadAllText(Path.Combine(fixturesDir, "unit-costs-20.yaml"));

    private static string WriteIssueBody(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".md");
        File.WriteAllText(path, content);
        return path;
    }

    private static string NewOutDir() => Directory.CreateTempSubdirectory("muster-report-out-").FullName;

    private static async Task<int> RunCaptured(
        string issueBodyPath, string dataDir, string outDir,
        string dataSource = "local:.", string[]? engines = null, string[]? governing = null)
    {
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            return await ReportCommand.Run(
                new FileInfo(issueBodyPath), new DirectoryInfo(dataDir), dataSource,
                engines ?? [], governing ?? [], new DirectoryInfo(outDir), TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task Inline_spec_with_matching_pins_is_confirmed()
    {
        var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();
        var yaml = GreenFixtureYaml(fixturesDir);
        var issueBody = $"""
            ### Roster

            ```yaml
            {yaml}
            ```

            ### Problem

            Test Unit seems to cost more than it should.

            ### Expected

            Test Unit should cost less.
            """;
        var bodyPath = WriteIssueBody(issueBody);
        var outDir = NewOutDir();

        var exit = await RunCaptured(bodyPath, dataDir, outDir);

        Assert.Equal(0, exit);
        var reply = await File.ReadAllTextAsync(Path.Combine(outDir, "reply.md"), TestContext.Current.CancellationToken);
        Assert.StartsWith("<!-- muster:report -->", reply, StringComparison.Ordinal);
        Assert.Contains("Verdict: Confirmed", reply, StringComparison.Ordinal);
        Assert.Contains("<!-- muster:snapshot -->", reply, StringComparison.Ordinal);
        Assert.Contains("```yaml", reply, StringComparison.Ordinal);
        Assert.Contains("Test Unit seems to cost more", reply, StringComparison.Ordinal);

        var json = await File.ReadAllTextAsync(Path.Combine(outDir, "report.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"verdict\": \"confirmed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"engineGap\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"governing\": \"wham\"", json, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(outDir, "snapshot.yaml")));
    }

    [Fact]
    public async Task Inline_spec_with_mismatched_pins_is_not_reproducible()
    {
        var (dataDir, fixturesDir) = TestRepoFactory.CreateTestRepo();
        var yaml = GreenFixtureYaml(fixturesDir).Replace("value: 20", "value: 999", StringComparison.Ordinal);
        var issueBody = $"""
            ```yaml
            {yaml}
            ```
            """;
        var bodyPath = WriteIssueBody(issueBody);
        var outDir = NewOutDir();

        var exit = await RunCaptured(bodyPath, dataDir, outDir);

        Assert.Equal(0, exit);
        var reply = await File.ReadAllTextAsync(Path.Combine(outDir, "reply.md"), TestContext.Current.CancellationToken);
        Assert.Contains("Verdict: Not reproducible", reply, StringComparison.Ordinal);

        var json = await File.ReadAllTextAsync(Path.Combine(outDir, "report.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"verdict\": \"not-reproducible\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Garbage_body_is_needs_info_and_exits_zero()
    {
        var (dataDir, _) = TestRepoFactory.CreateTestRepo();
        var bodyPath = WriteIssueBody("this issue has no roster, no yaml, nothing usable at all.");
        var outDir = NewOutDir();

        var exit = await RunCaptured(bodyPath, dataDir, outDir);

        Assert.Equal(0, exit);
        var reply = await File.ReadAllTextAsync(Path.Combine(outDir, "reply.md"), TestContext.Current.CancellationToken);
        Assert.StartsWith("<!-- muster:report -->", reply, StringComparison.Ordinal);
        Assert.Contains("Verdict: Needs info", reply, StringComparison.Ordinal);
        Assert.Contains("New Recruit share link", reply, StringComparison.Ordinal);
        Assert.Contains("no roster found", reply, StringComparison.Ordinal);

        var json = await File.ReadAllTextAsync(Path.Combine(outDir, "report.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"verdict\": \"needs-info\"", json, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(outDir, "snapshot.yaml")));
    }

    [Fact]
    public async Task Invalid_inline_yaml_is_needs_info_and_exits_zero()
    {
        var (dataDir, _) = TestRepoFactory.CreateTestRepo();
        var issueBody = """
            ```yaml
            steps:
              - action: notAnAction
                thisIsNot: validSpecYaml: [
            ```
            """;
        var bodyPath = WriteIssueBody(issueBody);
        var outDir = NewOutDir();

        var exit = await RunCaptured(bodyPath, dataDir, outDir);

        Assert.Equal(0, exit);
        var json = await File.ReadAllTextAsync(Path.Combine(outDir, "report.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"verdict\": \"needs-info\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_data_dir_exits_2()
    {
        var bodyPath = WriteIssueBody("no roster here");
        var outDir = NewOutDir();

        var exit = await RunCaptured(bodyPath, Path.Combine(Path.GetTempPath(), "no-such-muster-data-dir"), outDir);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Missing_issue_body_file_exits_2()
    {
        var (dataDir, _) = TestRepoFactory.CreateTestRepo();
        var outDir = NewOutDir();

        var exit = await RunCaptured(Path.Combine(Path.GetTempPath(), "no-such-issue-body.md"), dataDir, outDir);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Unwritable_out_dir_exits_2_without_throwing()
    {
        var (dataDir, _) = TestRepoFactory.CreateTestRepo();
        var bodyPath = WriteIssueBody("no roster here");

        // A file already occupies the path we'll ask ReportCommand to create as a directory —
        // Directory.CreateDirectory must fail, and that failure must be caught (exit 2), not thrown.
        var collidingPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(collidingPath, "occupied");

        var exit = await RunCaptured(bodyPath, dataDir, collidingPath);

        Assert.Equal(2, exit);
    }
}
